using System.Drawing.Drawing2D;

namespace KeyLend;

public sealed partial class DeskForm
{
    private readonly Panel _card = new();
    private readonly Panel _canvas = new();
    private readonly Panel _viewport = new() { Dock = DockStyle.Fill };
    private readonly PictureBox _brandIcon = new() { SizeMode = PictureBoxSizeMode.Zoom };
    private readonly Label _brand = new() { Text = "借键" };
    private readonly Label _explanation = new();
    private readonly Label _footnote = new() { Text = "先在手机上点一下要输入的位置。" };
    private readonly Button _action = new();
    private bool _layoutQueued, _fitting, _moving;
    private Size _wantedClient;


    private void BuildDeskLayout()
    {
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(30, 43, 67);
        var secondary = Color.FromArgb(97, 113, 140);
        _heading.Font = new Font(Font.FontFamily, 15, FontStyle.Bold);
        _brand.Font = new Font(Font.FontFamily, 16, FontStyle.Bold);
        _action.Font = new Font(Font, FontStyle.Bold);
        using (var large = new Icon(_appIcon, 128, 128))
            _brandIcon.Image = large.ToBitmap();
        _brandIcon.Disposed += (_, _) => _brandIcon.Image?.Dispose();
        foreach (var label in new[] { _heading, _hint, _detail, _brand, _explanation, _footnote })
        {
            label.AutoSize = false;
            label.AutoEllipsis = false;
            label.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            label.Dock = DockStyle.None;
            label.TextAlign = ContentAlignment.MiddleLeft;
            label.UseMnemonic = false;
            label.TextChanged += (_, _) => ScheduleLayout();
        }
        _explanation.ForeColor = _detail.ForeColor = _footnote.ForeColor = secondary;
        _hint.TextAlign = ContentAlignment.MiddleCenter;
        _hint.BackColor = Color.White;
        _action.ForeColor = Color.White;
        _action.Click += (_, _) =>
        {
            if (CanChooseDevice) OpenPreferences();
            else if (_capture.Enabled) MinimizeToTaskbar();
            else Reveal();
        };
        _preferences.BackColor = Color.White;
        foreach (var button in new[] { _action, _preferences })
        {
            button.AutoSize = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.Cursor = Cursors.Hand;
            RoundCorners(button, 8);
        }
        _card.Controls.AddRange([_heading, _explanation]);
        _canvas.Controls.AddRange([_brandIcon, _brand, _hint, _card, _action, _preferences, _detail, _footnote]);
        _viewport.Controls.Add(_canvas);
        Controls.Add(_viewport);
        RoundCorners(_hint, 7);
        RoundCorners(_card, 13);
        Load += (_, _) => FitContent();
        ResizeBegin += (_, _) => _moving = true;
        ResizeEnd += (_, _) => { _moving = false; ScheduleLayout(); };
        ClientSizeChanged += (_, _) =>
        {
            // 系统拖动循环可能在 DPI 消息之后再次写入窗口大小。
            if (!_fitting && !_wantedClient.IsEmpty && ClientSize != _wantedClient &&
                WindowState == FormWindowState.Normal) ScheduleLayout();
        };
    }

    private InputStatus CurrentInputStatus => !_ready ? InputStatus.Offline
        : _capture.Enabled ? InputStatus.Typing : InputStatus.Paused;

    private void UpdateConnectionAppearance()
    {
        // 按键转发、窗口与托盘共用同一状态，设置窗口打开期间也算暂停。
        var status = CurrentInputStatus;
        var (background, card, title, note, button) = status switch
        {
            InputStatus.Typing => (0xF5F8FF, 0xDEEBFF, 0x184594, 0x35537E, 0x2563DB),
            InputStatus.Paused => (0xFFFBF5, 0xFFF0CF, 0x744400, 0x76562C, 0x955805),
            _ => (0xF6F7F8, 0xE7E9EC, 0x424850, 0x4F555C, _action.Enabled ? 0x58606C : 0xD5D8DC)
        };
        static Color Opaque(int rgb) => Color.FromArgb(rgb | unchecked((int)0xFF000000));
        BackColor = Opaque(background);
        _card.BackColor = Opaque(card);
        _heading.ForeColor = Opaque(title);
        _explanation.ForeColor = Opaque(note);
        _hint.ForeColor = _heading.ForeColor;
        _action.BackColor = Opaque(button);
        UpdateStatusIcons(status);
    }

    private void UpdateStatusIcons(InputStatus status, bool taskbarCreated = false)
    {
        if (_allowClose || _tray is null) return;
        int size = TaskbarStatus.TrayIconSize;
        var previous = _tray.Icon;
        if (_trayStatus != status || previous?.Width != size)
        {
            var replacement = TaskbarStatus.CreateTrayIcon(_appIcon, size, status);
            try { _tray.Icon = replacement; }
            catch { replacement.Dispose(); throw; }
            _trayStatus = status;
            previous?.Dispose();
        }
        if (IsHandleCreated) _taskbar.Update(Handle, status, _heading.Text, force: taskbarCreated);
    }

