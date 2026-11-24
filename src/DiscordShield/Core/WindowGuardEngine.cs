using System.Runtime.InteropServices;
using DiscordShield.Native;

namespace DiscordShield.Core;

public sealed record WindowSnapshot(
    IntPtr Handle,
    int ProcessId,
    string ProcessName,
    string Title,
    bool IsVisible,
    bool IsMinimized,
    Rectangle Bounds);

public sealed record ScreenSnapshot(
    string DeviceName,
    bool IsPrimary,
    Rectangle WorkingArea);

public sealed record GuardActionItem(
    WindowSnapshot Window,
    NativeGuardAction EffectiveAction,
    string Reason,
    ScreenSnapshot? TargetScreen = null);

public sealed record GuardEvaluation(
    bool UpworkRunning,
    IReadOnlyList<WindowSnapshot> ExposedNativeWindows,
    IReadOnlyList<GuardActionItem> Actions,
    string StatusSummary,
    bool ShouldHideTaskbar = false);

public interface IWindowInspector
{
    IReadOnlyList<WindowSnapshot> GetTopLevelWindows();
    IReadOnlyList<string> GetRunningProcessNames();
    IReadOnlyList<ScreenSnapshot> GetScreens();
    bool MinimizeWindow(IntPtr hwnd);
    bool MoveWindowToScreen(IntPtr hwnd, Rectangle windowBounds, ScreenSnapshot targetScreen);
}

public sealed class Win32WindowInspector : IWindowInspector
{
    private static Dictionary<int, string> BuildPidMapSnapshot()
    {
        var map = new Dictionary<int, string>(256);
        IntPtr snap = Win32Interop.CreateToolhelp32Snapshot(Win32Interop.TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1))
        {
            return map;
        }

        try
        {
            var entry = new Win32Interop.PROCESSENTRY32W
            {
                dwSize = (uint)Marshal.SizeOf<Win32Interop.PROCESSENTRY32W>()
            };

            if (Win32Interop.Process32FirstW(snap, ref entry))
            {
                do
                {
                    string exe = entry.szExeFile;
                    if (!string.IsNullOrEmpty(exe))
                    {
                        if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            exe = exe[..^4];
                        }

                        map[unchecked((int)entry.th32ProcessID)] = exe;
                    }
                }
                while (Win32Interop.Process32NextW(snap, ref entry));
            }
        }
        finally
        {
            _ = Win32Interop.CloseHandle(snap);
        }

        return map;
    }

    public IReadOnlyList<WindowSnapshot> GetTopLevelWindows()
    {
        var results = new List<WindowSnapshot>(64);
        Dictionary<int, string> pidCache = BuildPidMapSnapshot();

        Win32Interop.EnumWindows((hwnd, lParam) =>
        {
            if (!Win32Interop.IsWindowVisible(hwnd))
            {
                return true;
            }

            _ = Win32Interop.GetWindowThreadProcessId(hwnd, out uint rawPid);
            int pid = unchecked((int)rawPid);
            if (pid <= 0 || !pidCache.TryGetValue(pid, out string? processName) || string.IsNullOrEmpty(processName))
            {
                return true;
            }

            string title = Win32Interop.ReadWindowTitle(hwnd);
            bool isMinimized = Win32Interop.IsIconic(hwnd);
            var bounds = Win32Interop.GetWindowRect(hwnd, out Win32Interop.RECT rect)
                ? new Rectangle(rect.Left, rect.Top, rect.Width, rect.Height)
                : Rectangle.Empty;

            results.Add(new WindowSnapshot(hwnd, pid, processName, title, true, isMinimized, bounds));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    public IReadOnlyList<string> GetRunningProcessNames()
    {
        Dictionary<int, string> pidMap = BuildPidMapSnapshot();
        var names = new HashSet<string>(pidMap.Values, StringComparer.OrdinalIgnoreCase);
        return names.ToList();
    }

    public IReadOnlyList<ScreenSnapshot> GetScreens()
    {
        return Screen.AllScreens
            .Select(s => new ScreenSnapshot(s.DeviceName, s.Primary, s.WorkingArea))
            .ToList();
    }

    public bool MinimizeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        return Win32Interop.ShowWindow(hwnd, Win32Interop.SW_MINIMIZE);
    }

    public bool MoveWindowToScreen(IntPtr hwnd, Rectangle windowBounds, ScreenSnapshot targetScreen)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        if (Win32Interop.IsZoomed(hwnd))
        {
            _ = Win32Interop.ShowWindow(hwnd, Win32Interop.SW_RESTORE);
        }

        int width = windowBounds.Width > 0
            ? Math.Min(windowBounds.Width, targetScreen.WorkingArea.Width)
            : Math.Min(1100, targetScreen.WorkingArea.Width);
        int height = windowBounds.Height > 0
            ? Math.Min(windowBounds.Height, targetScreen.WorkingArea.Height)
            : Math.Min(750, targetScreen.WorkingArea.Height);

        int targetX = targetScreen.WorkingArea.Left + Math.Max(0, (targetScreen.WorkingArea.Width - width) / 2);
        int targetY = targetScreen.WorkingArea.Top + Math.Max(0, (targetScreen.WorkingArea.Height - height) / 2);

        return Win32Interop.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            targetX,
            targetY,
            width,
            height,
            Win32Interop.SWP_NOZORDER | Win32Interop.SWP_NOACTIVATE);
    }
}

