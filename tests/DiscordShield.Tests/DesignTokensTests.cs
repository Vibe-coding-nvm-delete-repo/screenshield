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
            ("DangerText on BaseSurface", p.DangerText, p.BaseSurface),
            ("TextPrimary on SurfaceCard", p.TextPrimary, p.IsDark ? Color.FromArgb(30, 32, 38) : Color.White),
            ("TextSub on SurfaceCard", p.TextSub, p.IsDark ? Color.FromArgb(30, 32, 38) : Color.White),
            ("ShieldActiveText on SurfaceCard", p.ShieldActiveText, p.IsDark ? Color.FromArgb(30, 32, 38) : Color.White),
            ("DangerText on SurfaceCard", p.DangerText, p.IsDark ? Color.FromArgb(30, 32, 38) : Color.White)
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

                // Verify all 10 settings have (?) HelpBadges with plain-English text and >= 24x24 hit targets
                Assert.Equal(10, form.HelpBadges.Count);
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

    [Theory]
    [InlineData(940, 120, 24, 280, 72, 980, 620)] // Far right edge — must clamp left inside form
    [InlineData(12, 580, 24, 280, 72, 980, 620)]  // Near bottom edge — must flip above anchor
    [InlineData(950, 590, 24, 280, 72, 980, 620)] // Bottom-right corner — must clamp both X and Y
    [InlineData(-10, -5, 24, 280, 72, 980, 620)]  // Negative coordinates — must clamp >= 10px margin
    public void HelpPopover_ComputeClampedBounds_NeverOverflowsWindowEdges(
        int anchorX, int anchorY, int anchorH, int popW, int popH, int formW, int formH)
    {
        Rectangle rect = HelpPopover.ComputeClampedBounds(
            new Point(anchorX, anchorY),
            anchorH,
            new Size(popW, popH),
            new Size(formW, formH));

        Assert.True(rect.Left >= 10, $"Left {rect.Left} clipped!");
        Assert.True(rect.Top >= 10, $"Top {rect.Top} clipped!");
        Assert.True(rect.Right <= formW - 10, $"Right {rect.Right} overflowed form width {formW}!");
        Assert.True(rect.Bottom <= formH - 10, $"Bottom {rect.Bottom} overflowed form height {formH}!");
    }

    [Fact]
    public void WindowGuardEngine_IdleClearSweep_AllocatesMinimalBytes()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings();
        var primary = new ScreenSnapshot(@"\\.\DISPLAY1", true, new Rectangle(0, 0, 1920, 1080));
        var windows = new[]
        {
            new WindowSnapshot(new IntPtr(1), 100, "Code", "ScreenShield", true, false, new Rectangle(0, 0, 1200, 800)),
            new WindowSnapshot(new IntPtr(2), 101, "Notion", "Roadmap", true, false, new Rectangle(0, 0, 1200, 800))
        };
        var procs = new[] { "Code", "Notion" };

        // Warmup
        _ = engine.Evaluate(windows, procs, [primary], settings, DateTimeOffset.UtcNow);

        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = engine.Evaluate(windows, procs, [primary], settings, DateTimeOffset.UtcNow);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Only the tiny GuardEvaluation record itself (<= 128 bytes; previously > 1,200 bytes with HashSets + LINQ!)
        Assert.True(allocated <= 128, $"Idle sweep allocated {allocated} bytes (expected <= 128 bytes).");
    }

    [Fact]
    public void MainForm_InstantShowFromTray_RestoresVisibilityAndPreservesCaptureAffinity()
    {
        Exception? ex = null;
        var sta = new Thread(() =>
        {
            try
            {
                var capture = new CaptureExclusionService();
                using var form = new MainForm(
                    new AppSettings { ExcludeWrapperFromCapture = true },
                    capture,
                    new Win32WindowInspector(),
                    new UpworkAutomationHelper(),
                    taskbarShield: new TaskbarShieldService(() => false, _ => true),
                    initialPalette: DesignTokens.Dark,
                    autoRefreshOnShow: false);

                form.Show();
                form.Hide();
                Assert.False(form.Visible);

                form.InstantShowFromTray();
                Assert.True(form.Visible);

                AffinityResult affinity = capture.ReadAffinity(form.Handle);
                Assert.True(affinity.Success);
                Assert.Equal(DiscordShield.Native.Win32Interop.WDA_EXCLUDEFROMCAPTURE, affinity.ActualAffinity);
                form.Hide();
            }
            catch (Exception e) { ex = e; }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();
        sta.Join(TimeSpan.FromSeconds(10));
        Assert.Null(ex);
    }
}
