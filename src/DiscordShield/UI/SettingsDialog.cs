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
        ClientSize = new Size(490, 440);
        BackColor = Color.FromArgb(30, 31, 34);
        ForeColor = Color.FromArgb(242, 243, 245);
        Font = new Font("Segoe UI", 9.5f);

        int y = 18;
        _chkExcludeWrapper = AddCheckBox("Exclude this Discord window from all Windows screenshots (WDA_EXCLUDEFROMCAPTURE)", current.ExcludeWrapperFromCapture, ref y);
        _chkNativeGuard = AddCheckBox("Monitor native Discord desktop app (Discord.exe)", current.NativeGuardEnabled, ref y);
        _chkOnlyWhenUpwork = AddCheckBox("Only trigger Native Guard actions while Upwork is running", current.OnlyActWhenUpworkRunning, ref y);

        AddLabel("Action when native Discord.exe is visible:", ref y);
        _cmbAction = new ComboBox
        {
            Location = new Point(20, y),
            Width = 445,
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = Color.FromArgb(43, 45, 49),
            ForeColor = Color.White
        };
        _cmbAction.Items.AddRange(new object[]
        {
            "Alert Only (Recommended — warn & offer 1-click minimize)",
            "Auto-Minimize native Discord.exe",
            "Move native Discord.exe to Secondary Monitor (requires 2+ monitors)",
            "Pause Upwork Time Tracker (UI Automation) + Alert"
        });
        _cmbAction.SelectedIndex = (int)current.NativeGuardAction;
        Controls.Add(_cmbAction);
        y += 38;

        AddLabel("Protected native process names (comma-separated):", ref y);
        _txtProtectedProcesses = AddTextBox(string.Join(", ", current.ProtectedProcessNames), ref y);

        AddLabel("Upwork process names (comma-separated):", ref y);
        _txtUpworkProcesses = AddTextBox(string.Join(", ", current.UpworkProcessNames), ref y);

        var lblCooldown = new Label
        {
            Text = "Guard cooldown (seconds):",
            Location = new Point(20, y + 4),
            AutoSize = true
        };
        _numCooldown = new NumericUpDown
        {
            Location = new Point(200, y),
            Width = 80,
            Minimum = 1,
            Maximum = 300,
            Value = Math.Clamp(current.GuardCooldownSeconds, 1, 300),
            BackColor = Color.FromArgb(43, 45, 49),
            ForeColor = Color.White
        };
        Controls.Add(lblCooldown);
        Controls.Add(_numCooldown);
        y += 38;

        _chkMinimizeToTray = AddCheckBox("Minimize to system tray when clicking Close (X)", current.MinimizeToTrayOnClose, ref y);
        _chkHotkey = AddCheckBox("Enable global show/hide hotkey (Ctrl + Shift + D)", current.EnableGlobalHotkey, ref y);

        var btnSave = new Button
        {
            Text = "Save Settings",
            DialogResult = DialogResult.OK,
            Location = new Point(265, 392),
            Size = new Size(105, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(35, 165, 90),
            ForeColor = Color.White
        };
        btnSave.FlatAppearance.BorderSize = 0;
        btnSave.Click += (_, _) => CommitSettings();

        var btnCancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(380, 392),
            Size = new Size(85, 32),
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(78, 80, 88),
            ForeColor = Color.White
        };
        btnCancel.FlatAppearance.BorderSize = 0;

        Controls.Add(btnSave);
        Controls.Add(btnCancel);
        AcceptButton = btnSave;
        CancelButton = btnCancel;
    }

    private CheckBox AddCheckBox(string text, bool isChecked, ref int y)
    {
        var chk = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            Location = new Point(20, y),
            Size = new Size(450, 26),
            ForeColor = Color.FromArgb(242, 243, 245)
        };
        Controls.Add(chk);
        y += 28;
        return chk;
    }

    private void AddLabel(string text, ref int y)
    {
        var lbl = new Label
        {
            Text = text,
            Location = new Point(20, y),
            AutoSize = true,
            ForeColor = Color.FromArgb(181, 186, 193)
        };
        Controls.Add(lbl);
        y += 22;
    }

    private TextBox AddTextBox(string value, ref int y)
    {
        var txt = new TextBox
        {
            Text = value,
            Location = new Point(20, y),
            Width = 445,
            BackColor = Color.FromArgb(43, 45, 49),
            ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle
        };
        Controls.Add(txt);
        y += 32;
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
