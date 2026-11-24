using DiscordShield.Core;
using Xunit;

namespace DiscordShield.Tests;

public sealed class WebViewPolicyTests
{
    [Theory]
    [InlineData("https://discord.com/app", true)]
    [InlineData("https://ptb.discord.com/channels/@me", true)]
    [InlineData("https://discordapp.com/oauth2/authorize", true)]
    [InlineData("http://discord.com/app", false)]
    [InlineData("https://evil-discord.com/phish", false)]
    [InlineData("https://discord.com.evil.org/", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void IsDiscordOrigin_MatchesOnlyTrustedHttpsDiscordHosts(string url, bool expected)
    {
        Assert.Equal(expected, WebViewPolicy.IsDiscordOrigin(url));
    }

    [Theory]
    [InlineData("https://discord.com/channels/1/2", NewWindowDecision.NavigateInPlace)]
    [InlineData("https://canary.discord.com/app", NewWindowDecision.NavigateInPlace)]
    [InlineData("https://github.com/example/repo", NewWindowDecision.OpenExternalBrowser)]
    [InlineData("http://example.com", NewWindowDecision.OpenExternalBrowser)]
    [InlineData("file:///C:/Windows/win.ini", NewWindowDecision.Block)]
    [InlineData("javascript:void(0)", NewWindowDecision.Block)]
    [InlineData("", NewWindowDecision.Block)]
    public void DecideNewWindow_ContainsDiscordAndRoutesExternalSafely(string url, NewWindowDecision expected)
    {
        Assert.Equal(expected, WebViewPolicy.DecideNewWindow(url));
    }

    [Fact]
    public void ComputeSelfTestVerdict_NeverFalsePassesWhenDesktopCaptureUnavailable()
    {
        var (status, pass, code) = Program.ComputeSelfTestVerdict(
            affinityVerified: true,
            captureUnavailable: true,
            baselineSawWindow: false,
            protectedHidWindow: false);

        Assert.Equal("INCONCLUSIVE_CAPTURE_UNAVAILABLE", status);
        Assert.False(pass);
        Assert.Equal(2, code);
    }

    [Fact]
    public void ComputeSelfTestVerdict_PassesOnlyWhenAffinityAndPixelProofBothSucceed()
    {
        var (status, pass, code) = Program.ComputeSelfTestVerdict(
            affinityVerified: true,
            captureUnavailable: false,
            baselineSawWindow: true,
            protectedHidWindow: true);

        Assert.Equal("PROVEN_EXCLUDED", status);
        Assert.True(pass);
        Assert.Equal(0, code);
    }

    [Fact]
    public void ComputeSelfTestVerdict_FailsWhenPixelsLeakOrAffinityFails()
    {
        var leaked = Program.ComputeSelfTestVerdict(true, false, true, false);
        Assert.Equal("FAILED_PIXEL_VERIFICATION", leaked.Status);
        Assert.False(leaked.OverallPass);
        Assert.Equal(1, leaked.ExitCode);

        var badAffinity = Program.ComputeSelfTestVerdict(false, false, true, true);
        Assert.Equal("FAILED_AFFINITY", badAffinity.Status);
        Assert.False(badAffinity.OverallPass);
        Assert.Equal(1, badAffinity.ExitCode);
    }
}
