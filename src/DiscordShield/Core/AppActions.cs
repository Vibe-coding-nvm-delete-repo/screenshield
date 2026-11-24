namespace DiscordShield.Core;

/// <summary>
/// Exhaustive registry of every input door in ScreenShield (action-coverage method).
/// Adding a new door without updating ActionRegistry.Describe() fails compilation/tests.
/// </summary>
public enum AppAction
{
    SwitchTabAll,
    SwitchTabAllowed,
    SwitchTabBlocked,
    SwitchTabSettings,
    SearchCatalog,
    RefreshCatalog,
    ToggleItemBlocked,
    BulkBlockSelected,
    BulkAllowSelected,
    AddCustomSite,
    ToggleCaptureShield,
    ToggleTaskbarShield,
    VerifyCaptureNow,
    SaveSettings,
    ToggleWindowVisibility,
    MinimizeToTrayOnClose,
    ExitApplication,
    GlobalHotkeyToggle,
    NativeGuardEvaluate,
    NativeGuardMinimizeApp,
    NativeGuardMoveToSecondary,
    NativeGuardPauseUpwork,
    AffinityWatchdogCheck,
    SystemThemeSync,
    CliSelfTest
}

public sealed record ActionDescriptor(
    AppAction Action,
    string DoorKind,
    string Trigger,
    string Shortcut,
    string Description);

public static class ActionRegistry
{
    public static IReadOnlyList<AppAction> All { get; } = Enum.GetValues<AppAction>();

    public static ActionDescriptor Describe(AppAction action) => action switch
    {
        AppAction.SwitchTabAll => new(action, "Tab Bar / Key", "Click All Apps & Sites tab", "Ctrl+1", "Show all active apps and browser sites"),
        AppAction.SwitchTabAllowed => new(action, "Tab Bar / Key", "Click Allowed tab", "Ctrl+2", "Show all auto-accepted apps and sites"),
        AppAction.SwitchTabBlocked => new(action, "Tab Bar / Key", "Click Blocked tab", "Ctrl+3", "Show all blocked apps and domains"),
        AppAction.SwitchTabSettings => new(action, "Tab Bar / Key", "Click Settings tab", "Ctrl+4", "Show inline minimalist settings sheet"),
        AppAction.SearchCatalog => new(action, "Toolbar Input", "Type in search box", "Ctrl+F", "Filter active apps and sites in real time"),
        AppAction.RefreshCatalog => new(action, "TopBar / Key", "Click Refresh", "F5", "Refresh active desktop apps and browser tabs off UI thread"),
        AppAction.ToggleItemBlocked => new(action, "List Row", "Double-click row or press Enter", "Enter", "Toggle single app/site between Allowed and Blocked"),
        AppAction.BulkBlockSelected => new(action, "Toolbar Button", "Check rows -> Click Block Selected", "—", "Bulk-add selected apps/sites to Blocked"),
        AppAction.BulkAllowSelected => new(action, "Toolbar Button", "Check rows -> Click Allow Selected", "—", "Bulk-remove selected apps/sites from Blocked"),
        AppAction.AddCustomSite => new(action, "Toolbar Input", "Type domain -> Click + Add Site", "—", "Add any custom website domain to Blocked"),
        AppAction.ToggleCaptureShield => new(action, "TopBar / Tray / Key", "Click Shield pill", "Ctrl+Shift+S", "Toggle WDA_EXCLUDEFROMCAPTURE on ScreenShield window"),
        AppAction.ToggleTaskbarShield => new(action, "Settings / Guard", "Toggle Auto-hide Windows taskbar", "—", "Hide/restore Windows taskbar via native SHAppBarMessage(ABS_AUTOHIDE)"),
        AppAction.VerifyCaptureNow => new(action, "TopBar / Tray / Key", "Click Verify button", "Ctrl+Shift+V", "Run live BitBlt capture-exclusion proof on ScreenShield window"),
        AppAction.SaveSettings => new(action, "Settings Tab", "Change any setting", "—", "Apply settings immediately and persist asynchronously"),
        AppAction.ToggleWindowVisibility => new(action, "Tray", "Double-click Tray icon", "Ctrl+Shift+H", "Show/focus or hide ScreenShield window"),
        AppAction.MinimizeToTrayOnClose => new(action, "Window Event", "Click Close (X) with Tray enabled", "Alt+F4", "Hide to system tray while continuing protection"),
        AppAction.ExitApplication => new(action, "Tray / Window", "Tray Exit", "—", "Restore taskbar state, unregister hotkey, dispose GDI icons, exit"),
        AppAction.GlobalHotkeyToggle => new(action, "Win32 WM_HOTKEY", "Press global shortcut from any app", "Ctrl+Shift+H", "Instantly show or hide ScreenShield"),
        AppAction.NativeGuardEvaluate => new(action, "Background Timer", "2s background guard sweep", "—", "Evaluate blocked apps & browser sites off UI thread"),
        AppAction.NativeGuardMinimizeApp => new(action, "Guard Action", "Auto-minimize triggered", "—", "Minimize exposed blocked window"),
        AppAction.NativeGuardMoveToSecondary => new(action, "Guard Action", "Move to 2nd monitor triggered", "—", "Restore maximized window and move to secondary display"),
        AppAction.NativeGuardPauseUpwork => new(action, "Guard Action", "Pause Upwork triggered", "—", "Locate Upwork Stop/Pause button via UI Automation"),
        AppAction.AffinityWatchdogCheck => new(action, "Timer", "3s affinity readback check", "—", "Verify WDA_EXCLUDEFROMCAPTURE readback (0x11)"),
        AppAction.SystemThemeSync => new(action, "System Event", "SystemEvents.UserPreferenceChanged", "—", "Follow Windows Light/Dark theme + DWM chrome"),
        AppAction.CliSelfTest => new(action, "CLI Flag", "ScreenShield.exe --self-test", "—", "Run CLI capture-exclusion proof and output JSON verdict"),
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unhandled AppAction in ActionRegistry.")
    };
}