public sealed class WindowGuardEngine
{
    private readonly int _selfProcessId;
    private readonly Dictionary<IntPtr, DateTimeOffset> _lastActionByHandle = new();

    public WindowGuardEngine(int? selfProcessId = null)
    {
        _selfProcessId = selfProcessId ?? Environment.ProcessId;
    }

    public void ResetCooldowns() => _lastActionByHandle.Clear();

    public GuardEvaluation Evaluate(
        IReadOnlyList<WindowSnapshot> windows,
        IReadOnlyList<string> runningProcessNames,
        IReadOnlyList<ScreenSnapshot> screens,
        AppSettings settings,
        DateTimeOffset now)
    {
        var protectedSet = new HashSet<string>(settings.ProtectedProcessNames, StringComparer.OrdinalIgnoreCase);
        var upworkSet = new HashSet<string>(settings.UpworkProcessNames, StringComparer.OrdinalIgnoreCase);

        bool upworkRunning = runningProcessNames.Any(upworkSet.Contains)
            || windows.Any(w => upworkSet.Contains(w.ProcessName));

        var exposed = windows
            .Where(w =>
                w.ProcessId != _selfProcessId
                && w.IsVisible
                && !w.IsMinimized
                && w.Bounds.Width > 50
                && w.Bounds.Height > 50
                && (protectedSet.Contains(w.ProcessName)
                    || (ActiveCatalogService.BrowserProcesses.Contains(w.ProcessName)
                        && ActiveCatalogService.MatchesBlockedSite(w.Title, settings.BlockedDomains))))
            .ToList();

        if (!settings.NativeGuardEnabled)
        {
            return new GuardEvaluation(
                upworkRunning,
                exposed,
                Array.Empty<GuardActionItem>(),
                "Shield guard is paused.",
                ShouldHideTaskbar: false);
        }

        if (exposed.Count == 0)
        {
            string msg = upworkRunning
                ? "Clear: Upwork active, no blocked apps or sites open."
                : "Clear: No blocked apps or sites open.";
            return new GuardEvaluation(upworkRunning, exposed, Array.Empty<GuardActionItem>(), msg, ShouldHideTaskbar: false);
        }

        if (settings.OnlyActWhenUpworkRunning && !upworkRunning)
        {
            return new GuardEvaluation(
                false,
                exposed,
                Array.Empty<GuardActionItem>(),
                $"Standby: {exposed.Count} blocked item(s) open, Upwork is off.",
                ShouldHideTaskbar: false);
        }

        bool shouldHideTaskbar = settings.AlwaysHideTaskbarWhenBlockedOpen;

        ScreenSnapshot? secondaryScreen = screens.FirstOrDefault(s => !s.IsPrimary);
        var actions = new List<GuardActionItem>();
        var cooldown = TimeSpan.FromSeconds(Math.Max(1, settings.GuardCooldownSeconds));

        foreach (WindowSnapshot win in exposed)
        {
            if (_lastActionByHandle.TryGetValue(win.Handle, out DateTimeOffset lastTime)
                && (now - lastTime) < cooldown)
            {
                continue;
            }

            if (settings.NativeGuardAction == NativeGuardAction.MoveToSecondaryMonitor)
            {
                if (secondaryScreen is null)
                {
                    actions.Add(new GuardActionItem(
                        win,
                        NativeGuardAction.AlertOnly,
                        $"Only {screens.Count} monitor detected — cannot move {win.ProcessName} to a secondary monitor. Use Invisible Discord or Auto-Minimize."));
                    _lastActionByHandle[win.Handle] = now;
                    continue;
                }

                if (IsWindowMostlyOnScreen(win.Bounds, secondaryScreen.WorkingArea))
                {
                    continue;
                }

                actions.Add(new GuardActionItem(
                    win,
                    NativeGuardAction.MoveToSecondaryMonitor,
                    $"Moving {win.ProcessName} to secondary monitor {secondaryScreen.DeviceName}.",
                    secondaryScreen));
                _lastActionByHandle[win.Handle] = now;
                continue;
            }

            string reason = settings.NativeGuardAction switch
            {
                NativeGuardAction.AutoMinimize => $"Auto-minimizing exposed native window '{win.ProcessName}' while Upwork protection is active.",
                NativeGuardAction.PauseUpworkAndAlert => $"Exposed native window '{win.ProcessName}' detected — attempting to pause Upwork Time Tracker and alerting.",
                _ => $"Blocked '{win.ProcessName}' window is visible to screenshots — minimize or close it."
            };

            actions.Add(new GuardActionItem(win, settings.NativeGuardAction, reason));
            _lastActionByHandle[win.Handle] = now;
        }

        string summary = actions.Count > 0
            ? $"Action triggered for {actions.Count} blocked window(s)."
            : $"Blocked window(s) detected ({exposed.Count}); within cooldown or already on secondary monitor.";

        return new GuardEvaluation(upworkRunning, exposed, actions, summary, shouldHideTaskbar);
    }

    public static bool IsWindowMostlyOnScreen(Rectangle windowBounds, Rectangle screenArea)
    {
        if (windowBounds.Width <= 0 || windowBounds.Height <= 0)
        {
            return false;
        }

        var center = new Point(
            windowBounds.Left + (windowBounds.Width / 2),
            windowBounds.Top + (windowBounds.Height / 2));

        return screenArea.Contains(center);
    }
}
