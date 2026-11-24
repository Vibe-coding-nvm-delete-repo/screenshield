using DiscordShield.Core;
using Xunit;

namespace DiscordShield.Tests;

public sealed class WindowGuardEngineTests
{
    private static readonly ScreenSnapshot PrimaryScreen = new(@"\\.\DISPLAY1", true, new Rectangle(0, 0, 1920, 1080));
    private static readonly ScreenSnapshot SecondaryScreen = new(@"\\.\DISPLAY2", false, new Rectangle(1920, 0, 1920, 1080));

    private static WindowSnapshot MakeWindow(
        int handle,
        int pid,
        string proc,
        bool visible = true,
        bool minimized = false,
        Rectangle? bounds = null) =>
        new(new IntPtr(handle), pid, proc, $"{proc} Window", visible, minimized, bounds ?? new Rectangle(100, 100, 900, 700));

    [Fact]
    public void Evaluate_ZeroDiscordWindows_ProducesZeroActions()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings();

        GuardEvaluation eval = engine.Evaluate(
            [MakeWindow(1, 200, "Upwork"), MakeWindow(2, 201, "Code")],
            ["Upwork", "Code"],
            [PrimaryScreen],
            settings,
            DateTimeOffset.UtcNow);

        Assert.True(eval.UpworkRunning);
        Assert.Empty(eval.ExposedNativeWindows);
        Assert.Empty(eval.Actions);
    }

    [Fact]
    public void Evaluate_IgnoresSelfProcessAndMinimizedAndTinyWindows()
    {
        var engine = new WindowGuardEngine(selfProcessId: 500);
        var settings = new AppSettings { OnlyActWhenUpworkRunning = false };

        var windows = new[]
        {
            MakeWindow(1, 500, "Discord"), // Own process — must never touch
            MakeWindow(2, 600, "Discord", minimized: true),
            MakeWindow(3, 601, "Discord", visible: false),
            MakeWindow(4, 602, "Discord", bounds: new Rectangle(0, 0, 10, 10)) // Tiny helper window
        };

        GuardEvaluation eval = engine.Evaluate(windows, ["Discord"], [PrimaryScreen], settings, DateTimeOffset.UtcNow);

        Assert.Empty(eval.ExposedNativeWindows);
        Assert.Empty(eval.Actions);
    }

    [Fact]
    public void Evaluate_OnlyActWhenUpworkRunning_StandsByWhenUpworkAbsent()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings
        {
            OnlyActWhenUpworkRunning = true,
            NativeGuardAction = NativeGuardAction.AutoMinimize
        };

        GuardEvaluation eval = engine.Evaluate(
            [MakeWindow(10, 300, "Discord")],
            ["Discord"],
            [PrimaryScreen],
            settings,
            DateTimeOffset.UtcNow);

        Assert.False(eval.UpworkRunning);
        Assert.Single(eval.ExposedNativeWindows);
        Assert.Empty(eval.Actions);
    }

    [Fact]
    public void Evaluate_AutoMinimize_TriggersWhenUpworkRunning()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings
        {
            OnlyActWhenUpworkRunning = true,
            NativeGuardAction = NativeGuardAction.AutoMinimize
        };

        GuardEvaluation eval = engine.Evaluate(
            [MakeWindow(10, 300, "Discord")],
            ["Discord", "Upwork"],
            [PrimaryScreen],
            settings,
            DateTimeOffset.UtcNow);

        Assert.True(eval.UpworkRunning);
        GuardActionItem action = Assert.Single(eval.Actions);
        Assert.Equal(NativeGuardAction.AutoMinimize, action.EffectiveAction);
        Assert.Equal(new IntPtr(10), action.Window.Handle);
    }

    [Fact]
    public void Evaluate_MoveToSecondaryMonitor_OnSingleMonitor_FailsClosedToAlertOnly()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings
        {
            OnlyActWhenUpworkRunning = false,
            NativeGuardAction = NativeGuardAction.MoveToSecondaryMonitor
        };

        GuardEvaluation eval = engine.Evaluate(
            [MakeWindow(10, 300, "Discord")],
            ["Discord"],
            [PrimaryScreen],
            settings,
            DateTimeOffset.UtcNow);

        GuardActionItem action = Assert.Single(eval.Actions);
        Assert.Equal(NativeGuardAction.AlertOnly, action.EffectiveAction);
        Assert.Contains("Only 1 monitor detected", action.Reason);
    }

    [Fact]
    public void Evaluate_MoveToSecondaryMonitor_OnDualMonitor_MovesPrimaryAndSkipsAlreadySecondary()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings
        {
            OnlyActWhenUpworkRunning = false,
            NativeGuardAction = NativeGuardAction.MoveToSecondaryMonitor
        };

        var onPrimary = MakeWindow(10, 300, "Discord", bounds: new Rectangle(200, 200, 800, 600));
        var onSecondary = MakeWindow(11, 301, "DiscordPTB", bounds: new Rectangle(2100, 150, 800, 600));

        GuardEvaluation eval = engine.Evaluate(
            [onPrimary, onSecondary],
            ["Discord", "DiscordPTB"],
            [PrimaryScreen, SecondaryScreen],
            settings,
            DateTimeOffset.UtcNow);

        GuardActionItem action = Assert.Single(eval.Actions);
        Assert.Equal(new IntPtr(10), action.Window.Handle);
        Assert.Equal(NativeGuardAction.MoveToSecondaryMonitor, action.EffectiveAction);
        Assert.Equal(SecondaryScreen.DeviceName, action.TargetScreen?.DeviceName);
    }

    [Fact]
    public void Evaluate_RespectsPerHandleCooldown()
    {
        var engine = new WindowGuardEngine(selfProcessId: 999);
        var settings = new AppSettings
        {
            OnlyActWhenUpworkRunning = false,
            NativeGuardAction = NativeGuardAction.AlertOnly,
            GuardCooldownSeconds = 10
        };

        DateTimeOffset t0 = new(2026, 11, 24, 15, 0, 0, TimeSpan.Zero);
        var win = new[] { MakeWindow(42, 300, "Discord") };

        GuardEvaluation first = engine.Evaluate(win, ["Discord"], [PrimaryScreen], settings, t0);
        GuardEvaluation withinCooldown = engine.Evaluate(win, ["Discord"], [PrimaryScreen], settings, t0.AddSeconds(5));
        GuardEvaluation afterCooldown = engine.Evaluate(win, ["Discord"], [PrimaryScreen], settings, t0.AddSeconds(11));

        Assert.Single(first.Actions);
        Assert.Empty(withinCooldown.Actions);
        Assert.Single(afterCooldown.Actions);
    }

    [Theory]
    [InlineData("Stop", true)]
    [InlineData("Pause tracking", true)]
    [InlineData("Turn Off", true)]
    [InlineData("Close", false)]
    [InlineData("Minimize", false)]
    [InlineData("Start", false)]
    [InlineData("", false)]
    public void UpworkAutomationHelper_MatchesPauseControl(string label, bool expected)
    {
        Assert.Equal(expected, UpworkAutomationHelper.MatchesPauseControl(label));
    }
}
