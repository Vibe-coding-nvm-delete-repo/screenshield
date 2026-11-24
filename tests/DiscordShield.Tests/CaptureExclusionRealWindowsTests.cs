using DiscordShield.Core;
using DiscordShield.Native;
using Xunit;

namespace DiscordShield.Tests;

public sealed class CaptureExclusionRealWindowsTests
{
    [Fact]
    public void CountMatchingPixels_DetectsExactSentinelColor()
    {
        using var bmp = new Bitmap(20, 20);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(30, 31, 34));
            using var brush = new SolidBrush(Color.Magenta);
            g.FillRectangle(brush, 0, 0, 10, 10);
        }

        var (matches, total) = CaptureExclusionService.CountMatchingPixels(bmp, Color.Magenta, tolerance: 4);
        Assert.Equal(400, total);
        Assert.Equal(100, matches);
    }

    [Fact]
    public void RealWindowsScreenshot_ProvesWdaExcludeFromCaptureHidesWindowFromBitBlt()
    {
        var service = new CaptureExclusionService();
        Assert.True(service.IsOsSupported, "Test requires Windows 10 2004+ / Windows 11.");

        Exception? threadEx = null;
        CaptureVerificationResult? baseline = null;
        CaptureVerificationResult? shielded = null;
        AffinityResult? affinity = null;

        var staThread = new Thread(() =>
        {
            try
            {
                Color sentinel = Color.FromArgb(255, 0, 255);
                using var form = new Form
                {
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(140, 140),
                    Size = new Size(200, 120),
                    BackColor = sentinel,
                    TopMost = true,
                    ShowInTaskbar = false
                };

                form.Show();
                form.Refresh();
                Application.DoEvents();
                Thread.Sleep(180);

                var inner = new Rectangle(form.Left + 20, form.Top + 20, form.Width - 40, form.Height - 40);

                // 1) Baseline (WDA_NONE): verify Windows screenshot DOES capture the magenta window
                _ = service.DisableExcludeFromCapture(form.Handle);
                Application.DoEvents();
                Thread.Sleep(150);
                baseline = service.VerifyRegionExcludesColor(inner, sentinel);

                // 2) Shielded (WDA_EXCLUDEFROMCAPTURE): verify Windows screenshot does NOT capture the window
                affinity = service.EnableExcludeFromCapture(form.Handle);
                Application.DoEvents();
                Thread.Sleep(200);
                shielded = service.VerifyRegionExcludesColor(inner, sentinel);

                form.Close();
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join(TimeSpan.FromSeconds(15));

        Assert.Null(threadEx);
        Assert.NotNull(baseline);
        Assert.NotNull(affinity);
        Assert.NotNull(shielded);

        // Always prove the live Windows DWM accepted and read back WDA_EXCLUDEFROMCAPTURE (0x11)
        Assert.True(affinity.Success, affinity.Message);
        Assert.Equal(Win32Interop.WDA_EXCLUDEFROMCAPTURE, affinity.ActualAffinity);

        if (!baseline.CaptureUnavailable && !shielded.CaptureUnavailable)
        {
            // Unlocked interactive desktop: prove baseline WAS captured and shielded was 0 pixels leaked
            Assert.True(
                baseline.SentinelPixelsFound > (baseline.TotalPixelsSampled * 0.8),
                $"Baseline screenshot did not see unshielded test window: {baseline.Summary}");

            Assert.True(shielded.Passed, shielded.Summary);
            Assert.Equal(0, shielded.SentinelPixelsFound);
        }
        else
        {
            // Locked / non-interactive desktop: verify fail-closed CaptureUnavailable reporting (no crash)
            Assert.False(shielded.Passed);
            Assert.Contains("unavailable", shielded.Summary, StringComparison.OrdinalIgnoreCase);
        }
    }
}
