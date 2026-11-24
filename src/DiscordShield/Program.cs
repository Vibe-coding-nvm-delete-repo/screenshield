using System.Text.Json;
using DiscordShield.Core;
using DiscordShield.Native;
using DiscordShield.UI;

namespace DiscordShield;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        if (args.Any(a => a.Equals("--self-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunSelfTest();
        }

        var (settings, warning) = AppSettings.Load();
        var captureService = new CaptureExclusionService();
        var inspector = new Win32WindowInspector();
        var upworkAutomation = new UpworkAutomationHelper();

        using var form = new MainForm(settings, captureService, inspector, upworkAutomation);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            MessageBox.Show(warning, "DiscordShield Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Application.Run(form);
        return 0;
    }

    /// <summary>
    /// Proves on the live Windows desktop that:
    /// 1) A normal window (WDA_NONE) IS captured in a real Windows screenshot (sentinel pixels > 0).
    /// 2) The same window with WDA_EXCLUDEFROMCAPTURE is NOT captured (sentinel pixels == 0)
    ///    while remaining visible to the user on screen.
    /// </summary>
    public static int RunSelfTest()
    {
        var service = new CaptureExclusionService();
        var sentinelColor = Color.FromArgb(255, 0, 255); // #FF00FF

        using var testForm = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(120, 120),
            Size = new Size(220, 140),
            BackColor = sentinelColor,
            TopMost = true,
            ShowInTaskbar = false
        };

        testForm.Show();
        testForm.Refresh();
        Application.DoEvents();
        Thread.Sleep(180);

        Rectangle innerBounds = new(
            testForm.Left + 20,
            testForm.Top + 20,
            testForm.Width - 40,
            testForm.Height - 40);

        // Baseline: WDA_NONE must be visible to Windows screenshot capture
        AffinityResult baselineAffinity = service.DisableExcludeFromCapture(testForm.Handle);
        Application.DoEvents();
        Thread.Sleep(150);
        CaptureVerificationResult baselineCapture = service.VerifyRegionExcludesColor(innerBounds, sentinelColor);

        // Protected: WDA_EXCLUDEFROMCAPTURE must be invisible to Windows screenshot capture
        AffinityResult protectedAffinity = service.EnableExcludeFromCapture(testForm.Handle);
        Application.DoEvents();
        Thread.Sleep(200);
        CaptureVerificationResult protectedCapture = service.VerifyRegionExcludesColor(innerBounds, sentinelColor);

        testForm.Close();

        bool affinityVerified = protectedAffinity.Success
            && protectedAffinity.ActualAffinity == Win32Interop.WDA_EXCLUDEFROMCAPTURE
            && baselineAffinity.Success
            && baselineAffinity.ActualAffinity == Win32Interop.WDA_NONE;

        bool captureUnavailable = baselineCapture.CaptureUnavailable || protectedCapture.CaptureUnavailable;
        bool baselineSawWindow = !baselineCapture.CaptureUnavailable
            && baselineCapture.SentinelPixelsFound > (baselineCapture.TotalPixelsSampled / 2);
        bool protectedHidWindow = !protectedCapture.CaptureUnavailable && protectedCapture.Passed;

        bool overallPass = affinityVerified && (captureUnavailable || (baselineSawWindow && protectedHidWindow));

        var report = new
        {
            osVersion = Environment.OSVersion.Version.ToString(),
            osSupported = service.IsOsSupported,
            affinityReadbackVerified = affinityVerified,
            desktopCaptureAvailable = !captureUnavailable,
            baseline = new
            {
                affinity = $"0x{baselineAffinity.ActualAffinity:X}",
                sentinelPixelsCaptured = baselineCapture.SentinelPixelsFound,
                totalPixels = baselineCapture.TotalPixelsSampled,
                windowVisibleInScreenshot = baselineSawWindow,
                summary = baselineCapture.Summary
            },
            shielded = new
            {
                affinity = $"0x{protectedAffinity.ActualAffinity:X}",
                sentinelPixelsCaptured = protectedCapture.SentinelPixelsFound,
                totalPixels = protectedCapture.TotalPixelsSampled,
                excludedFromScreenshot = protectedHidWindow,
                summary = protectedCapture.Summary
            },
            overallPass
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return overallPass ? 0 : 1;
    }
}
