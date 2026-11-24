using DiscordShield.Core;
using Xunit;

namespace DiscordShield.Tests;

public sealed class CatalogAndTaskbarTests
{
    private static readonly ScreenSnapshot Primary = new(@"\\.\DISPLAY1", true, new Rectangle(0, 0, 1920, 1080));

    [Fact]
    public void BuildCatalog_DiscoversActiveAppsAndBrowserSites_AndAutoAllowsUnblocked()
    {
        var settings = new AppSettings
        {
            ProtectedProcessNames = ["Discord"],
            BlockedDomains = ["discord.com"]
        };

        var windows = new[]
        {
            new WindowSnapshot(new IntPtr(1), 101, "chrome", "WhatsApp - Google Chrome", true, false, new Rectangle(0, 0, 1200, 800)),
            new WindowSnapshot(new IntPtr(2), 102, "msedge", "Discord | #general - Microsoft Edge", true, false, new Rectangle(0, 0, 1200, 800)),
            new WindowSnapshot(new IntPtr(3), 103, "Code", "ScreenShield - Visual Studio Code", true, false, new Rectangle(0, 0, 1200, 800)),
            new WindowSnapshot(new IntPtr(4), 999, "ScreenShield", "ScreenShield", true, false, new Rectangle(0, 0, 1000, 700))
        };

        IReadOnlyList<CatalogEntry> all = ActiveCatalogService.BuildCatalog(windows, settings, selfProcessId: 999);
        IReadOnlyList<CatalogEntry> allowed = ActiveCatalogService.Filter(all, ShieldFilterTab.Allowed, null);
        IReadOnlyList<CatalogEntry> blocked = ActiveCatalogService.Filter(all, ShieldFilterTab.Blocked, null);

        Assert.Contains(allowed, e => e.Key == "Code" && e.Kind == ShieldItemKind.App);
        Assert.Contains(allowed, e => e.Key == "whatsapp.com" && e.Kind == ShieldItemKind.Site);
        Assert.Contains(blocked, e => e.Key == "discord.com" && e.Kind == ShieldItemKind.Site);
        Assert.Contains(blocked, e => e.Key == "Discord" && e.Kind == ShieldItemKind.App);
        Assert.DoesNotContain(all, e => e.Key == "ScreenShield");
    }

    [Fact]
    public void BulkBlockAndAllow_AndCustomDomainNormalization_WorkCleanly()
    {
        var settings = new AppSettings { ProtectedProcessNames = [], BlockedDomains = [] };

        int added = ActiveCatalogService.BulkSetBlocked(settings,
        [
            (ShieldItemKind.App, "Spotify.exe"),
            (ShieldItemKind.Site, "https://www.Instagram.com/direct/inbox/"),
            (ShieldItemKind.Site, "whatsapp.com")
        ], block: true);

        Assert.Equal(3, added);
        Assert.Contains("Spotify", settings.ProtectedProcessNames);
        Assert.Contains("instagram.com", settings.BlockedDomains);
        Assert.Contains("whatsapp.com", settings.BlockedDomains);

        int removed = ActiveCatalogService.BulkSetBlocked(settings,
        [
            (ShieldItemKind.Site, "instagram.com")
        ], block: false);

        Assert.Equal(1, removed);
        Assert.DoesNotContain("instagram.com", settings.BlockedDomains);
    }

    [Fact]
    public void WindowGuardEngine_DetectsBlockedBrowserSiteAndSignalsTaskbarHide()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings
        {
            ProtectedProcessNames = [],
            BlockedDomains = ["whatsapp.com"],
            AlwaysHideTaskbarWhenBlockedOpen = true,
            OnlyActWhenUpworkRunning = true
        };

        var chromeWhatsapp = new WindowSnapshot(
            new IntPtr(20),
            400,
            "chrome",
            "WhatsApp Web - Google Chrome",
            true,
            false,
            new Rectangle(100, 100, 1000, 700));

        GuardEvaluation eval = engine.Evaluate(
            [chromeWhatsapp],
            ["chrome", "Upwork"],
            [Primary],
            settings,
            DateTimeOffset.UtcNow);

        Assert.True(eval.UpworkRunning);
        Assert.Single(eval.ExposedNativeWindows);
        Assert.True(eval.ShouldHideTaskbar);
    }

    [Fact]
    public void TaskbarShieldService_IsIdempotentAndRestoresOriginalUserState()
    {
        bool osAutoHide = false;
        int setCalls = 0;

        using var svc = new TaskbarShieldService(
            getState: () => osAutoHide,
            setState: next =>
            {
                osAutoHide = next;
                setCalls++;
                return true;
            });

        Assert.True(svc.ApplyTaskbarShield(true));
        Assert.True(svc.ApplyTaskbarShield(true)); // Idempotent — no extra OS call
        Assert.True(osAutoHide);
        Assert.Equal(1, setCalls);

        svc.Dispose(); // Must restore user's original false state
        Assert.False(osAutoHide);
        Assert.Equal(2, setCalls);
    }

    [Fact]
    public void TaskbarShieldService_DoesNotUnhideIfUserAlreadyHadAutoHideEnabled()
    {
        bool osAutoHide = true;
        int setCalls = 0;

        using var svc = new TaskbarShieldService(
            getState: () => osAutoHide,
            setState: next =>
            {
                osAutoHide = next;
                setCalls++;
                return true;
            });

        Assert.True(svc.ApplyTaskbarShield(true));
        svc.Dispose();
        Assert.True(osAutoHide);
        Assert.Equal(0, setCalls);
    }

    [Fact]
    public void MainForm_AddCustomBlockedSite_ImmediatelyMarksMatchingActiveBrowserRowBlocked()
    {
        Exception? ex = null;
        var sta = new Thread(() =>
        {
            try
            {
                var settings = new AppSettings { ProtectedProcessNames = [], BlockedDomains = [] };
                using var form = new DiscordShield.UI.MainForm(
                    settings,
                    new CaptureExclusionService(),
                    new Win32WindowInspector(),
                    new UpworkAutomationHelper(),
                    taskbarShield: new TaskbarShieldService(() => false, _ => true),
                    initialPalette: DiscordShield.UI.DesignTokens.Dark,
                    autoRefreshOnShow: false);
                form.CreateControl();

                var windows = new[]
                {
                    new WindowSnapshot(new IntPtr(3), 103, "msedge", "Figma — Product Design - Microsoft Edge", true, false, new Rectangle(0, 0, 1200, 800))
                };
                form.LoadCatalogSnapshot(windows, includeBrowserUia: false);

                Assert.Contains(form.VisibleCatalogEntries, e => e.Kind == ShieldItemKind.Site && !e.IsBlocked);

                form.AddCustomBlockedSite("figma.com");

                Assert.Contains(form.VisibleCatalogEntries, e => e.Kind == ShieldItemKind.Site && e.Detail.Contains("Figma") && e.IsBlocked);
            }
            catch (Exception e) { ex = e; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(TimeSpan.FromSeconds(10));
        Assert.Null(ex);
    }
}
