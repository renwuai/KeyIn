using System.Runtime.InteropServices;

namespace KeyLend;

internal sealed partial class DeskSettings
{
    private sealed record Section(Panel Card, Label Title, Label? Help, Control[][] Rows);
    private readonly List<Section> _sections = new();
    private readonly Panel _viewport = new() { AutoScroll = true };
    private readonly Panel _canvas = new();
    private readonly Panel _footer = new() { BackColor = Color.White };
    private readonly ToolTip _feedbackTip = new();
    private bool _fitting, _queued, _moving, _userSizing;
    private SizeF? _customClient;
    private static readonly Color Ink = Color.FromArgb(30, 43, 67);
    private static readonly Color Muted = Color.FromArgb(80, 96, 122);
    private static readonly Color Accent = Color.FromArgb(28, 76, 140);

    private void BuildLayout()
    {
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Ink;
        _viewport.TabIndex = 0;
        _footer.TabIndex = 1;
        Controls.AddRange([_viewport, _footer]);
        _viewport.Controls.Add(_canvas);
        _footer.Controls.AddRange([_message, Bind(_done, "完成", "Done")]);
        _message.ForeColor = Muted;
        _message.TextAlign = ContentAlignment.MiddleLeft;
        _done.TabIndex = 0;
        CancelButton = _done;
        AddSection("iPhone 连接", "iPhone connection", null,
            [[_bindingNote], [_deviceList], [Bind(_reset, "清除绑定并重新检测", "Clear binding and scan again")],
             [_recoveryHint], [Bind(_toggleCleared, "切换原 iPhone 的屏幕键盘", "Toggle the previous iPhone’s onscreen keyboard")],
             [Bind(_notifications, "连接或断开时通知我", "Notify me on connection or disconnection")]]);
        AddSection("语言 / Language", "Language / 语言", null, [[_language]]);
        AddSection("启动", "Startup", null,
            [[Bind(_startWithWindows, "开机自启", "Start with Windows")]]);
        AddSection("快捷键", "Global shortcut",
            ("按键录入，或填写组合后保存，例如 Ctrl + I。", "Record a shortcut or type one, such as Ctrl + I, then save."),
            [[_entry, Bind(_apply, "保存", "Save")], [_shortcut, Bind(_defaultKey, "恢复默认", "Restore default")]]);
        AddSection("iPhone 屏幕键盘", "iPhone onscreen keyboard",
            ("先在 iPhone 点开输入框。需要时切换一次，再按实际看到的状态校准。",
             "Tap a text field on your iPhone. Toggle the keyboard if needed, then select its current state."),
            [[Bind(_flip, "校正手机键盘显隐", "Toggle keyboard"), Bind(_shown, "已显示", "Visible"), Bind(_hidden, "已收起", "Hidden")]]);
        foreach (var button in new[] { _done, _reset, _toggleCleared, _apply, _shortcut, _defaultKey, _flip, _shown, _hidden })
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(181, 192, 207);
            button.BackColor = Color.White;
            button.ForeColor = Ink;
            button.UseVisualStyleBackColor = false;
        }
        _done.BackColor = Accent;
        _done.ForeColor = Color.White;
        _done.FlatAppearance.BorderSize = 0;
        // 初次进入设置时让完成按钮接收焦点，候选设备由用户主动点击或 Tab 选取。
        ActiveControl = _done;
        _shortcut.TextChanged += (_, _) => QueueLayout();
        _footer.Paint += (_, e) => e.Graphics.DrawLine(SystemPens.ControlLight, 0, 0, _footer.Width, 0);

