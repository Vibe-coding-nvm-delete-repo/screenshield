using System.Runtime.InteropServices;
using DiscordShield.Native;
using Microsoft.Win32;

namespace DiscordShield.Core;

public sealed record DesktopSnapshot(
    IReadOnlyList<WindowSnapshot> Windows,
    IReadOnlyList<string> RunningProcessNames,
    IReadOnlyList<ScreenSnapshot> Screens);

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
    DesktopSnapshot CaptureDesktopSnapshot();
    IReadOnlyList<WindowSnapshot> GetTopLevelWindows();
    IReadOnlyList<string> GetRunningProcessNames();
    IReadOnlyList<ScreenSnapshot> GetScreens();
    bool MinimizeWindow(IntPtr hwnd);
    bool MoveWindowToScreen(IntPtr hwnd, Rectangle windowBounds, ScreenSnapshot targetScreen);
}

public sealed class Win32WindowInspector : IWindowInspector
{
    private volatile IReadOnlyList<ScreenSnapshot>? _cachedScreens;

    public Win32WindowInspector()
    {
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        _cachedScreens = null;
    }

    /// <summary>
    /// Single-pass desktop capture: runs CreateToolhelp32Snapshot ONCE and reuses the PID map
    /// for both EnumWindows and RunningProcessNames, plus cached Screen snapshots.
    /// </summary>
    public DesktopSnapshot CaptureDesktopSnapshot()
    {
        Dictionary<int, string> pidCache = BuildPidMapSnapshot();
        List<WindowSnapshot> windows = EnumerateVisibleWindows(pidCache);
        var uniqueNames = new HashSet<string>(pidCache.Values, StringComparer.OrdinalIgnoreCase);
        return new DesktopSnapshot(windows, uniqueNames.ToList(), GetScreens());
    }

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

    private static List<WindowSnapshot> EnumerateVisibleWindows(Dictionary<int, string> pidCache)
    {
        var results = new List<WindowSnapshot>(64);

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

    public IReadOnlyList<WindowSnapshot> GetTopLevelWindows()
    {
        return EnumerateVisibleWindows(BuildPidMapSnapshot());
    }

    public IReadOnlyList<string> GetRunningProcessNames()
    {
        Dictionary<int, string> pidMap = BuildPidMapSnapshot();
        var names = new HashSet<string>(pidMap.Values, StringComparer.OrdinalIgnoreCase);
        return names.ToList();
    }

    public IReadOnlyList<ScreenSnapshot> GetScreens()
    {
        IReadOnlyList<ScreenSnapshot>? cached = _cachedScreens;
        if (cached is not null)
        {
            return cached;
        }

        Screen[] all = Screen.AllScreens;
        var list = new ScreenSnapshot[all.Length];
        for (int i = 0; i < all.Length; i++)
        {
            Screen s = all[i];
            list[i] = new ScreenSnapshot(s.DeviceName, s.Primary, s.WorkingArea);
        }

        _cachedScreens = list;
        return list;
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
        List<string> upworkProcs = settings.UpworkProcessNames;
        List<string> protectedProcs = settings.ProtectedProcessNames;
        List<string> blockedDomains = settings.BlockedDomains;

        bool upworkRunning = false;
        for (int i = 0; i < runningProcessNames.Count; i++)
        {
            if (ContainsIgnoreCase(upworkProcs, runningProcessNames[i]))
            {
                upworkRunning = true;
                break;
            }
        }

        if (!upworkRunning)
        {
            for (int i = 0; i < windows.Count; i++)
            {
                if (ContainsIgnoreCase(upworkProcs, windows[i].ProcessName))
                {
                    upworkRunning = true;
                    break;
                }
            }
        }

        List<WindowSnapshot>? exposedList = null;
        for (int i = 0; i < windows.Count; i++)
        {
            WindowSnapshot w = windows[i];
            if (w.ProcessId == _selfProcessId
                || !w.IsVisible
                || w.IsMinimized
                || w.Bounds.Width <= 50
                || w.Bounds.Height <= 50)
            {
                continue;
            }

            bool isBlocked = ContainsIgnoreCase(protectedProcs, w.ProcessName)
                || (blockedDomains.Count > 0
                    && ActiveCatalogService.BrowserProcesses.Contains(w.ProcessName)
                    && ActiveCatalogService.MatchesBlockedSite(w.Title, blockedDomains));

            if (isBlocked)
            {
                exposedList ??= new List<WindowSnapshot>(4);
                exposedList.Add(w);
            }
        }

        IReadOnlyList<WindowSnapshot> exposed = exposedList is not null ? exposedList : Array.Empty<WindowSnapshot>();

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
        ScreenSnapshot? secondaryScreen = null;
        for (int i = 0; i < screens.Count; i++)
        {
            if (!screens[i].IsPrimary)
            {
                secondaryScreen = screens[i];
                break;
            }
        }

        List<GuardActionItem>? actionsList = null;
        var cooldown = TimeSpan.FromSeconds(Math.Max(1, settings.GuardCooldownSeconds));

        for (int i = 0; i < exposed.Count; i++)
        {
            WindowSnapshot win = exposed[i];
            if (_lastActionByHandle.TryGetValue(win.Handle, out DateTimeOffset lastTime)
                && (now - lastTime) < cooldown)
            {
                continue;
            }

            if (settings.NativeGuardAction == NativeGuardAction.MoveToSecondaryMonitor)
            {
                if (secondaryScreen is null)
                {
                    actionsList ??= new List<GuardActionItem>(exposed.Count);
                    actionsList.Add(new GuardActionItem(
                        win,
                        NativeGuardAction.AlertOnly,
                        $"Only {screens.Count} monitor detected — cannot move {win.ProcessName} to a secondary monitor. Use Auto-Minimize instead."));
                    _lastActionByHandle[win.Handle] = now;
                    continue;
                }

                if (IsWindowMostlyOnScreen(win.Bounds, secondaryScreen.WorkingArea))
                {
                    continue;
                }

                actionsList ??= new List<GuardActionItem>(exposed.Count);
                actionsList.Add(new GuardActionItem(
                    win,
                    NativeGuardAction.MoveToSecondaryMonitor,
                    $"Moving {win.ProcessName} to secondary monitor {secondaryScreen.DeviceName}.",
                    secondaryScreen));
                _lastActionByHandle[win.Handle] = now;
                continue;
            }

            string reason = settings.NativeGuardAction switch
            {
                NativeGuardAction.AutoMinimize => $"Auto-minimizing blocked '{win.ProcessName}' window while Upwork protection is active.",
                NativeGuardAction.PauseUpworkAndAlert => $"Blocked '{win.ProcessName}' window detected — attempting to pause Upwork Time Tracker.",
                _ => $"Blocked '{win.ProcessName}' window is visible to screenshots — minimize or close it."
            };

            actionsList ??= new List<GuardActionItem>(exposed.Count);
            actionsList.Add(new GuardActionItem(win, settings.NativeGuardAction, reason));
            _lastActionByHandle[win.Handle] = now;
        }

        IReadOnlyList<GuardActionItem> actions = actionsList is not null ? actionsList : Array.Empty<GuardActionItem>();
        string summary = actions.Count > 0
            ? $"Action triggered for {actions.Count} blocked window(s)."
            : $"Blocked window(s) detected ({exposed.Count}); within cooldown or already on secondary monitor.";

        return new GuardEvaluation(upworkRunning, exposed, actions, summary, shouldHideTaskbar);
    }

    private static bool ContainsIgnoreCase(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
