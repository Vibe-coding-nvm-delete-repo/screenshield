using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using DiscordShield.Native;

namespace DiscordShield.Core;

public sealed record AffinityResult(
    bool Success,
    uint RequestedAffinity,
    uint ActualAffinity,
    int LastWin32Error,
    string Message);

public sealed record CaptureVerificationResult(
    bool Passed,
    bool AffinityConfirmed,
    int SentinelPixelsFound,
    int TotalPixelsSampled,
    string Summary,
    bool CaptureUnavailable = false);

public interface ICaptureExclusionService
{
    bool IsOsSupported { get; }
    AffinityResult EnableExcludeFromCapture(IntPtr hwnd);
    AffinityResult DisableExcludeFromCapture(IntPtr hwnd);
    AffinityResult ReadAffinity(IntPtr hwnd);
    CaptureVerificationResult VerifyRegionExcludesColor(Rectangle screenBounds, Color sentinelColor, int colorTolerance = 8);
}

public sealed class CaptureExclusionService : ICaptureExclusionService
{
    public bool IsOsSupported => Win32Interop.IsExcludeFromCaptureSupported();

    public AffinityResult EnableExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return new AffinityResult(false, Win32Interop.WDA_EXCLUDEFROMCAPTURE, 0, 0, "Window handle is not created.");
        }

        if (!IsOsSupported)
        {
            return new AffinityResult(
                false,
                Win32Interop.WDA_EXCLUDEFROMCAPTURE,
                0,
                0,
                $"Windows 10 build 19041+ required for WDA_EXCLUDEFROMCAPTURE (current: {Environment.OSVersion.Version}).");
        }

        bool setOk = Win32Interop.SetWindowDisplayAffinity(hwnd, Win32Interop.WDA_EXCLUDEFROMCAPTURE);
        int setErr = setOk ? 0 : Marshal.GetLastWin32Error();

        bool readOk = Win32Interop.GetWindowDisplayAffinity(hwnd, out uint actual);
        int readErr = readOk ? 0 : Marshal.GetLastWin32Error();

        bool verified = setOk && readOk && actual == Win32Interop.WDA_EXCLUDEFROMCAPTURE;
        string message = verified
            ? "WDA_EXCLUDEFROMCAPTURE applied and verified by readback."
            : $"Failed to apply/verify WDA_EXCLUDEFROMCAPTURE (setOk={setOk}, setErr={setErr}, readOk={readOk}, readErr={readErr}, actual=0x{actual:X}).";

        return new AffinityResult(verified, Win32Interop.WDA_EXCLUDEFROMCAPTURE, actual, setErr != 0 ? setErr : readErr, message);
    }

    public AffinityResult DisableExcludeFromCapture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return new AffinityResult(false, Win32Interop.WDA_NONE, 0, 0, "Window handle is not created.");
        }

        bool setOk = Win32Interop.SetWindowDisplayAffinity(hwnd, Win32Interop.WDA_NONE);
        int setErr = setOk ? 0 : Marshal.GetLastWin32Error();

        bool readOk = Win32Interop.GetWindowDisplayAffinity(hwnd, out uint actual);
        int readErr = readOk ? 0 : Marshal.GetLastWin32Error();

        bool verified = setOk && readOk && actual == Win32Interop.WDA_NONE;
        string message = verified
            ? "Capture exclusion disabled (WDA_NONE verified)."
            : $"Failed to disable capture exclusion (setOk={setOk}, setErr={setErr}, readOk={readOk}, actual=0x{actual:X}).";

        return new AffinityResult(verified, Win32Interop.WDA_NONE, actual, setErr != 0 ? setErr : readErr, message);
    }

    public AffinityResult ReadAffinity(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return new AffinityResult(false, 0, 0, 0, "Window handle is not created.");
        }

        bool ok = Win32Interop.GetWindowDisplayAffinity(hwnd, out uint actual);
        int err = ok ? 0 : Marshal.GetLastWin32Error();
        return new AffinityResult(ok, actual, actual, err, ok ? $"Current affinity: 0x{actual:X}" : $"GetWindowDisplayAffinity failed (err={err}).");
    }

    public CaptureVerificationResult VerifyRegionExcludesColor(Rectangle screenBounds, Color sentinelColor, int colorTolerance = 8)
    {
        if (screenBounds.Width <= 0 || screenBounds.Height <= 0)
        {
            return new CaptureVerificationResult(false, false, 0, 0, "Invalid capture region bounds.");
        }

        using var bitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppArgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(screenBounds.Location, Point.Empty, screenBounds.Size, CopyPixelOperation.SourceCopy);
        }
        catch (Win32Exception ex)
        {
            return new CaptureVerificationResult(
                Passed: false,
                AffinityConfirmed: false,
                SentinelPixelsFound: 0,
                TotalPixelsSampled: 0,
                Summary: $"Desktop screen capture unavailable right now (screen may be locked or session non-interactive; Win32 error {ex.NativeErrorCode}: {ex.Message}).",
                CaptureUnavailable: true);
        }

        var (matches, total) = CountMatchingPixels(bitmap, sentinelColor, colorTolerance);
        bool passed = total > 0 && matches == 0;
        string summary = passed
            ? $"PASS: 0/{total:N0} sentinel pixels leaked into Windows screenshot capture."
            : $"FAIL: {matches:N0}/{total:N0} sentinel pixels were visible in Windows screenshot capture.";

        return new CaptureVerificationResult(passed, true, matches, total, summary, CaptureUnavailable: false);
    }

    public static (int Matches, int Total) CountMatchingPixels(Bitmap bitmap, Color target, int tolerance)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        var rect = new Rectangle(0, 0, width, height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int byteCount = Math.Abs(data.Stride) * height;
            byte[] buffer = new byte[byteCount];
            Marshal.Copy(data.Scan0, buffer, 0, byteCount);

            int matches = 0;
            int total = width * height;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * data.Stride;
                for (int x = 0; x < width; x++)
                {
                    int idx = rowOffset + (x * 4);
                    byte b = buffer[idx];
                    byte g = buffer[idx + 1];
                    byte r = buffer[idx + 2];

                    if (Math.Abs(r - target.R) <= tolerance
                        && Math.Abs(g - target.G) <= tolerance
                        && Math.Abs(b - target.B) <= tolerance)
                    {
                        matches++;
                    }
                }
            }

            return (matches, total);
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }
}
