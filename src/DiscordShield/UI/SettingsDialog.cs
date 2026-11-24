using DiscordShield.Core;

namespace DiscordShield.UI;

public sealed class SettingsDialog : Form
{
    private readonly CheckBox _chkExcludeWrapper;
    private readonly CheckBox _chkNativeGuard;
    private readonly ComboBox _cmbAction;
    private readonly CheckBox _chkOnlyWhenUpwork;
    private readonly TextBox _txtProtectedProcesses;
    private readonly TextBox _txtUpworkProcesses;
    private readonly CheckBox _chkMinimizeToTray;
    private readonly CheckBox _chkHotkey;
    private readonly NumericUpDown _numCooldown;

    public AppSettings UpdatedSettings { get; private set; }

    public SettingsDialog(AppSettings current)
    {
        UpdatedSettings = new AppSettings
        {
            ExcludeWrapperFromCapture = current.ExcludeWrapperFromCapture,
            DiscordUrl = current.DiscordUrl,
            NativeGuardEnabled = current.NativeGuardEnabled,
            NativeGuardAction = current.NativeGuardAction,
            OnlyActWhenUpworkRunning = current.OnlyActWhenUpworkRunning,
            ProtectedProcessNames = [.. current.ProtectedProcessNames],
            UpworkProcessNames = [.. current.UpworkProcessNames],
            MinimizeToTrayOnClose = current.MinimizeToTrayOnClose,
            EnableGlobalHotkey = current.EnableGlobalHotkey,
            GuardCooldownSeconds = current.GuardCooldownSeconds
        };

        Text = "DiscordShield Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(640, 0);
        Padding = new Padding(18);
        BackColor = Color.FromArgb(30, 31, 34);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 9.5f);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _chkExcludeWrapper = AddCheckBox(layout, "Exclude this Discord window from all Windows screenshots (WDA_EXCLUDEFROMCAPTURE)", current.ExcludeWrapperFromCapture);
        _chkNativeGuard = AddCheckBox(layout, "Monitor native Discord desktop app (Discord.exe)", current.NativeGuardEnabled);
        _chkOnlyWhenUpwork = AddCheckBox(layout, "Only trigger Native Guard actions while Upwork is running", current.OnlyActWhenUpworkRunning);

        AddLabel(layout, "Action when native Discord.exe is visible:");
        _cmbAction = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(43, 45, 49),
            ForeColor = Color.White,
            Margin = new Padding(0, 2, 0, 10)
        };
        _cmbAction.Items.AddRange(new object[]
        {
            "Alert Only (Recommended — warn & offer 1-click minimize)",
            "Auto-Minimize native Discord.exe",
            "Move native Discord.exe to Secondary Monitor (requires 2+ monitors)",
            "Pause Upwork Time Tracker (UI Automation) + Alert"
        });
        _cmbAction.SelectedIndex = (int)current.NativeGuardAction;
        layout.Controls.Add(_cmbAction);

        AddLabel(layout, "Protected native process names (comma-separated):");
        _txtProtectedProcesses = AddTextBox(layout, string.Join(", ", current.ProtectedProcessNames));

        AddLabel(layout, "Upwork process names (comma-separated):");
        _txtUpworkProcesses = AddTextBox(layout, string.Join(", ", current.UpworkProcessNames));

        var cooldownRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 10)
        };
        cooldownRow.Controls.Add(new Label
        {
            Text = "Guard cooldown (seconds):",
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Padding = new Padding(0, 4, 10, 0),
            ForeColor = Color.FromArgb(181, 186, 193)
        });
        _numCooldown = new NumericUpDown
        {
            Width = 90,
            Minimum = 1,
            Maximum = 300,
            Value = Math.Clamp(current.GuardCooldownSeconds, 1, 300),
            BackColor = Color.FromArgb(43, 45, 49),
            ForeColor = Color.White
        };
        cooldownRow.Controls.Add(_numCooldown);
        layout.Controls.Add(cooldownRow);

        _chkMinimizeToTray = AddCheckBox(layout, "Minimize to system tray when clicking Close (X)", current.MinimizeToTrayOnClose);
        _chkHotkey = AddCheckBox(layout, "Enable global show/hide hotkey (Ctrl + Shift + D)", current.EnableGlobalHotkey);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 14, 0, 0)
        };

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(78, 80, 88),
            ForeColor = Color.White
        };
        btnCancel.FlatAppearance.BorderSize = 0;

        var btnSave = new Button
        {
            Text = "Save Settings",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 6, 14, 6),
            Margin = new Padding(0),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 165, 90),
            ForeColor = Color.White
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => CommitSettings();

        buttonRow.Controls.Add(btnCancel);
        buttonRow.Controls.Add(btnSave);
        layout.Controls.Add(buttonRow);

        Controls.Add(layout);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private static CheckBox AddCheckBox(TableLayoutPanel layout, string text, bool isChecked)
    {
        var chk = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            Margin = new Padding(0, 3, 0, 5),
            ForeColor = Color.FromArgb(242, 243, 245)
        };
        layout.Controls.Add(chk);
        return chk;
    }

    private static void AddLabel(TableLayoutPanel layout, string text)
    {
        layout.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 2),
            ForeColor = Color.FromArgb(181, 186, 193)
        });
    }

    private static TextBox AddTextBox(TableLayoutPanel layout, string value)
    {
        var txt = new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 8),
            BackColor = Color.FromArgb(43, 45, 49),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        layout.Controls.Add(txt);
        return txt;
    }

    private void CommitSettings()
    {
        UpdatedSettings.ExcludeWrapperFromCapture = _chkExcludeWrapper.Checked;
        UpdatedSettings.NativeGuardEnabled = _chkNativeGuard.Checked;
        UpdatedSettings.OnlyActWhenUpworkRunning = _chkOnlyWhenUpwork.Checked;
        UpdatedSettings.NativeGuardAction = (NativeGuardAction)Math.Clamp(_cmbAction.SelectedIndex, 0, 3);
        UpdatedSettings.ProtectedProcessNames = _txtProtectedProcesses.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        UpdatedSettings.UpworkProcessNames = _txtUpworkProcesses.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        UpdatedSettings.GuardCooldownSeconds = (int)_numCooldown.Value;
        UpdatedSettings.MinimizeToTrayOnClose = _chkMinimizeToTray.Checked;
        UpdatedSettings.EnableGlobalHotkey = _chkHotkey.Checked;
        UpdatedSettings.Normalize();
    }
}
