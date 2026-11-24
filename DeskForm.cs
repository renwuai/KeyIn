using static KeyLend.UiLanguage;

namespace KeyLend;

public sealed partial class DeskForm : Form
{
    private readonly RadioLink _radio = new();
    private readonly KeyboardCapture _capture = new();
    private readonly InputLanguageSession _language = new();
    private readonly DeskShortcut _shortcut = new();
    private readonly Queue<(byte[] Data, bool Text)> _reports = new();
    private readonly Label _heading = new() { AutoSize = true };
    private readonly Label _detail = new() { AutoEllipsis = true, Dock = DockStyle.Fill };
    private readonly Label _hint = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button _preferences = new() { Text = "设置", AutoSize = true, TabStop = false };
    private readonly NotifyIcon _tray;
    private readonly Icon _appIcon = new(typeof(DeskForm), "KeyLend.ico");
    private InputStatus? _trayStatus;
    private readonly TaskbarStatus _taskbar = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolTip _tip = new();
    private DeskSettings? _settings;
    private bool _started, _starting, _editing, _foreground, _resetting, _exiting, _allowClose;
    private bool _ready, _languageActive, _lastQueuedRelease, _manualScreen, _flipPending, _screenFailed;
    // null 表示本次连接还没有可靠的显隐依据。
    private bool? _screenShown;
    private bool _canEject;
    private bool _preparingReset;
    private bool _wasMinimized;
    private long _generation = -1;
    private long _epoch;
    private TaskCompletionSource? _drain;
    private Func<string>? _error;

    public DeskForm()
    {
        Text = UiLanguage.Brand;
        Icon = _appIcon;
        Font = new Font("Microsoft YaHei UI", 10);
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(414, 291);
        BuildDeskLayout();
        _menu.Items.Add("显示借键", null, (_, _) => Reveal());
        _menu.Items.Add("退出借键", null, (_, _) => ExitGracefully());
        _tray = new NotifyIcon { Text = "借键", ContextMenuStrip = _menu };
        _tray.DoubleClick += (_, _) => Reveal();
        _preferences.Click += (_, _) => OpenPreferences();
        _capture.Enabled = false;
        _capture.IsShortcut = keys => _shortcut.Id != 0 && DeskShortcut.WithWindowsModifier(keys) == _shortcut.Value;
        _capture.Report += OnReport;
        _capture.Pause += MinimizeToTaskbar;
        _radio.Changed += RadioChanged;
        _language.RememberForeground();
        UiLanguage.Changed += ApplyLanguage;
        ApplyLanguage();
    }

