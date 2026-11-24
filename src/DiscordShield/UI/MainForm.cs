using System.Diagnostics;
using System.IO;
using DiscordShield.Core;
using DiscordShield.Native;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DiscordShield.UI;

public sealed class MainForm : Form
{
    private const int HotkeyId = 0x51D0;
    private const uint VkD = 0x44;

    private readonly ICaptureExclusionService _captureService;
    private readonly IWindowInspector _windowInspector;
    private readonly IUpworkAutomationHelper _upworkAutomation;
    private readonly WindowGuardEngine _guardEngine;

    private AppSettings _settings;
    private bool _forceExit;
    private bool _hotkeyRegistered;
    private readonly Icon _shieldIcon;

    private readonly TableLayoutPanel _topBar;
    private readonly Label _lblShieldStatus;
    private readonly Label _lblGuardStatus;
    private readonly Button _btnToggleShield;
    private readonly Button _btnVerify;
    private readonly Button _btnLog;
    private readonly Button _btnSettings;
    private readonly Panel _sentinelOverlay;
    private readonly TextBox _txtLog;
    private readonly WebView2 _webView;

    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly System.Windows.Forms.Timer _affinityWatchdog;

    public MainForm(
        AppSettings settings,
        ICaptureExclusionService captureService,
        IWindowInspector windowInspector,
        IUpworkAutomationHelper upworkAutomation)
    {
        _settings = settings;
        _captureService = captureService;
        _windowInspector = windowInspector;
        _upworkAutomation = upworkAutomation;
        _guardEngine = new WindowGuardEngine();

        Text = "DiscordShield — Screenshot-Excluded Discord";
        Size = new Size(1240, 820);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 31, 34);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 9.5f);
        _shieldIcon = CreateShieldIcon(active: true);
        Icon = _shieldIcon;

        _topBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 52),
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.FromArgb(43, 45, 49),
            Padding = new Padding(12, 8, 12, 8)
        };
        _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60f));
        _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40f));
        _topBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _topBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        _lblShieldStatus = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 9.5f),
            ForeColor = Color.FromArgb(35, 165, 90),
            Text = "● Initializing Shield..."
        };

        _lblGuardStatus = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", 9f),
            ForeColor = Color.FromArgb(181, 186, 193),
            Text = "Guard: Standby"
        };

        var rightButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0),
            Margin = new Padding(8, 0, 0, 0)
        };

        _btnToggleShield = CreateBarButton("Shield: ON", Color.FromArgb(35, 165, 90));
        _btnToggleShield.Click += (_, _) => ToggleCaptureExclusion();

        _btnVerify = CreateBarButton("Verify Now", Color.FromArgb(88, 101, 242));
        _btnVerify.Click += async (_, _) => await RunLiveCaptureVerificationAsync();

        _btnLog = CreateBarButton("Log", Color.FromArgb(78, 80, 88));
        _btnLog.Click += (_, _) => ToggleLogPanel();

        _btnSettings = CreateBarButton("Settings", Color.FromArgb(78, 80, 88));
        _btnSettings.Click += (_, _) => OpenSettings();

        rightButtons.Controls.Add(_btnToggleShield);
        rightButtons.Controls.Add(_btnVerify);
        rightButtons.Controls.Add(_btnLog);
        rightButtons.Controls.Add(_btnSettings);

        _topBar.Controls.Add(_lblShieldStatus, 0, 0);
        _topBar.Controls.Add(_lblGuardStatus, 1, 0);
        _topBar.Controls.Add(rightButtons, 2, 0);

        _txtLog = new TextBox
        {
            Dock = DockStyle.Bottom,
            Height = 130,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Visible = false,
            BackColor = Color.FromArgb(20, 21, 24),
            ForeColor = Color.FromArgb(219, 222, 225),
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.None
        };

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
            DefaultBackgroundColor = Color.FromArgb(30, 31, 34)
        };

        _sentinelOverlay = new Panel
        {
            Size = new Size(160, 80),
            Location = new Point(40, 70),
            BackColor = Color.Magenta,
            Visible = false
        };

        Controls.Add(_sentinelOverlay);
        Controls.Add(_webView);
        Controls.Add(_txtLog);
        Controls.Add(_topBar);
        _sentinelOverlay.BringToFront();

        _trayIcon = new NotifyIcon
        {
            Icon = Icon,
            Text = "DiscordShield (Capture Exclusion Active)",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ToggleWindowVisibility();

        _guardTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _guardTimer.Tick += (_, _) => RunNativeGuardTick();

        _affinityWatchdog = new System.Windows.Forms.Timer { Interval = 3000 };
        _affinityWatchdog.Tick += (_, _) => EnforceAffinityWatchdog();
    }

    private static Button CreateBarButton(string text, Color backColor)
    {
        var btn = new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 4, 10, 4),
            Margin = new Padding(6, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = backColor,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
            Font = new Font("Segoe UI Semibold", 9f)
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show / Hide DiscordShield (Ctrl+Shift+D)", null, (_, _) => ToggleWindowVisibility());
        menu.Items.Add("Toggle Capture Exclusion", null, (_, _) => ToggleCaptureExclusion());
        menu.Items.Add("Verify Capture Exclusion Now", null, async (_, _) => await RunLiveCaptureVerificationAsync());
        menu.Items.Add("Settings...", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit DiscordShield", null, (_, _) =>
        {
            _forceExit = true;
            Close();
        });
        return menu;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCaptureExclusionState();
        SyncGlobalHotkey();
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyCaptureExclusionState();
        _guardTimer.Start();
        _affinityWatchdog.Start();
        await InitializeWebViewAsync();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32Interop.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            ToggleWindowVisibility();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceExit && _settings.MinimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(
                2000,
                "DiscordShield minimized to tray",
                "Press Ctrl+Shift+D or double-click the tray icon to restore.",
                ToolTipIcon.Info);
            return;
        }

        _guardTimer.Stop();
        _affinityWatchdog.Stop();
        UnregisterGlobalHotkey();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _shieldIcon.Dispose();
        base.OnFormClosing(e);
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.DefaultWebViewProfilePath);
            CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: AppSettings.DefaultWebViewProfilePath);

            await _webView.EnsureCoreWebView2Async(env);
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.NewWindowRequested += OnWebViewNewWindowRequested;
            _webView.CoreWebView2.PermissionRequested += OnWebViewPermissionRequested;
            _webView.CoreWebView2.Navigate(_settings.DiscordUrl);
            AppendLog($"WebView2 initialized with persistent profile at {AppSettings.DefaultWebViewProfilePath}.");
        }
        catch (Exception ex)
        {
            AppendLog($"WebView2 initialization failed: {ex.Message}");
            MessageBox.Show(
                $"Could not initialize Microsoft Edge WebView2:\n{ex.Message}\n\nMake sure the Evergreen WebView2 Runtime is installed.",
                "DiscordShield — WebView2 Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void ApplyCaptureExclusionState()
    {
        AffinityResult result = _settings.ExcludeWrapperFromCapture
            ? _captureService.EnableExcludeFromCapture(Handle)
            : _captureService.DisableExcludeFromCapture(Handle);

        bool active = _settings.ExcludeWrapperFromCapture && result.Success && result.ActualAffinity == Win32Interop.WDA_EXCLUDEFROMCAPTURE;

        if (active)
        {
            _lblShieldStatus.Text = "● SHIELD ACTIVE — Hidden from Upwork Screenshots";
            _lblShieldStatus.ForeColor = Color.FromArgb(35, 165, 90);
            _btnToggleShield.Text = "Shield: ON";
            _btnToggleShield.BackColor = Color.FromArgb(35, 165, 90);
            _trayIcon.Text = "DiscordShield — Capture Exclusion ACTIVE";
        }
        else if (!_settings.ExcludeWrapperFromCapture)
        {
            _lblShieldStatus.Text = "○ SHIELD OFF — Visible to Screenshots";
            _lblShieldStatus.ForeColor = Color.FromArgb(240, 178, 50);
            _btnToggleShield.Text = "Shield: OFF";
            _btnToggleShield.BackColor = Color.FromArgb(180, 120, 20);
            _trayIcon.Text = "DiscordShield — Capture Exclusion OFF";
        }
        else
        {
            _lblShieldStatus.Text = "✕ SHIELD ERROR — Check Log";
            _lblShieldStatus.ForeColor = Color.FromArgb(242, 63, 67);
            _btnToggleShield.Text = "Shield: ERR";
            _btnToggleShield.BackColor = Color.FromArgb(218, 55, 60);
            _trayIcon.Text = "DiscordShield — Capture Exclusion ERROR";
        }

        AppendLog(result.Message);
    }

    private void EnforceAffinityWatchdog()
    {
        if (!_settings.ExcludeWrapperFromCapture || !IsHandleCreated)
        {
            return;
        }

        AffinityResult current = _captureService.ReadAffinity(Handle);
        if (!current.Success || current.ActualAffinity != Win32Interop.WDA_EXCLUDEFROMCAPTURE)
        {
            AppendLog("Affinity drift detected by watchdog — re-applying WDA_EXCLUDEFROMCAPTURE.");
            ApplyCaptureExclusionState();
        }
    }

    private void ToggleLogPanel()
    {
        _txtLog.Visible = !_txtLog.Visible;
    }

    private void ToggleCaptureExclusion()
    {
        _settings.ExcludeWrapperFromCapture = !_settings.ExcludeWrapperFromCapture;
        _settings.Save();
        ApplyCaptureExclusionState();
    }

    private async Task RunLiveCaptureVerificationAsync()
    {
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        _btnVerify.Enabled = false;
        try
        {
            AffinityResult affinity = _captureService.ReadAffinity(Handle);
            _sentinelOverlay.Visible = true;
            _sentinelOverlay.BringToFront();
            _sentinelOverlay.Refresh();
            await Task.Delay(150);

            Point topLeft = _sentinelOverlay.PointToScreen(Point.Empty);
            var screenRect = new Rectangle(topLeft, _sentinelOverlay.Size);
            CaptureVerificationResult capture = _captureService.VerifyRegionExcludesColor(screenRect, Color.Magenta);

            _sentinelOverlay.Visible = false;

            bool affinityOk = affinity.Success && affinity.ActualAffinity == Win32Interop.WDA_EXCLUDEFROMCAPTURE;
            bool fullyVerified = affinityOk && capture.Passed;

            string verdictText = fullyVerified
                ? "VERIFIED: Windows DWM excluded this window from screen capture. Upwork screenshots will see what is behind this window instead of Discord."
                : capture.CaptureUnavailable && affinityOk
                    ? "AFFINITY CONFIRMED (0x11), PIXEL CHECK DEFERRED: Desktop capture is unavailable right now (locked/non-interactive desktop). Re-run Verify Now on your active desktop for pixel confirmation."
                    : "WARNING: This window appeared in the live screenshot capture! Make sure Shield is ON and Windows 10 2004+ / Windows 11 is active.";

            string report =
                $"Affinity Readback: 0x{affinity.ActualAffinity:X} ({(affinityOk ? "WDA_EXCLUDEFROMCAPTURE" : "NOT EXCLUDED")})\n" +
                $"Live Windows Screenshot Check: {capture.Summary}\n\n" +
                verdictText;

            AppendLog($"[Verify] {capture.Summary} (affinity=0x{affinity.ActualAffinity:X})");
            MessageBox.Show(
                report,
                fullyVerified ? "DiscordShield — Verified Hidden" : "DiscordShield — Verification Warning",
                MessageBoxButtons.OK,
                fullyVerified ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _sentinelOverlay.Visible = false;
            _btnVerify.Enabled = true;
        }
    }

    private void RunNativeGuardTick()
    {
        IReadOnlyList<WindowSnapshot> windows = _windowInspector.GetTopLevelWindows();
        IReadOnlyList<string> procs = _windowInspector.GetRunningProcessNames();
        IReadOnlyList<ScreenSnapshot> screens = _windowInspector.GetScreens();

        GuardEvaluation eval = _guardEngine.Evaluate(windows, procs, screens, _settings, DateTimeOffset.UtcNow);
        _lblGuardStatus.Text = eval.UpworkRunning
            ? $"Upwork: ACTIVE | Native Discord: {eval.ExposedNativeWindows.Count}"
            : $"Upwork: Not Running | Native Discord: {eval.ExposedNativeWindows.Count}";
        _lblGuardStatus.ForeColor = eval.ExposedNativeWindows.Count > 0 && eval.UpworkRunning
            ? Color.FromArgb(242, 63, 67)
            : Color.FromArgb(181, 186, 193);

        foreach (GuardActionItem item in eval.Actions)
        {
            AppendLog($"[Guard] {item.Reason}");

            switch (item.EffectiveAction)
            {
                case NativeGuardAction.AutoMinimize:
                    bool minOk = _windowInspector.MinimizeWindow(item.Window.Handle);
                    AppendLog(minOk
                        ? $"[Guard] Minimized native '{item.Window.ProcessName}'."
                        : $"[Guard] Could not minimize '{item.Window.ProcessName}'.");
                    _trayIcon.ShowBalloonTip(2500, "DiscordShield — Minimized Native Discord", item.Reason, ToolTipIcon.Info);
                    break;

                case NativeGuardAction.MoveToSecondaryMonitor when item.TargetScreen is not null:
                    bool moveOk = _windowInspector.MoveWindowToScreen(item.Window.Handle, item.Window.Bounds, item.TargetScreen);
                    AppendLog(moveOk
                        ? $"[Guard] Moved '{item.Window.ProcessName}' to {item.TargetScreen.DeviceName}."
                        : $"[Guard] Failed to move '{item.Window.ProcessName}' to {item.TargetScreen.DeviceName}.");
                    break;

                case NativeGuardAction.PauseUpworkAndAlert:
                    UpworkPauseResult pause = _upworkAutomation.TryPauseUpworkTracker(windows, _settings.UpworkProcessNames);
                    AppendLog($"[Upwork UIA] {pause.Message}");
                    _trayIcon.ShowBalloonTip(3500, "DiscordShield — Native Discord Exposed", $"{item.Reason}\n{pause.Message}", ToolTipIcon.Warning);
                    break;

                case NativeGuardAction.AlertOnly:
                default:
                    _trayIcon.ShowBalloonTip(3500, "DiscordShield — Native Discord Exposed", item.Reason, ToolTipIcon.Warning);
                    break;
            }
        }
    }

    private void OpenSettings()
    {
        using var dialog = new SettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _settings = dialog.UpdatedSettings;
        _settings.Save();
        _guardEngine.ResetCooldowns();
        ApplyCaptureExclusionState();
        SyncGlobalHotkey();
        AppendLog("Settings updated and saved.");
    }

    private void ToggleWindowVisibility()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            Hide();
        }
        else
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            ApplyCaptureExclusionState();
        }
    }

    private void SyncGlobalHotkey()
    {
        UnregisterGlobalHotkey();
        if (_settings.EnableGlobalHotkey && IsHandleCreated)
        {
            _hotkeyRegistered = Win32Interop.RegisterHotKey(
                Handle,
                HotkeyId,
                Win32Interop.MOD_CONTROL | Win32Interop.MOD_SHIFT | Win32Interop.MOD_NOREPEAT,
                VkD);
            AppendLog(_hotkeyRegistered
                ? "Global hotkey Ctrl+Shift+D registered."
                : "Could not register global hotkey Ctrl+Shift+D (already in use by another app).");
        }
    }

    private void UnregisterGlobalHotkey()
    {
        if (_hotkeyRegistered && IsHandleCreated)
        {
            _ = Win32Interop.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (_txtLog.IsDisposed)
        {
            return;
        }

        if (_txtLog.InvokeRequired)
        {
            _txtLog.BeginInvoke(() => AppendLog(message));
            return;
        }

        _txtLog.AppendText(line + Environment.NewLine);
    }

    private void OnWebViewNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Never allow WebView2 to spawn an unshielded top-level popup window.
        e.Handled = true;
        NewWindowDecision decision = WebViewPolicy.DecideNewWindow(e.Uri);

        if (decision == NewWindowDecision.NavigateInPlace)
        {
            _webView.CoreWebView2.Navigate(e.Uri);
            AppendLog($"[WebView2] Contained Discord popup in-place: {e.Uri}");
        }
        else if (decision == NewWindowDecision.OpenExternalBrowser)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true });
                AppendLog($"[WebView2] Opened external link in default browser: {e.Uri}");
            }
            catch (Exception ex)
            {
                AppendLog($"[WebView2] Failed to open external link ({ex.Message}).");
            }
        }
        else
        {
            AppendLog($"[WebView2] Blocked unsafe popup URI: {e.Uri}");
        }
    }

    private void OnWebViewPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        if (!WebViewPolicy.IsDiscordOrigin(e.Uri))
        {
            return;
        }

        if (e.PermissionKind is CoreWebView2PermissionKind.Microphone
            or CoreWebView2PermissionKind.Camera
            or CoreWebView2PermissionKind.Notifications)
        {
            e.State = CoreWebView2PermissionState.Allow;
            AppendLog($"[WebView2] Granted {e.PermissionKind} permission for trusted Discord origin.");
        }
    }

    private static Icon CreateShieldIcon(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Point[] shield =
            [
                new Point(16, 2),
                new Point(28, 7),
                new Point(26, 20),
                new Point(16, 30),
                new Point(6, 20),
                new Point(4, 7)
            ];

            using var brush = new SolidBrush(active ? Color.FromArgb(35, 165, 90) : Color.FromArgb(242, 63, 67));
            using var pen = new Pen(Color.White, 2f);
            g.FillPolygon(brush, shield);
            g.DrawPolygon(pen, shield);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            _ = Win32Interop.DestroyIcon(hIcon);
        }
    }
}
