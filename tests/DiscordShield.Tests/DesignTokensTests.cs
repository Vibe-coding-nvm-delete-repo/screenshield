using DiscordShield.Core;
using DiscordShield.UI;
using Xunit;

namespace DiscordShield.Tests;

public sealed class DesignTokensTests
{
    public static IEnumerable<object[]> BothThemes()
    {
        yield return [DesignTokens.Dark];
        yield return [DesignTokens.Light];
    }

    [Theory]
    [MemberData(nameof(BothThemes))]
    public void AllReadableTextPairs_MeetWcagAA_4Point5To1(ThemePalette p)
    {
        var pairs = new (string Name, Color Fg, Color Bg)[]
        {
            ("TextPrimary on BaseSurface", p.TextPrimary, p.BaseSurface),
            ("TextPrimary on ContentSurface", p.TextPrimary, p.ContentSurface),
            ("TextPrimary on HoverFill", p.TextPrimary, p.HoverFill),
            ("TextPrimary on PressedFill", p.TextPrimary, p.PressedFill),
            ("TextPrimary on SelectedFill", p.TextPrimary, p.SelectedFill),
            ("TextPrimary on InputSurface", p.TextPrimary, p.InputSurface),
            ("TextSub on BaseSurface", p.TextSub, p.BaseSurface),
            ("TextSub on ContentSurface", p.TextSub, p.ContentSurface),
            ("TextOnAccent on AccentFill", p.TextOnAccent, p.AccentFill),
            ("ShieldActiveText on BaseSurface", p.ShieldActiveText, p.BaseSurface),
            ("ShieldActiveText on ShieldActiveFill", p.ShieldActiveText, p.ShieldActiveFill),
            ("ShieldOffText on BaseSurface", p.ShieldOffText, p.BaseSurface),
            ("ShieldOffText on ShieldOffFill", p.ShieldOffText, p.ShieldOffFill),
            ("DangerText on BaseSurface", p.DangerText, p.BaseSurface)
        };

        foreach (var (name, fg, bg) in pairs)
        {
            double ratio = DesignTokens.ContrastRatio(fg, bg);
            Assert.True(
                ratio >= 4.5,
                $"[{p.Name}] {name} contrast ratio {ratio:F2}:1 is below WCAG AA 4.5:1.");
        }
    }

    [Theory]
    [MemberData(nameof(BothThemes))]
    public void InputBottomLine_Meets3To1ControlContrast(ThemePalette p)
    {
        double ratio = DesignTokens.ContrastRatio(p.InputBottomLine, p.InputSurface);
        Assert.True(ratio >= 3.0, $"[{p.Name}] InputBottomLine contrast {ratio:F2}:1 < 3.0:1.");
    }

    [Fact]
    public void MainForm_AllInteractiveControls_MeetMinimumHitSize24x24()
    {
        Exception? ex = null;
        var sta = new Thread(() =>
        {
            try
            {
                using var form = new MainForm(
                    new AppSettings(),
                    new CaptureExclusionService(),
                    new Win32WindowInspector(),
                    new UpworkAutomationHelper(),
                    taskbarShield: new TaskbarShieldService(() => false, _ => true),
                    initialPalette: DesignTokens.Dark);
                form.CreateControl();

                Assert.NotEmpty(form.InteractiveControls);
                foreach (Control ctrl in form.InteractiveControls)
                {
                    Assert.True(ctrl.Width >= 24, $"{ctrl.Text} width {ctrl.Width} < 24px.");
                    Assert.True(ctrl.Height >= 24, $"{ctrl.Text} height {ctrl.Height} < 24px.");
                    Assert.True(ctrl.TabStop, $"{ctrl.Text} must be keyboard focusable (TabStop=true).");
                }

                // Verify all 9 settings have (?) HelpBadges with plain-English text and >= 24x24 hit targets
                Assert.Equal(9, form.HelpBadges.Count);
                foreach (HelpBadge badge in form.HelpBadges)
                {
                    Assert.True(badge.Width >= 24 && badge.Height >= 24, "HelpBadge must be at least 24x24px.");
                    Assert.True(badge.TabStop, "HelpBadge must be keyboard focusable.");
                    Assert.False(string.IsNullOrWhiteSpace(badge.HelpText));
                }
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(TimeSpan.FromSeconds(10));
        Assert.Null(ex);
    }
}
