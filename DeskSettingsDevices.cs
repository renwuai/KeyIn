namespace KeyLend;

internal sealed partial class DeskSettings
{
    private readonly Label _bindingNote = new() { ForeColor = Muted, UseMnemonic = false };
    private readonly Label _recoveryHint = new() { ForeColor = Muted, UseMnemonic = false };
    private readonly Button _toggleCleared = new();
    private readonly Panel _deviceList = new() { AccessibleRole = AccessibleRole.Grouping };
    private readonly List<PhoneButton> _deviceButtons = new();
    private bool _choosing, _recovering, _showRecoveryButton;

    private void RefreshDevices()
    {
        bool saved = _desk.HasBinding;
        IReadOnlyList<PhoneChoice> choices = saved ? Array.Empty<PhoneChoice>() : _desk.Candidates;
        var ids = choices.Select(choice => choice.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed = false;
        for (int i = _deviceButtons.Count - 1; i >= 0; i--)
        {
            var button = _deviceButtons[i];
            if (ids.Contains(button.Id)) continue;
            if (button.ContainsFocus) _done.Select();
            _deviceButtons.RemoveAt(i);
            button.Dispose();
            changed = true;
        }

        // 每个按钮始终对应同一设备 ID。保留已有按钮的顺序，新发现的设备追加到末尾。
        foreach (var choice in choices)
        {
            var button = _deviceButtons.Find(b => StringComparer.OrdinalIgnoreCase.Equals(b.Id, choice.Id));
            if (button is null)
            {
                button = new PhoneButton(choice.Id, _sectionFont);
                button.Click += ChoosePhone;
                button.Enter += (_, _) => _viewport.ScrollControlIntoView(button);
                _deviceList.Controls.Add(button);
                _deviceButtons.Add(button);
                changed = true;
            }
            button.TabIndex = _deviceButtons.IndexOf(button);
            button.Enabled = !_choosing && !_recovering && _desk.CanReset;
            if (button.Present(choice.Name, choice.Detail))
                _feedbackTip.SetToolTip(button, button.AccessibleName);
        }

        _deviceList.Visible = !saved && _deviceButtons.Count > 0;
        _deviceList.AccessibleName = UiLanguage.T("可选 iPhone", "Available iPhones");
        _reset.Visible = saved;
        string note = saved
            ? _desk.BindingStatus + Environment.NewLine + UiLanguage.T("清除绑定前，借键会先尝试把输入交还这台 iPhone。清除后会立即重新检测，等你选择设备。",
                "Before clearing the binding, KeyIn will try to return input to this iPhone. It will then scan again and wait for you to choose a device.")
            : choices.Count == 0
                ? UiLanguage.T("暂未发现可选的 iPhone。在 iPhone 的「设置 → 蓝牙」中连接这台电脑，设备就绪后会出现在这里。",
                    "No iPhones are available yet. On your iPhone, open Settings → Bluetooth and connect to this PC. The device will appear here when ready.")
                : UiLanguage.T("请选择接收键盘输入的 iPhone。设备同名时，可核对蓝牙地址末段。",
                    "Choose the iPhone that should receive keyboard input. For devices with the same name, compare the address endings.");
        if (_bindingNote.Text != note) { _bindingNote.Text = note; changed = true; }

        bool canToggle = !saved && _desk.CanToggleClearedKeyboard;
        // 发送期间原设备的一次性机会会被消费，保留禁用的按钮直到此次调用结束。
        bool showButton = !saved && (canToggle || _recovering);
        if (_showRecoveryButton != showButton)
        {
            _showRecoveryButton = showButton;
            if (!showButton && _toggleCleared.ContainsFocus) _done.Select();
            changed = true;
        }
        _toggleCleared.Visible = showButton;
        _toggleCleared.Enabled = canToggle && !_recovering && !_choosing && _desk.CanReset;
        _recoveryHint.Visible = !saved;
        string hint = showButton
            ? UiLanguage.T("先在原 iPhone 上点一下输入框。如果屏幕键盘仍然收起，只点一次下方按钮。",
                "Tap a text field on the previous iPhone. Only if its onscreen keyboard is still hidden, click the button below once.")
            : UiLanguage.T("如果原 iPhone 的屏幕键盘仍收起，可在手机的「设置」中暂时关闭蓝牙，再点输入框。",
                "If the previous iPhone’s keyboard is still hidden, briefly turn off Bluetooth in its Settings, then tap a text field.");
        if (_recoveryHint.Text != hint)
        {
            _recoveryHint.Text = _toggleCleared.AccessibleDescription = hint;
            changed = true;
        }
        if (changed) QueueLayout();
    }

    private async void ToggleClearedKeyboard(object? sender, EventArgs e)
    {
        if (_recovering || _choosing || _desk.HasBinding || !_desk.CanReset || !_desk.CanToggleClearedKeyboard) return;
        CancelRecording();
        _recovering = true;
        Func<string> pending = () => UiLanguage.T("正在向原 iPhone 发送键盘切换…", "Sending a keyboard toggle to the previous iPhone…");
        SetFeedback(pending);
        RefreshState();
        bool sent;
        try { sent = await _desk.ToggleClearedKeyboardAsync(); }
        catch (Exception) { sent = false; }
        _recovering = false;
        if (IsDisposed || Disposing || !Visible) return;
        RefreshState();
        // 避免旧请求覆盖用户随后执行的操作，或把原设备的结果显示在新绑定上。
        if (!ReferenceEquals(_feedback, pending)) return;
        if (_desk.HasBinding) { SetFeedback(null); return; }
        SetFeedback(sent
            ? () => UiLanguage.T("已发送一次切换，请在原 iPhone 上确认屏幕键盘。", "Toggle sent once. Check the onscreen keyboard on the previous iPhone.")
            : () => UiLanguage.T("未能确认切换。如果键盘仍收起，请在原 iPhone 的「设置」中暂时关闭蓝牙，再点输入框。",
                "Could not confirm the toggle. If the keyboard is still hidden, briefly turn off Bluetooth in the previous iPhone’s Settings, then tap a text field."));
    }

    private async void ChoosePhone(object? sender, EventArgs e)
    {
        if (sender is not PhoneButton button || _choosing || _recovering || _desk.HasBinding || !_desk.CanReset) return;
        string id = button.Id;
        if (!_desk.Candidates.Any(choice => StringComparer.OrdinalIgnoreCase.Equals(choice.Id, id)))
        {
            RefreshDevices();
            SetFeedback(() => UiLanguage.T("这台 iPhone 已不可用，请重新选择。", "This iPhone is no longer available. Choose another device."));
            return;
        }
        CancelRecording();
        _choosing = true;
        RefreshState();
        try
        {
            string? error = await _desk.SelectDeviceAsync(id);
            if (IsDisposed || Disposing) return;
            if (error is null)
                SetFeedback(() => UiLanguage.T("已保存所选的 iPhone。", "Your selected iPhone has been saved."));
            else SetFeedback(() => error);
        }
        catch (Exception)
        {
            if (!IsDisposed && !Disposing)
                SetFeedback(() => UiLanguage.T("选择设备失败，请检查蓝牙连接后再试。", "Could not select this device. Check the Bluetooth connection and try again."));
        }
        finally
        {
            _choosing = false;
            if (!IsDisposed && !Disposing) RefreshState();
        }
    }

    private int ArrangeDeviceButtons(Graphics dc, int width)
    {
        int gap = Pixels(7), top = 0;
        int height = Math.Max(Pixels(64), TextRenderer.MeasureText(dc, "Ag", _sectionFont).Height +
            TextRenderer.MeasureText(dc, "Ag", Font).Height + Pixels(17));
        foreach (var button in _deviceButtons)
        {
            button.SetBounds(0, top, width, height);
            top += height + gap;
        }
        return Math.Max(0, top - gap);
    }

    // 保留标准 Button 的按压、Tab、空格和辅助技术行为，内容按两行显示名称与地址。
    private sealed class PhoneButton : Button
    {
        internal string Id { get; }
        private readonly Font _titleFont;
        private string _name = "", _detail = "", _action = "";

        internal PhoneButton(string id, Font titleFont)
        {
            Id = id;
            _titleFont = titleFont;
            FlatStyle = FlatStyle.Flat;
            bool highContrast = SystemInformation.HighContrast;
            FlatAppearance.BorderColor = highContrast ? SystemColors.WindowText : Color.FromArgb(151, 180, 217);
            FlatAppearance.MouseOverBackColor = highContrast ? SystemColors.Control : Color.FromArgb(225, 237, 252);
            BackColor = highContrast ? SystemColors.Window : Color.FromArgb(243, 248, 255);
            UseVisualStyleBackColor = false;
            UseMnemonic = false;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }

        internal bool Present(string name, string detail)
        {
            string action = UiLanguage.T("选择", "Choose");
            if (_name == name && _detail == detail && _action == action) return false;
            _name = name;
            _detail = detail;
            _action = action;
            AccessibleName = $"{action} {name} · {detail}";
            AccessibleDescription = UiLanguage.T("选择并绑定这台 iPhone。", "Choose and bind this iPhone.");
            Invalidate();
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int S(int n) => (int)Math.Ceiling(n * DeviceDpi / 96d);
            bool highContrast = SystemInformation.HighContrast;
            Color main = !Enabled ? SystemColors.GrayText : highContrast ? SystemColors.WindowText : Ink;
            Color secondary = !Enabled ? SystemColors.GrayText : highContrast ? SystemColors.WindowText : Muted;
            Color accent = !Enabled ? SystemColors.GrayText : highContrast ? SystemColors.WindowText : Accent;
            const TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
            int pad = S(13);
            int actionWidth = TextRenderer.MeasureText(e.Graphics, _action, Font).Width + S(4);
            int titleHeight = TextRenderer.MeasureText(e.Graphics, "Ag", _titleFont).Height;
            int detailHeight = TextRenderer.MeasureText(e.Graphics, "Ag", Font).Height;
            int top = (Height - titleHeight - detailHeight - S(2)) / 2;
            int textWidth = Math.Max(1, Width - 3 * pad - actionWidth);
            TextRenderer.DrawText(e.Graphics, _name, _titleFont, new Rectangle(pad, top, textWidth, titleHeight), main, flags);
            TextRenderer.DrawText(e.Graphics, _detail, Font, new Rectangle(pad, top + titleHeight + S(2), Math.Max(1, Width - 2 * pad), detailHeight), secondary, flags);
            TextRenderer.DrawText(e.Graphics, _action, Font, new Rectangle(Width - pad - actionWidth, top, actionWidth, titleHeight),
                accent, flags | TextFormatFlags.Right);
        }
    }
}
