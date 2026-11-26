namespace KeyLend;

internal sealed partial class DeskSettings : Form
{
    private readonly DeskForm _desk;
    private readonly Font _bodyFont, _sectionFont;
    private readonly ComboBox _language = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _notifications = new();
    private readonly Button _shortcut = new();
    private readonly TextBox _entry = new();
    private readonly Button _reset = new();
    private readonly Button _flip = new();
    private readonly Button _shown = new();
    private readonly Button _hidden = new();
    private readonly Button _apply = new();
    private readonly Button _defaultKey = new();
    private readonly Button _done = new() { DialogResult = DialogResult.OK };
    private readonly Label _message = new() { AutoEllipsis = true, UseMnemonic = false };
    private readonly List<(Control Control, string Zh, string En)> _labels = new();
    private Func<string>? _feedback;
    private bool _recording, _loaded;

    internal DeskSettings(DeskForm desk)
    {
        _desk = desk;
        Icon = desk.Icon;
        _bodyFont = new Font(desk.Font.FontFamily, 10.5f);
        _sectionFont = new Font(desk.Font.FontFamily, 12f, FontStyle.Bold);
        Font = _bodyFont;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BuildLayout();
        _language.Items.AddRange(["简体中文", "English"]);
        _language.AccessibleName = "语言 / Language";
        _entry.Text = desk.ShortcutLabel;
        _startWithWindows.Enabled = StartupPreference.TryRead(out bool startsWithWindows);
        _startWithWindows.Checked = startsWithWindows;
        if (!_startWithWindows.Enabled)
            SetFeedback(() => UiLanguage.T("无法读取开机自启设置。", "Could not read the startup setting."));
        _startWithWindows.Click += (_, _) =>
        {
            CancelRecording();
            if (!StartupPreference.TrySet(_startWithWindows.Checked, out var error))
                _startWithWindows.Checked = !_startWithWindows.Checked;
            SetFeedback(error);
        };
        _notifications.Checked = ConnectionNotifications.Enabled;
        _notifications.Click += (_, _) =>
        {
            CancelRecording();
            if (!ConnectionNotifications.TrySet(_notifications.Checked, out string error))
            {
                _notifications.Checked = ConnectionNotifications.Enabled;
                SetFeedback(() => error);
            }
            else SetFeedback(null);
        };
        _language.SelectionChangeCommitted += (_, _) =>
        {
            if (!UiLanguage.TrySet(_language.SelectedIndex == 1, out string error))
            {
                _language.SelectedIndex = UiLanguage.English ? 1 : 0;
                SetFeedback(() => error);
            }
        };
        _shortcut.Click += (_, _) =>
        {
            _recording = true;
            RefreshRecordButton();
            SetFeedback(null);
        };
        _apply.Click += (_, _) =>
        {
            if (DeskShortcut.TryParse(_entry.Text, out var value)) SaveKey(value);
            else SetFeedback(() => UiLanguage.T("请填写 Ctrl、Alt 或 Win 加一个普通键，可再加 Shift。",
                "Use Ctrl, Alt or Win with a key. Shift is optional."));
        };
        _defaultKey.Click += (_, _) => SaveKey(DeskShortcut.Default);
        _reset.Click += async (_, _) =>
        {
            CancelRecording();
            Func<string> pending = () => UiLanguage.T("正在尝试将输入交还原 iPhone，然后清除绑定并重新检测…",
                "Returning input to the previous iPhone, then clearing the binding and scanning…");
            SetFeedback(pending);
            string result = await _desk.ResetBinding();
            if (IsDisposed || Disposing || !Visible || !ReferenceEquals(_feedback, pending)) return;
            if (_desk.HasBinding) SetFeedback(() => result);
            else SetFeedback(() => _desk.BindingStatus);
        };
        _toggleCleared.Click += ToggleClearedKeyboard;
        _flip.Click += (_, _) =>
        {
            CancelRecording();
            _desk.FlipScreenKeyboard();
        };
        _shown.Click += (_, _) => Calibrate(true);
        _hidden.Click += (_, _) => Calibrate(false);
        UiLanguage.Changed += ApplyLanguage;
        ApplyLanguage();
        RefreshState();
    }

    private T Bind<T>(T control, string zh, string en) where T : Control
    {
        _labels.Add((control, zh, en));
        return control;
    }

    private void ApplyLanguage()
    {
        Text = UiLanguage.T("借键设置", "KeyIn Settings");
        foreach (var (control, zh, en) in _labels)
            control.Text = UiLanguage.T(zh, en);
        _language.SelectedIndex = UiLanguage.English ? 1 : 0;
        _entry.AccessibleName = UiLanguage.T("全局快捷键组合", "Global shortcut");
        RefreshRecordButton();
        _message.Text = _feedback?.Invoke() ?? "";
        RefreshDevices();
        FitDialog();
    }

    protected override void Dispose(bool disposing)
    {
        bool release = disposing && !IsDisposed;
        if (release)
        {
            UiLanguage.Changed -= ApplyLanguage;
            _feedbackTip.Dispose();
        }
        base.Dispose(disposing);
        if (release)
        {
            _sectionFont.Dispose();
            _bodyFont.Dispose();
        }
    }

    internal void RefreshState()
    {
        if (!ReferenceEquals(Icon, _desk.Icon)) Icon = _desk.Icon;
        _reset.Enabled = _desk.CanReset && !_choosing && !_recovering;
        RefreshDevices();
        _flip.Enabled = _shown.Enabled = _hidden.Enabled = _desk.CanCalibrate;
    }

    internal void RegisteredKeyPressed()
    {
        if (_recording) SaveKey(_desk.ShortcutValue);
    }

    internal void SetFeedback(Func<string>? text)
    {
        _feedback = text;
        _message.Text = text?.Invoke() ?? "";
        FitDialog();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_recording) return base.ProcessCmdKey(ref msg, keyData);
        var key = keyData & Keys.KeyCode;
        if (keyData == Keys.Escape) CancelRecording();
        else if (key is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin))
            SaveKey(DeskShortcut.WithWindowsModifier(keyData));
        return true;
    }

    private void SaveKey(Keys value)
    {
        string? error = _desk.ChangeShortcut(value);
        SetFeedback(error is null
            ? () => UiLanguage.T("快捷键已保存。", "Shortcut saved.")
            : () => DeskShortcut.TranslateChangeError(error));
        if (error is null) _entry.Text = _desk.ShortcutLabel;
        CancelRecording();
    }

    private void CancelRecording()
    {
        _recording = false;
        RefreshRecordButton();
    }

    private void RefreshRecordButton() => _shortcut.Text = _recording
        ? UiLanguage.T("请按新组合…", "Press a shortcut…")
        : UiLanguage.T("按键录入", "Record shortcut");

    private void Calibrate(bool shown)
    {
        CancelRecording();
        _desk.CalibrateScreenKeyboard(shown);
        SetFeedback(() => shown ? UiLanguage.T("已按屏幕键盘显示校准。", "Keyboard state set to visible.")
            : UiLanguage.T("已按屏幕键盘收起校准。", "Keyboard state set to hidden."));
    }
}