        // 在创建窗口句柄前定位到宿主的显示器，这样首次测量就使用该屏幕的 DPI。
        StartPosition = FormStartPosition.Manual;
        var area = Screen.FromControl(_desk).WorkingArea;
        int width = Math.Min(area.Width, 548 * _desk.DeviceDpi / 96);
        int height = Math.Min(area.Height, 610 * _desk.DeviceDpi / 96);
        Bounds = new Rectangle(Math.Clamp(_desk.Left + (_desk.Width - width) / 2, area.Left, area.Right - width),
            Math.Clamp(_desk.Top + (_desk.Height - height) / 2, area.Top, area.Bottom - height), width, height);
    }

    private void AddSection(string titleZh, string titleEn, (string Zh, string En)? help, Control[][] rows)
    {
        var card = new Panel { BackColor = Color.White, TabIndex = _sections.Count };
        var title = Bind(new Label { Font = _sectionFont, ForeColor = Ink, UseMnemonic = false }, titleZh, titleEn);
        card.Controls.Add(title);
        Label? description = null;
        if (help is { } pair)
        {
            description = Bind(new Label { ForeColor = Muted, UseMnemonic = false }, pair.Zh, pair.En);
            card.Controls.Add(description);
        }
        int index = 0;
        foreach (var row in rows)
        {
            foreach (var control in row)
            {
                control.TabIndex = index++;
                card.Controls.Add(control);
                control.Enter += (_, _) => _viewport.ScrollControlIntoView(control);
            }
        }
        _canvas.Controls.Add(card);
        _sections.Add(new Section(card, title, description, rows));
    }

    private int Pixels(int logical) => (int)Math.Ceiling(logical * DeviceDpi / 96d);

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _loaded = true;
        FitDialog();
        var area = Screen.FromControl(this).WorkingArea;
        Location = new Point(Math.Clamp(_desk.Left + (_desk.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(_desk.Top + (_desk.Height - Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    // 由 Windows 缩放拖动中的外框，控件尺寸每次按目标显示器 DPI 从逻辑尺寸重算。
    protected override bool OnGetDpiScaledSize(int oldDpi, int newDpi, ref Size desiredSize) => false;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (_loaded) { FitDialog(); QueueLayout(); }
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        if (_loaded && !_fitting) QueueLayout();
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        _moving = true;
        _userSizing = false;
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        if (_userSizing) RememberSize();
        _moving = _userSizing = false;
        base.OnResizeEnd(e);
        QueueLayout();
    }

    protected override void WndProc(ref Message m)
    {
        const int wmSizing = 0x0214;
        if (m.Msg == wmSizing) _userSizing = true;
        base.WndProc(ref m);
    }

    private void RememberSize() => _customClient = new SizeF(ClientSize.Width * 96f / DeviceDpi, ClientSize.Height * 96f / DeviceDpi);

    private void QueueLayout()
    {
        if (!_loaded || _queued || !IsHandleCreated || IsDisposed || Disposing) return;
        _queued = true;
        BeginInvoke((Action)(() => { _queued = false; FitDialog(); }));
    }

    private void FitDialog()
    {
        if (!_loaded || _fitting || IsDisposed || Disposing) return;
        _fitting = true;
        try
        {
            if (_moving && _userSizing) RememberSize();
            using var dc = CreateGraphics();
            var area = Screen.FromControl(this).WorkingArea;
            var chrome = SizeFromClientSize(Size.Empty);
            int maxWidth = Math.Max(1, area.Width - chrome.Width);
            int maxHeight = Math.Max(1, area.Height - chrome.Height);
            int minWidth = Math.Min(Pixels(280), maxWidth);
            int minHeight = Math.Min(Pixels(220), maxHeight);
            MinimumSize = SizeFromClientSize(new Size(minWidth, minHeight));
            int width = Math.Clamp(Pixels((int)Math.Round(_customClient?.Width ?? 548)), minWidth, maxWidth);
            int footerPad = Pixels(17);
            int doneWidth = Math.Min(width / 2, Math.Max(Pixels(90), Measure(dc, _done).Width + Pixels(34)));
            int doneHeight = Math.Max(Pixels(38), Measure(dc, _done).Height + Pixels(12));
            int feedbackWidth = Math.Max(1, width - doneWidth - 3 * footerPad);
            int feedbackHeight = _message.Text.Length == 0 ? 0 : Measure(dc, _message, feedbackWidth).Height + Pixels(3);
            feedbackHeight = Math.Min(feedbackHeight, Math.Max(doneHeight, maxHeight / 4));
            int footerHeight = Math.Max(doneHeight, feedbackHeight) + Pixels(24);
            int contentHeight = LayoutSections(dc, width);
            int naturalHeight = contentHeight + footerHeight;
            int height = Math.Clamp(_customClient is { } custom ? Pixels((int)Math.Round(custom.Height)) : naturalHeight, minHeight, maxHeight);
            if (ClientSize != new Size(width, height)) ClientSize = new Size(width, height);
            int viewportHeight = Math.Max(1, height - footerHeight);
            _viewport.SetBounds(0, 0, width, viewportHeight);
            _footer.SetBounds(0, viewportHeight, width, footerHeight);
            _done.SetBounds(width - footerPad - doneWidth, (footerHeight - doneHeight) / 2, doneWidth, doneHeight);
            _message.SetBounds(footerPad, Pixels(6), feedbackWidth, footerHeight - Pixels(12));
            _feedbackTip.SetToolTip(_message, _message.Text);
            int canvasWidth = width;
            if (contentHeight > viewportHeight)
            {
                canvasWidth = Math.Max(1, width - GetSystemMetricsForDpi(2, (uint)DeviceDpi));
                contentHeight = LayoutSections(dc, canvasWidth);
            }
            _canvas.Size = new Size(canvasWidth, contentHeight);
            _viewport.AutoScrollMinSize = _canvas.Size;
            if (!_moving && Visible)
                Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                    Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        }
        finally { _fitting = false; }
    }

    private static Size Measure(Graphics dc, Control control, int width = 0)
    {
        var flags = TextFormatFlags.NoPrefix | (width > 0 ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine);
        return TextRenderer.MeasureText(dc, control.Text, control.Font,
            new Size(width > 0 ? width : int.MaxValue, int.MaxValue), flags);
    }

    private int LayoutSections(Graphics dc, int width)
    {
        int outer = Pixels(16), inner = Pixels(16), gap = Pixels(8);
        int cardWidth = Math.Max(1, width - 2 * outer);
        int lineWidth = Math.Max(1, cardWidth - 2 * inner);
        int y = outer;
        foreach (var section in _sections)
        {
            int top = inner;
            int titleHeight = Measure(dc, section.Title, lineWidth).Height + Pixels(2);
            section.Title.SetBounds(inner, top, lineWidth, titleHeight);
            top += titleHeight;
            if (section.Help is { } help)
            {
                top += Pixels(5);
                int helpHeight = Measure(dc, help, lineWidth).Height + Pixels(2);
                help.SetBounds(inner, top, lineWidth, helpHeight);
                top += helpHeight;
            }
            top += Pixels(12);
            foreach (var row in section.Rows)
            {
                int x = 0, rowHeight = 0;
                foreach (var control in row)
                {
                    if (control == _reset && !_desk.HasBinding || control == _deviceList && _deviceButtons.Count == 0 ||
                        control == _recoveryHint && _desk.HasBinding || control == _toggleCleared && !_showRecoveryButton) continue;
                    var text = Measure(dc, control);
                    int desiredWidth = control switch
                    {
                        _ when control == _bindingNote || control == _recoveryHint || control == _deviceList => lineWidth,
                        ComboBox => Pixels(185), TextBox => Pixels(191),
                        CheckBox => text.Width + Pixels(30),
                        _ => Math.Max(Pixels(70), text.Width + Pixels(26))
                    };
                    int w = Math.Min(desiredWidth, lineWidth);
                    if (x > 0 && x + w > lineWidth) { top += rowHeight + gap; x = rowHeight = 0; }
                    int h = control switch
                    {
                        Label => Measure(dc, control, w).Height + Pixels(3),
                        _ when control == _deviceList => ArrangeDeviceButtons(dc, w),
                        ComboBox combo => combo.PreferredHeight,
                        TextBox entry => entry.PreferredHeight,
                        _ => Math.Max(Pixels(35), Measure(dc, control, Math.Max(1, w - Pixels(control is CheckBox ? 30 : 12))).Height + Pixels(10))
                    };
                    control.SetBounds(inner + x, top, w, h);
                    x += w + gap;
                    rowHeight = Math.Max(rowHeight, h);
                }
                if (rowHeight > 0) top += rowHeight + gap;
            }
            int cardHeight = top - gap + inner;
            section.Card.SetBounds(outer, y, cardWidth, cardHeight);
            y += cardHeight + Pixels(13);
        }
        return y + outer - Pixels(13);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
