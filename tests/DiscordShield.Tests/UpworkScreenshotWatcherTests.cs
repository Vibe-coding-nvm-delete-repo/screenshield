using System.IO;
using DiscordShield.Core;
using Xunit;

namespace DiscordShield.Tests;

public sealed class UpworkScreenshotWatcherTests
{
    [Fact]
    public void Watcher_DetectsNewScreenshotImageAndDebouncesRapidWrites()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"upwork-ss-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var detected = new List<UpworkScreenshotEvent>();
            using var signal = new ManualResetEventSlim(false);
            using var watcher = new UpworkScreenshotWatcher([dir], debounce: TimeSpan.FromSeconds(2));
            watcher.ScreenshotDetected += e =>
            {
                detected.Add(e);
                signal.Set();
            };

            Assert.True(watcher.IsWatchingAnyFolder);
            Assert.Equal("Last SS: none yet", watcher.FormatLastScreenshot(DateTimeOffset.UtcNow));

            // Non-image file must be ignored
            File.WriteAllText(Path.Combine(dir, "tracker.log"), "hello");

            // Write an image file — must trigger detection
            string imgPath = Path.Combine(dir, "ss_001.png");
            File.WriteAllBytes(imgPath, [0x89, 0x50, 0x4E, 0x47]);
            // Rapid second write within debounce window must be collapsed
            File.WriteAllBytes(Path.Combine(dir, "ss_002.jpg"), [0xFF, 0xD8, 0xFF]);

            Assert.True(signal.Wait(TimeSpan.FromSeconds(5)), "Expected screenshot event within 5s.");
            Thread.Sleep(150);

            Assert.Single(detected);
            Assert.Equal(1, watcher.SessionScreenshotCount);
            Assert.NotNull(watcher.LastScreenshotAt);
            Assert.Contains("just now", watcher.FormatLastScreenshot(DateTimeOffset.UtcNow));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Watcher_MissingDirectories_StaysSafeWithoutThrowing()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}");
        using var watcher = new UpworkScreenshotWatcher([missing]);

        Assert.False(watcher.IsWatchingAnyFolder);
        Assert.Null(watcher.LastScreenshotAt);
        Assert.Equal("Last SS: none yet", watcher.FormatLastScreenshot(DateTimeOffset.UtcNow));
    }
}