    internal Keys ShortcutValue => _shortcut.Value;
    internal string BindingStatus => _radio.Status;
    internal IReadOnlyList<PhoneChoice> Candidates => _radio.Candidates;
    internal bool HasBinding => _radio.HasBinding;
    private bool CanChooseDevice => !HasBinding && Candidates.Count > 0;
    internal string ShortcutLabel => DeskShortcut.Format(ShortcutValue);
    internal bool CanReset => _started && !_starting && !_preparingReset && !_resetting && !_exiting;
    internal bool CanCalibrate => CanReset && _ready && _radio.CanEject && _drain is null;
    internal bool CanToggleClearedKeyboard => CanReset && _radio.CanToggleClearedKeyboard;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _shortcut.Load(Handle);
        _tray.Visible = true;
        Application.AddMessageFilter(_capture);
        _started = _starting = true;
        StartRadio();
    }

    private async void StartRadio()
    {
        try { await _radio.StartAsync(); }
        catch (Exception) { _error = () => T("蓝牙启动失败，可在设置中重新检测。", "Bluetooth could not start. Try again in Settings."); }
        finally
        {
            _starting = false;
            if (!IsDisposed && !_allowClose) ReadRadioState();
        }
    }

    public void Reveal()
    {
        if (_exiting || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)Reveal); return; }
        if (_settings is { Visible: true } dialog) { dialog.Activate(); return; }
        if (!_foreground) _language.RememberForeground();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    private void MinimizeToTaskbar()
    {
        if (_editing || _exiting) return;
        _foreground = false;
        SyncInput();
        // 保留任务栏按钮，让窗口收起后仍可显示实时状态角标。
        WindowState = FormWindowState.Minimized;
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        _foreground = true;
        SyncInput();
    }

    protected override void OnDeactivate(EventArgs e)
    {
        _foreground = false;
        SyncInput();
        base.OnDeactivate(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        bool minimized = WindowState == FormWindowState.Minimized;
        if (!_started || minimized == _wasMinimized) return;
        _wasMinimized = minimized;
        if (minimized) _foreground = false;
        SyncInput();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == TaskbarStatus.ButtonCreatedMessage && _tray is not null)
        {
            base.WndProc(ref m);
            UpdateStatusIcons(CurrentInputStatus, taskbarCreated: true);
            return;
        }
        const int hotkeyMessage = 0x0312;
        if (m.Msg == hotkeyMessage && _shortcut is { Id: not 0 } && m.WParam.ToInt32() == _shortcut.Id)
        {
            if (_editing) _settings?.RegisteredKeyPressed();
            else if (_foreground && Visible && WindowState != FormWindowState.Minimized) MinimizeToTaskbar();
            else Reveal();
            return;
        }
        base.WndProc(ref m);
    }

    private void OpenPreferences()
    {
        if (_editing || _exiting) return;
        _editing = true;
        SyncInput();
        try
        {
            using var dialog = new DeskSettings(this);
            _settings = dialog;
            dialog.ShowDialog(this);
        }
        finally
        {
            _settings = null;
            _manualScreen = _editing = false;
            _screenFailed = false;
            if (!IsDisposed && !_allowClose) SyncInput();
        }
    }

    internal string? ChangeShortcut(Keys value)
    {
        string? error = _shortcut.Change(Handle, value);
        UpdateView();
        return error;
    }

    internal async Task<string> ResetBinding()
    {
        if (!CanReset) return T("正在检测蓝牙设备。", "Checking Bluetooth devices.");
        _preparingReset = true;
        _error = null;
        try
        {
            // 暂停采集，但保持旧发送通道可用，直到排队的按键、全释放和恢复请求完成。
            long generation = _radio.Generation;
            _manualScreen = false;
            QueueRelease();
            SyncInput();
            if (_drain is { } drain)
            {
                await Task.WhenAny(drain.Task, Task.Delay(3000));
                if (!drain.Task.IsCompleted && _radio.Ready && generation == _radio.Generation)
                    return T("键盘交接尚未完成，绑定已保留。请稍后重试。",
                        "The keyboard handoff is still pending. Your binding was kept. Try again shortly.");
            }
            if (_exiting || IsDisposed) return T("清除已取消。", "Clearing the binding was cancelled.");
            _resetting = true;
            await _radio.ResetBindingAsync();
            return _radio.Status;
        }
        catch (Exception)
        {
            _error = () => T("重新检测失败，请检查电脑蓝牙状态。", "Could not detect a device. Check Bluetooth on your PC.");
            return _error();
        }
        finally
        {
            _preparingReset = false;
            _resetting = false;
            if (!IsDisposed && !_allowClose) ReadRadioState();
        }
    }

    internal async Task<bool> ToggleClearedKeyboardAsync()
    {
        if (!CanToggleClearedKeyboard) return false;
        _resetting = true;
        try
        {
            SyncInput();
            return await _radio.ToggleClearedKeyboardAsync();
        }
        finally
        {
            _resetting = false;
            if (!IsDisposed && !_allowClose) ReadRadioState();
        }
    }

    internal async Task<string?> SelectDeviceAsync(string id)
    {
        if (!CanReset) return T("正在检测设备，请稍候。", "Checking devices. Try again in a moment.");
        _resetting = true;
        _error = null;
        SyncInput();
        try
        {
            return await _radio.SelectDeviceAsync(id);
        }
        finally
        {
            _resetting = false;
            if (!IsDisposed && !_allowClose) ReadRadioState();
        }
    }

    internal void FlipScreenKeyboard()
    {
        if (!CanCalibrate) return;
        _manualScreen = true;
        _flipPending = true;
        WakeSender();
    }

    internal void CalibrateScreenKeyboard(bool shown)
    {
        if (!CanCalibrate) return;
        _manualScreen = true;
        _screenShown = shown;
        _screenFailed = false;
    }

    private void RadioChanged()
    {
        if (!IsHandleCreated || IsDisposed || _allowClose) return;
        try { BeginInvoke((Action)ReadRadioState); }
        catch (InvalidOperationException) { /* 窗口退出时忽略已过期的设备通知。 */ }
    }

    private void ReadRadioState()
    {
        if (IsDisposed || _allowClose) return;
        bool ready = _radio.Ready;
        long generation = _radio.Generation;
        bool changed = _ready != ready || _generation != generation;
        if (_canEject != _radio.CanEject) _screenFailed = false;
        _canEject = _radio.CanEject;
        if (changed)
        {
            bool wasReady = _ready;
            _ready = ready;
            _generation = generation;
            _epoch++;
            // 旧连接的报告和显隐推断都不能沿用。
            _reports.Clear();
            _capture.Clear();
            _screenShown = null;
            _lastQueuedRelease = _flipPending = _manualScreen = _screenFailed = false;
            if (ready) _error = null;
            if (!_exiting && ready != wasReady && ConnectionNotifications.Enabled)
                _tray.ShowBalloonTip(2200, UiLanguage.Brand, ready ? T("iPhone 键盘已连接。", "iPhone keyboard connected.") : T("iPhone 键盘连接已断开。", "iPhone keyboard disconnected."), ToolTipIcon.Info);
        }
        SyncInput();
    }

    private void SyncInput()
    {
        if (!_started || _allowClose) return;
        bool inMainWindow = _foreground && Visible && WindowState != FormWindowState.Minimized && !_editing && !_exiting;
        if (_languageActive != inMainWindow)
        {
            if (inMainWindow) _language.Enter(Handle);
            else _language.Restore();
            _languageActive = inMainWindow;
        }
        bool enable = inMainWindow && !_preparingReset && !_resetting && _ready;
        if (_capture.Enabled != enable)
        {
            _screenFailed = false;
            if (enable)
            {
                _capture.Clear();
                _capture.Enabled = true;
            }
            else
            {
                _capture.Enabled = false;
                _capture.Clear();
                QueueRelease();
            }
        }
        UpdateView();
        WakeSender();
    }

    private void OnReport(byte[] data, bool text)
    {
        if (!_capture.Enabled || !_ready || _generation != _radio.Generation) return;
        _lastQueuedRelease = false;
        _reports.Enqueue(((byte[])data.Clone(), text));
        WakeSender();
    }

    private void QueueRelease()
    {
        if (!_ready || _lastQueuedRelease) return;
        // 失焦前已接受的按键继续发送，全释放必须排在它们后面。
        _reports.Enqueue((new byte[8], false));
        _lastQueuedRelease = true;
    }

    // 键盘报告、全释放和 Eject 都走同一个发送循环。每次发送后重新读取焦点和连接代次。
    private Task WakeSender()
    {
        if (_drain is not null) return _drain.Task;
        if (!_ready || _resetting || _allowClose || !HasWork()) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _drain = completion;
        SendQueued(completion);
        return completion.Task;
    }

    // 接管时直接转发真实按键，由 iPhone 在输入时收起键盘，避免主动切换干扰编辑焦点。
    // 交回手机时等已接受的按键和全释放都发完，再请求恢复屏幕键盘。
    private bool ShouldRestoreScreenKeyboard() =>
        !_capture.Enabled && _screenShown == false && _reports.Count == 0 &&
        _radio.CanEject && !_screenFailed && !_manualScreen;

    private bool HasWork() => _flipPending || ShouldRestoreScreenKeyboard() || _reports.Count != 0;

    private async void SendQueued(TaskCompletionSource completion)
    {
        _settings?.RefreshState();
        try
        {
            while (!_allowClose && !_resetting && _ready && _radio.Ready && _radio.Generation == _generation && HasWork())
            {
                long epoch = _epoch;
                bool current() => epoch == _epoch && !_allowClose && !IsDisposed && _ready && _radio.Ready && _radio.Generation == _generation;

                if (_flipPending || ShouldRestoreScreenKeyboard())
                {
                    bool manual = _flipPending;
                    _flipPending = false;
                    bool success = await _radio.EjectAsync();
                    if (current())
                    {
                        // 手动翻转未知状态后仍是未知，直到用户校正或首次发出文字键。
                        if (success) _screenShown = !_screenShown;
                        else _screenFailed = true;
                        if (manual) _settings?.SetFeedback(() => success ? T("已发送切换，请核对 iPhone 上的实际状态。", "Toggle sent. Check the keyboard on your iPhone.") : T("未能发送切换，请检查蓝牙连接。", "Could not toggle the keyboard. Check Bluetooth."));
                    }
                }
                else if (_reports.TryPeek(out var report))
                {
                    bool success = await _radio.SendAsync(report.Data);
                    if (current())
                    {
                        if (!success) break;
                        _reports.Dequeue();
                        if (report.Text)
                        {
                            _screenShown = false;
                            _screenFailed = false;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            if (!_allowClose && !IsDisposed)
            {
                _error = () => T("蓝牙报告发送失败，请检查 iPhone 连接。", "Could not send keystrokes. Check your iPhone connection.");
                UpdateView();
            }
        }
        finally
        {
            _drain = null;
            if (!IsDisposed && !_allowClose) _settings?.RefreshState();
            completion.TrySetResult();
        }
    }

    private void ApplyLanguage()
    {
        Text = _brand.Text = UiLanguage.Brand;
        _preferences.Text = T("设置", "Settings");
        _footnote.Text = T("先在手机上点一下要输入的位置。", "Tap a text field on your iPhone first.");
        _menu.Items[0].Text = T("显示借键", "Show KeyIn");
        _menu.Items[1].Text = T("退出借键", "Quit KeyIn");
        UpdateView();
        ScheduleLayout();
    }

    private void UpdateView()
    {
        _heading.Text = _capture.Enabled ? T("笔记本正在输入", "Typing on iPhone") : _ready ? T("电脑输入已暂停", "Typing paused")
            : CanChooseDevice ? T("等待选择 iPhone", "Choose an iPhone") : T("等待键盘连接", "Waiting for keyboard");
        _explanation.Text = _capture.Enabled ? T("直接打字，收起窗口后回到手机输入。", "Type here. Hide this window to type on your iPhone.")
            : _ready ? T("显示并激活窗口，继续使用笔记本输入。", "Activate this window to type on your iPhone.")
            : CanChooseDevice ? T("选择要接收笔记本输入的手机。", "Choose the phone that will receive your keystrokes.")
            : T($"在 iPhone 蓝牙设置中连接 {Environment.MachineName}。", $"Connect to {Environment.MachineName} in iPhone Bluetooth settings.");
        _action.Enabled = (_ready || CanChooseDevice) && !_resetting && !_exiting;
        _action.Text = CanChooseDevice ? T("选择 iPhone", "Choose iPhone") : !_ready ? T("等待键盘连接", "Waiting for keyboard")
            : _capture.Enabled ? T("收起，手机输入", "Hide and type on iPhone") : T("开始电脑输入", "Start typing");
        UpdateConnectionAppearance();
        string status = _radio.Status;
        _detail.Text = _shortcut.LoadWarning ?? _error?.Invoke() ?? status;
        _tip.SetToolTip(_detail, _detail.Text);
        _hint.Text = _shortcut.Id != 0 ? ShortcutLabel : T("快捷键未注册", "No shortcut");
        string trayText = $"{UiLanguage.Brand} · {_heading.Text}";
        _tray.Text = trayText;
        _settings?.RefreshState();
    }

    public async void ExitGracefully()
    {
        if (_exiting || IsDisposed) return;
        if (InvokeRequired) { BeginInvoke((Action)ExitGracefully); return; }
        _exiting = true;
        _manualScreen = _flipPending = _screenFailed = false;
        _settings?.Close();
        QueueRelease();
        SyncInput();
        _language.Restore();
        // 蓝牙控制器可能不再应答；退出前给释放报告和软键盘请求留出有限时间。
        await Task.WhenAny(WakeSender(), Task.Delay(1200));
        if (IsDisposed) return;
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason != CloseReason.WindowsShutDown)
        {
            e.Cancel = true;
            if (e.CloseReason == CloseReason.UserClosing) MinimizeToTaskbar();
            else if (!_exiting) BeginInvoke((Action)ExitGracefully);
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        bool release = disposing && !IsDisposed;
        if (release)
        {
            _allowClose = true;
            _capture.Enabled = false;
            Application.RemoveMessageFilter(_capture);
            _language.Restore();
            if (IsHandleCreated) _shortcut.Release(Handle);
            UiLanguage.Changed -= ApplyLanguage;
            _radio.Changed -= RadioChanged;
            _tray.Visible = false;
            var trayIcon = _tray.Icon;
            _tray.Dispose();
            trayIcon?.Dispose();
            _menu.Dispose();
            _tip.Dispose();
            _taskbar.Dispose();
            _radio.Dispose();
        }
        base.Dispose(disposing);
        if (release) _appIcon.Dispose();
    }
}
