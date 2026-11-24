using DiscordShield.Core;
using DiscordShield.Native;
using Microsoft.Win32;

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
    private readonly MinimalButton _btnSave;
    private readonly MinimalButton _btnCancel;
    private readonly List<Label> _subLabels = [];
    private ThemePalette _palette;

    public AppSettings UpdatedSettings { get; private set; }

    public SettingsDialog(AppSettings current, ThemePalette? palette = null)
    {
        _palette = palette ?? DesignTokens.SystemDefault();
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

        Text = "Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = new Size(580, 0);
        Padding = new Padding(DesignTokens.DialogPadding);
        Font = DesignTokens.CreateUiFont(9.5f);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            BackColor = Color.Transparent
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

        _chkExcludeWrapper = AddCheckBox(layout, "Hide this Discord window from screenshots", current.ExcludeWrapperFromCapture);
        _chkNativeGuard = AddCheckBox(layout, "Watch for regular Discord app (Discord.exe)", current.NativeGuardEnabled);
        _chkOnlyWhenUpwork = AddCheckBox(layout, "Only watch while Upwork is running", current.OnlyActWhenUpworkRunning);

        AddSubLabel(layout, "When regular Discord is open:");
        _cmbAction = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            Margin = new Padding(0, 4, 0, 12)
        };
        _cmbAction.Items.AddRange(new object[]
        {
            "Warn me (Recommended)",
            "Auto-minimize regular Discord",
            "Move regular Discord to 2nd monitor",
            "Pause Upwork & warn me"
        });
        _cmbAction.SelectedIndex = (int)current.NativeGuardAction;
        layout.Controls.Add(_cmbAction);

        AddSubLabel(layout, "Watched desktop apps (comma-separated):");
        _txtProtectedProcesses = AddTextBox(layout, string.Join(", ", current.ProtectedProcessNames));

        AddSubLabel(layout, "Time tracker process names:");
        _txtUpworkProcesses = AddTextBox(layout, string.Join(", ", current.UpworkProcessNames));

        var cooldownRow = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Margin = new Padding(0, 4, 0, 12)
        };
        var lblCooldown = new Label
        {
            Text = "Alert cooldown (seconds):",
            AutoSize = true,
            Padding = new Padding(0, 4, 12, 0)
        };
        _subLabels.Add(lblCooldown);
        _numCooldown = new NumericUpDown
        {
            Width = 84,
            Minimum = 1,
            Maximum = 300,
            Value = Math.Clamp(current.GuardCooldownSeconds, 1, 300),
            BorderStyle = BorderStyle.FixedSingle
        };
        cooldownRow.Controls.Add(lblCooldown);
        cooldownRow.Controls.Add(_numCooldown);
        layout.Controls.Add(cooldownRow);

        _chkMinimizeToTray = AddCheckBox(layout, "Keep running in system tray when closed", current.MinimizeToTrayOnClose);
        _chkHotkey = AddCheckBox(layout, "Quick show/hide shortcut (Ctrl + Shift + D)", current.EnableGlobalHotkey);

        var buttonRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = new Padding(0, 16, 0, 0)
        };

        _btnSave = new MinimalButton("Save", ButtonTone.Accent);
        _btnSave.Click += (_, _) =>
        {
            CommitSettings();
            DialogResult = DialogResult.OK;
            Close();
        };

        _btnCancel = new MinimalButton("Cancel", ButtonTone.Quiet);
        _btnCancel.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        buttonRow.Controls.Add(_btnSave);
        buttonRow.Controls.Add(_btnCancel);
        layout.Controls.Add(buttonRow);

        Controls.Add(layout);
        ApplyTheme(_palette);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32Interop.ApplyWindows11Chrome(Handle, _palette.IsDark, _palette.BaseSurface, _palette.TextPrimary);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
        }
        else if (e.KeyCode == Keys.Enter && ActiveControl is not ComboBox)
        {
            CommitSettings();
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.BaseSurface;
        ForeColor = palette.TextPrimary;

        foreach (Label lbl in _subLabels)
        {
            lbl.ForeColor = palette.TextSub;
        }

        _chkExcludeWrapper.ForeColor = palette.TextPrimary;
        _chkNativeGuard.ForeColor = palette.TextPrimary;
        _chkOnlyWhenUpwork.ForeColor = palette.TextPrimary;
        _chkMinimizeToTray.ForeColor = palette.TextPrimary;
        _chkHotkey.ForeColor = palette.TextPrimary;

        _cmbAction.BackColor = palette.InputSurface;
        _cmbAction.ForeColor = palette.TextPrimary;
        _txtProtectedProcesses.BackColor = palette.InputSurface;
        _txtProtectedProcesses.ForeColor = palette.TextPrimary;
        _txtUpworkProcesses.BackColor = palette.InputSurface;
        _txtUpworkProcesses.ForeColor = palette.TextPrimary;
        _numCooldown.BackColor = palette.InputSurface;
        _numCooldown.ForeColor = palette.TextPrimary;

        _btnSave.ApplyTheme(palette);
        _btnCancel.ApplyTheme(palette);

        if (IsHandleCreated)
        {
            Win32Interop.ApplyWindows11Chrome(Handle, palette.IsDark, palette.BaseSurface, palette.TextPrimary);
        }
    }

    private CheckBox AddCheckBox(TableLayoutPanel layout, string text, bool isChecked)
    {
        var chk = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 6)
        };
        layout.Controls.Add(chk);
        return chk;
    }

    private void AddSubLabel(TableLayoutPanel layout, string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 2)
        };
        _subLabels.Add(lbl);
        layout.Controls.Add(lbl);
    }

    private TextBox AddTextBox(TableLayoutPanel layout, string value)
    {
        var txt = new TextBox
        {
            Text = value,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 8),
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
