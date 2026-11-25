using System.IO;
using DiscordShield.Core;
using DiscordShield.UI;
using Xunit;

namespace DiscordShield.Tests;

public sealed class HistorySnoozeUpdateTests
{
    [Fact]
    public async Task HistoryStore_PersistsAcrossReload_CapsMaxEntries_AndSkipsCorruptLines()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"screenshield-hist-{Guid.NewGuid():N}.jsonl");
        try
        {
            var t0 = new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);
            using (var store = new HistoryStore(tempFile, maxEntries: 60, loadExisting: false))
            {
                store.Record(HistoryEventType.Screenshot, "Upwork", "SS #1 (shot.png) · Shield ACTIVE · 0 blocked exposed", t0);
                store.Record(HistoryEventType.BlockAllow, "Discord", "Changed: Allowed -> Blocked", t0.AddMinutes(1));
                store.Record(HistoryEventType.Setting, "Cooldown", "15s -> 30s", t0.AddMinutes(2));
                await store.FlushAsync();
            }

            // Inject a corrupt line to verify resilient reload
            await File.AppendAllTextAsync(tempFile, "{broken-json-line\n");

            using var reloaded = new HistoryStore(tempFile, maxEntries: 60, loadExisting: true);
            Assert.Equal(3, reloaded.Count);

            var screenshots = reloaded.Query(HistoryFilterCategory.Screenshots);
            Assert.Single(screenshots);
            Assert.Equal("Upwork", screenshots[0].Target);

            var changes = reloaded.Query(HistoryFilterCategory.Changes);
            Assert.Equal(2, changes.Count);

            // Sort by Target ascending
            var byTargetAsc = reloaded.Query(sortBy: HistorySortColumn.Target, ascending: true);
            Assert.Equal(["Cooldown", "Discord", "Upwork"], byTargetAsc.Select(e => e.Target).ToArray());

            // Sort by Target descending
            var byTargetDesc = reloaded.Query(sortBy: HistorySortColumn.Target, ascending: false);
            Assert.Equal(["Upwork", "Discord", "Cooldown"], byTargetDesc.Select(e => e.Target).ToArray());
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void HistoryStore_ExportCsv_EscapesCommasAndQuotesPerRfc4180()
    {
        var entries = new[]
        {
            new HistoryEntry(
                new DateTimeOffset(2026, 11, 24, 15, 30, 5, TimeSpan.Zero),
                HistoryEventType.Screenshot,
                "Upwork",
                "SS #1, \"quoted\" · 0 blocked exposed")
        };

        string csv = HistoryStore.ExportCsv(entries);
        Assert.Contains("Date,Time,Type,Target,Details", csv);
        Assert.Contains("\"SS #1, \"\"quoted\"\" · 0 blocked exposed\"", csv);
    }

    [Fact]
    public async Task AppUpdateService_DetectsUpToDateAndAvailableBySha256Digest()
    {
        string fakeExe = Path.Combine(Path.GetTempPath(), $"screenshield-fake-{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllBytesAsync(fakeExe, new byte[1024]); // < 5 MB -> Lite binary
            string realHash = AppUpdateService.ComputeSha256Hex(fakeExe);

            string sameJson = $$"""
                {
                  "tag_name": "v2.1.0",
                  "assets": [
                    {
                      "name": "ScreenShield-Lite.exe",
                      "browser_download_url": "https://github.com/example/ScreenShield-Lite.exe",
                      "digest": "sha256:{{realHash}}"
                    }
                  ]
                }
                """;
            var upToDateSvc = new AppUpdateService(_ => Task.FromResult(sameJson), () => fakeExe);
            UpdateCheckResult r1 = await upToDateSvc.CheckForUpdateAsync();
            Assert.False(r1.UpdateAvailable);
            Assert.Equal("ScreenShield-Lite.exe", r1.AssetName);

            string newerJson = """
                {
                  "tag_name": "v2.1.0",
                  "assets": [
                    {
                      "name": "ScreenShield-Lite.exe",
                      "browser_download_url": "https://github.com/example/ScreenShield-Lite.exe",
                      "digest": "sha256:DEADBEEF1234567890"
                    }
                  ]
                }
                """;
            var newerSvc = new AppUpdateService(_ => Task.FromResult(newerJson), () => fakeExe);
            UpdateCheckResult r2 = await newerSvc.CheckForUpdateAsync();
            Assert.True(r2.UpdateAvailable);
            Assert.Equal("DEADBEEF12", r2.RemoteHashShort);
        }
        finally
        {
            if (File.Exists(fakeExe)) File.Delete(fakeExe);
        }
    }

    [Fact]
    public void MainForm_SnoozeProtection_PausesShieldLogsHistoryAndAutoResumes()
    {
        Exception? ex = null;
        var sta = new Thread(() =>
        {
            try
            {
                using var history = new HistoryStore(filePath: null, loadExisting: false);
                var settings = new AppSettings { ExcludeWrapperFromCapture = true };
                using var form = new MainForm(
                    settings,
                    new CaptureExclusionService(),
                    new Win32WindowInspector(),
                    new UpworkAutomationHelper(),
                    taskbarShield: new TaskbarShieldService(() => false, _ => true),
                    initialPalette: DesignTokens.Dark,
                    autoRefreshOnShow: false,
                    historyStore: history);
                form.CreateControl();

                Assert.False(form.IsSnoozed);
                form.SnoozeProtection(TimeSpan.FromMinutes(15));
                Assert.True(form.IsSnoozed);
                Assert.False(settings.ExcludeWrapperFromCapture);
                Assert.Contains(history.Query(HistoryFilterCategory.Changes), e => e.Type == HistoryEventType.Snooze && e.Details.Contains("15 min"));

                form.ResumeFromSnooze(expired: true);
                Assert.False(form.IsSnoozed);
                Assert.True(settings.ExcludeWrapperFromCapture);
                Assert.Contains(history.Query(HistoryFilterCategory.Changes), e => e.Type == HistoryEventType.Snooze && e.Details.Contains("automatically re-armed"));
            }
            catch (Exception e) { ex = e; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(TimeSpan.FromSeconds(10));
        Assert.Null(ex);
    }
}