    // 此窗体手工按 DPI 排版。让 Windows 为拖动中的外框使用默认线性缩放，
    // 避免 WinForms 以 AutoScaleMode.None 的字体缩放系数返回旧的外框尺寸。
    protected override bool OnGetDpiScaledSize(int oldDpi, int newDpi, ref Size desiredSize) => false;

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateStatusIcons(CurrentInputStatus);
        if (!_started || WindowState != FormWindowState.Normal) return;
        FitContent();
        ScheduleLayout();
    }

    private void ScheduleLayout()
    {
        if (_layoutQueued || !IsHandleCreated || IsDisposed || Disposing) return;
        _layoutQueued = true;
        BeginInvoke((Action)(() =>
        {
            _layoutQueued = false;
            if (!IsDisposed && !Disposing) FitContent();
        }));
    }

    private void FitContent()
    {
        if (_fitting || WindowState != FormWindowState.Normal || IsDisposed || Disposing) return;
        _fitting = true;
        _viewport.SuspendLayout();
        try { ArrangeContent(); }
        finally
        {
            _viewport.ResumeLayout(true);
            _fitting = false;
        }
    }

    private void ArrangeContent()
    {
        // 手工布局统一使用当前显示器的设备像素。文字用同一窗口的绘图上下文测量。
        int S(int logical) => (int)Math.Ceiling(logical * DeviceDpi / 96d);
        using var dc = CreateGraphics();
        Size Measure(Control control, int width = 0)
        {
            var flags = TextFormatFlags.NoPrefix | (width > 0 ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine);
            return TextRenderer.MeasureText(dc, control.Text.Length == 0 ? "字" : control.Text, control.Font,
                new Size(width > 0 ? width : int.MaxValue, int.MaxValue), flags);
        }
        var area = Screen.FromControl(this).WorkingArea;
        var chrome = SizeFromClientSize(Size.Empty);
        int width = Math.Min(S(440), Math.Max(S(210), area.Width - chrome.Width));
        int pad = S(17), gap = S(8), inside = width - 2 * pad;
        int y = S(12), badge = Measure(_hint).Width + S(18);
        // 长快捷键在窄屏放不下时移到下一行。
        bool stackBadge = Measure(_brand).Width + S(42) + gap + badge > inside;
        int badgeHeight = Measure(_hint).Height + S(8);
        int header = Math.Max(S(36), Math.Max(Measure(_brand).Height, badgeHeight));
        _brandIcon.SetBounds(pad, y + (header - S(34)) / 2, S(34), S(34));
        _brand.SetBounds(pad + S(42), y, Math.Max(1, inside - S(42) - (stackBadge ? 0 : badge)), header);
        if (stackBadge)
        {
            _hint.SetBounds(pad, y + header + S(3), Math.Min(inside, badge), badgeHeight);
            header += S(3) + badgeHeight;
        }
        else _hint.SetBounds(width - pad - badge, y, badge, header);
        y += header + gap;

        int inset = S(15), textWidth = inside - 2 * inset;
        int titleHeight = Measure(_heading, textWidth).Height + S(2);
        int noteHeight = Measure(_explanation, textWidth).Height + S(2);
        int cardHeight = S(22) + titleHeight + S(5) + noteHeight;
        _card.SetBounds(pad, y, inside, cardHeight);
        _heading.SetBounds(inset, S(10), textWidth, titleHeight);
        _explanation.SetBounds(inset, S(15) + titleHeight, textWidth, noteHeight);
        y += cardHeight + S(10);

        int settingsWidth = Math.Max(S(62), Measure(_preferences).Width + S(18));
        int buttonsHeight = Math.Max(S(37), Math.Max(Measure(_action).Height, Measure(_preferences).Height) + S(12));
        _action.SetBounds(pad, y, inside - gap - settingsWidth, buttonsHeight);
        _preferences.SetBounds(width - pad - settingsWidth, y, settingsWidth, buttonsHeight);
        y += buttonsHeight + S(8);
        int detailHeight = Measure(_detail, inside).Height + S(3);
        _detail.SetBounds(pad, y, inside, detailHeight);
        y += detailHeight + S(4);
        int footHeight = Measure(_footnote, inside).Height + S(2);
        _footnote.SetBounds(pad, y, inside, footHeight);
        int height = y + footHeight + S(12);
        int visibleHeight = Math.Min(height, Math.Max(S(110), area.Height - chrome.Height));
        bool needsScroll = visibleHeight < height;
        _wantedClient = new Size(Math.Min(area.Width - chrome.Width,
            width + (needsScroll ? SystemInformation.VerticalScrollBarWidth : 0)), visibleHeight);
        // 先定外框，再定画布。常规大小禁用滚动，避免继承上一块屏幕的滚动偏移。
        if (ClientSize != _wantedClient) ClientSize = _wantedClient;
        _viewport.AutoScroll = needsScroll;
        if (!needsScroll) _canvas.Location = Point.Empty;
        _canvas.Size = new Size(width, height);
        if (!_moving && Visible && WindowState == FormWindowState.Normal)
            Location = new Point(Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width)),
                                 Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height)));
    }

    private static void RoundCorners(Control control, int radius)
    {
        void Shape()
        {
            int d = Math.Min(Math.Min(control.Width, control.Height), radius * 2 * control.DeviceDpi / 96);
            if (d < 2) return;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(control.Width - d, 0, d, d, 270, 90);
            path.AddArc(control.Width - d, control.Height - d, d, d, 0, 90);
            path.AddArc(0, control.Height - d, d, d, 90, 90);
            path.CloseFigure();
            control.Region = new Region(path);
        }
        control.SizeChanged += (_, _) => Shape();
        control.DpiChangedAfterParent += (_, _) => Shape();
        Shape();
    }
}
