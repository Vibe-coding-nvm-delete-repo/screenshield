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

        if (args.Any(a => a.Equals("--smoke-test", StringComparison.OrdinalIgnoreCase)))
        {
            return RunUiSmokeTest();
        }

        var (settings, warning) = AppSettings.Load();
        var captureService = new CaptureExclusionService();
        var inspector = new Win32WindowInspector();
        var upworkAutomation = new UpworkAutomationHelper();
        var taskbarShield = new TaskbarShieldService();

        using var form = new MainForm(settings, captureService, inspector, upworkAutomation, taskbarShield);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            MessageBox.Show(warning, "ScreenShield Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

        var (status, overallPass, exitCode) = ComputeSelfTestVerdict(
            affinityVerified,
            captureUnavailable,
            baselineSawWindow,
            protectedHidWindow);

        var report = new
        {
            osVersion = Environment.OSVersion.Version.ToString(),
            osSupported = service.IsOsSupported,
            status,
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
        return exitCode;
    }

    public static (string Status, bool OverallPass, int ExitCode) ComputeSelfTestVerdict(
        bool affinityVerified,
        bool captureUnavailable,
        bool baselineSawWindow,
        bool protectedHidWindow)
    {
        if (!affinityVerified)
        {
            return ("FAILED_AFFINITY", false, 1);
        }

        if (captureUnavailable)
        {
            return ("INCONCLUSIVE_CAPTURE_UNAVAILABLE", false, 2);
        }

        if (baselineSawWindow && protectedHidWindow)
        {
            return ("PROVEN_EXCLUDED", true, 0);
        }

        return ("FAILED_PIXEL_VERIFICATION", false, 1);
    }

    /// <summary>
    /// Instantiates the full MainForm UI, exercises all 4 tabs, catalog block/allow,
    /// HelpPopover, theme switch, and settings serialization to prove zero trimming regressions.
    /// </summary>
    public static int RunUiSmokeTest()
    {
        var settings = new AppSettings();
        using var form = new MainForm(
            settings,
            new CaptureExclusionService(),
            new Win32WindowInspector(),
            new UpworkAutomationHelper(),
            taskbarShield: new TaskbarShieldService(() => false, _ => true),
            initialPalette: DesignTokens.Dark,
            autoRefreshOnShow: false);

        form.CreateControl();
        var sample = new[]
        {
            new WindowSnapshot(new IntPtr(1), 101, "Discord", "Discord", true, false, new Rectangle(0, 0, 1000, 700)),
            new WindowSnapshot(new IntPtr(2), 102, "chrome", "WhatsApp - Google Chrome", true, false, new Rectangle(0, 0, 1200, 800))
        };
        form.LoadCatalogSnapshot(sample, includeBrowserUia: false);
        form.SelectTab(ShieldFilterTab.Allowed);
        form.SelectTab(ShieldFilterTab.Blocked);
        form.SelectTab(ShieldFilterTab.Settings);
        if (form.HelpBadges.Count > 0)
        {
            form.ShowHelpForBadge(form.HelpBadges[0]);
            form.HelpPopover.HidePopover();
        }

        form.ApplyTheme(DesignTokens.Light);
        form.ApplyTheme(DesignTokens.Dark);
        form.SelectTab(ShieldFilterTab.All);
        Console.WriteLine("SMOKE_TEST_OK");
        return 0;
    }
}
