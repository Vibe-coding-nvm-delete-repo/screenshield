using DiscordShield.Core;
using Xunit;

namespace DiscordShield.Tests;

public sealed class SingleInstanceAndStartupTests
{
    [Fact]
    public void SingleInstanceGuard_SecondInstanceWakesPrimaryAndExits()
    {
        string suffix = $"test_{Guid.NewGuid():N}";
        using var primary = new SingleInstanceGuard(suffix);
        Assert.True(primary.IsPrimaryInstance);

        using var woke = new ManualResetEventSlim(false);
        primary.StartListeningForWake(() => woke.Set());

        using var secondary = new SingleInstanceGuard(suffix);
        Assert.False(secondary.IsPrimaryInstance);

        secondary.SignalExistingInstance();
        Assert.True(woke.Wait(TimeSpan.FromSeconds(3)), "Primary instance did not receive wake signal from second instance.");
    }

    [Fact]
    public void StartupRegistrationService_RoundTripsAndCleansUpRegistryKey()
    {
        string valueName = $"ScreenShield_UnitTest_{Guid.NewGuid():N}";
        var svc = new StartupRegistrationService(valueName);

        try
        {
            Assert.False(svc.IsEnabled());
            Assert.True(svc.SetEnabled(true, @"C:\Test\ScreenShield.exe"));
            Assert.True(svc.IsEnabled());
            Assert.True(svc.SetEnabled(false));
            Assert.False(svc.IsEnabled());
        }
        finally
        {
            _ = svc.SetEnabled(false);
        }
    }
}
