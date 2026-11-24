using System.IO;
using DiscordShield.Core;
using Xunit;

namespace DiscordShield.Tests;

public sealed class AppSettingsTests
{
    [Fact]
    public void Load_MissingFile_ReturnsNormalizedDefaultsWithoutWarning()
    {
        string path = Path.Combine(Path.GetTempPath(), $"discordshield-missing-{Guid.NewGuid():N}.json");

        var (settings, warning) = AppSettings.Load(path);

        Assert.Null(warning);
        Assert.True(settings.ExcludeWrapperFromCapture);
        Assert.Equal(AppSettings.DefaultDiscordUrl, settings.DiscordUrl);
        Assert.Contains("Discord", settings.ProtectedProcessNames);
        Assert.Contains("Upwork", settings.UpworkProcessNames);
    }

    [Fact]
    public void Load_CorruptJson_RecoversDefaultsWithWarning()
    {
        string path = Path.Combine(Path.GetTempPath(), $"discordshield-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not valid json !!!");
            var (settings, warning) = AppSettings.Load(path);

            Assert.NotNull(warning);
            Assert.Equal(AppSettings.DefaultDiscordUrl, settings.DiscordUrl);
            Assert.True(settings.ExcludeWrapperFromCapture);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://discord.com/app")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    public void Normalize_RejectsNonHttpsUrls(string badUrl)
    {
        var settings = new AppSettings { DiscordUrl = badUrl };
        settings.Normalize();
        Assert.Equal(AppSettings.DefaultDiscordUrl, settings.DiscordUrl);
    }

    [Fact]
    public void Normalize_StripsExeSuffixDeduplicatesAndClampsCooldown()
    {
        var settings = new AppSettings
        {
            ProtectedProcessNames = ["Discord.exe", "discord", "  ", "DiscordPTB.EXE"],
            UpworkProcessNames = [],
            GuardCooldownSeconds = 0
        };

        settings.Normalize();

        Assert.Equal(["Discord", "DiscordPTB"], settings.ProtectedProcessNames);
        Assert.Equal(["Upwork"], settings.UpworkProcessNames);
        Assert.Equal(15, settings.GuardCooldownSeconds);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsCleanly()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"discordshield-rt-{Guid.NewGuid():N}");
        string path = Path.Combine(dir, "settings.json");
        try
        {
            var original = new AppSettings
            {
                ExcludeWrapperFromCapture = false,
                NativeGuardAction = NativeGuardAction.AutoMinimize,
                OnlyActWhenUpworkRunning = false,
                GuardCooldownSeconds = 30
            };

            original.Save(path);
            var (loaded, warning) = AppSettings.Load(path);

            Assert.Null(warning);
            Assert.False(loaded.ExcludeWrapperFromCapture);
            Assert.Equal(NativeGuardAction.AutoMinimize, loaded.NativeGuardAction);
            Assert.False(loaded.OnlyActWhenUpworkRunning);
            Assert.Equal(30, loaded.GuardCooldownSeconds);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
