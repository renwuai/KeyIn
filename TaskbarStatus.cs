using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KeyLend;

internal enum InputStatus { Offline, Paused, Typing }

internal sealed class TaskbarStatus : IDisposable
{
    private static readonly int CreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
    internal static int ButtonCreatedMessage => CreatedMessage != 0 ? CreatedMessage : -1;

    private nint _window;
    private bool _ready, _disposed;
    private ITaskbarList3? _shell;
    private (InputStatus Status, string Description, int Size)? _published;

    // 由窗口线程调用。只有 TaskbarButtonCreated 消息可以用 force 开启当前 HWND 的提交。
    internal void Update(nint window, InputStatus status, string description, bool force = false)
    {
        if (_disposed) return;
        if (window != _window || force)
        {
            ReleaseShell();
            _window = window;
            _ready = force && window != 0;
        }
        if (!_ready) return;

        uint dpi = GetDpiForWindow(window);
        if (dpi == 0) return;
        int size = Math.Max(1, (int)Math.Round(16 * dpi / 96d));
        var requested = (status, description, size);
        if (_published == requested) return;

        try
        {
            if (_shell is null)
            {
                var classId = new Guid("56FDF344-FD6D-11D0-958A-006097C9A090");
                var interfaceId = typeof(ITaskbarList3).GUID;
                const uint inProcessServer = 1;
                if (CoCreateInstance(in classId, 0, inProcessServer, in interfaceId, out _shell) < 0 ||
                    _shell is null || _shell.HrInit() < 0)
                {
                    ReleaseShell();
                    return;
                }
            }
            nint icon = RenderIcon(size, status);
            try
            {
                if (_shell.SetOverlayIcon(window, icon, description) >= 0)
                    _published = requested;
                else
                    ReleaseShell();
            }
            finally { DestroyIcon(icon); } // Explorer 已复制图标，调用方仍拥有原始 HICON。
        }
        catch (ExternalException)
        {
            // Shell 重启或图标创建失败时保持输入功能，后续更新可重试。
            ReleaseShell();
        }
    }

    internal static int TrayIconSize => Math.Max(16, (int)Math.Round(16 * GetDpiForSystem() / 96d));

    internal static Icon CreateTrayIcon(Icon brand, int size, InputStatus status)
    {
        nint handle = RenderIcon(size, status, brand);
        try
        {
            // FromHandle 借用原句柄，复制后交给 NotifyIcon 持有到下次更换。
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private static nint RenderIcon(int size, InputStatus status, Icon? brand = null)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.PageUnit = GraphicsUnit.Pixel;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int badgeSize = size;
            if (brand is not null)
            {
                using var frame = new Icon(brand, size, size);
                using var image = frame.ToBitmap();
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                // 按标准托盘尺寸绘制完整底图，再在右上叠加状态。
                g.DrawImage(image, new Rectangle(0, 0, size, size),
                    0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
                // 16px 托盘图标使用 8px 角标，键帽仍是主体。
                badgeSize = (int)Math.Round(size * 0.5);
                g.TranslateTransform(size - badgeSize, 0);
            }
            g.ScaleTransform(badgeSize / 16f, badgeSize / 16f);
            Color background = status switch
            {
                InputStatus.Typing => Color.FromArgb(24, 100, 187),
                InputStatus.Paused => Color.FromArgb(164, 94, 3),
                _ => Color.FromArgb(91, 102, 117)
            };
            using var fill = new SolidBrush(background);
            // 圆角方块充分使用系统分配的角标区域，避免细笔画缩小后粘连。
            g.FillRoundedRectangle(fill, new RectangleF(0, 0, 16, 16), new SizeF(4, 4));
            using var stroke = new Pen(Color.White, 2.6f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            switch (status)
            {
                case InputStatus.Typing:
                    g.DrawLines(stroke, new PointF[] { new(3.1f, 8.1f), new(6.6f, 11.4f), new(12.9f, 4.6f) });
                    break;
                case InputStatus.Paused:
                    g.FillRectangle(Brushes.White, 4.3f, 3.4f, 2.8f, 9.2f);
                    g.FillRectangle(Brushes.White, 8.9f, 3.4f, 2.8f, 9.2f);
                    break;
                default:
                    g.DrawLine(stroke, 3.4f, 8f, 12.6f, 8f);
                    break;
            }
        }
        return bitmap.GetHicon();
    }

    private void ReleaseShell()
    {
        _published = null;
        if (_shell is null) return;
        Marshal.ReleaseComObject(_shell);
        _shell = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_published.HasValue && _shell is not null)
                _shell.SetOverlayIcon(_window, 0, null);
        }
        catch (ExternalException) { /* Explorer 可能已经退出。 */ }
        finally { ReleaseShell(); }
    }

    // 保留 Windows SDK 中 ITaskbarList、ITaskbarList2 和 ITaskbarList3 的原始虚表顺序。
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        [PreserveSig] int AddTab(nint hwnd);
        [PreserveSig] int DeleteTab(nint hwnd);
        [PreserveSig] int ActivateTab(nint hwnd);
        [PreserveSig] int SetActiveAlt(nint hwnd);
        [PreserveSig] int MarkFullscreenWindow(nint hwnd, int fullscreen);
        [PreserveSig] int SetProgressValue(nint hwnd, ulong completed, ulong total);
        [PreserveSig] int SetProgressState(nint hwnd, uint flags);
        [PreserveSig] int RegisterTab(nint tab, nint owner);
        [PreserveSig] int UnregisterTab(nint tab);
        [PreserveSig] int SetTabOrder(nint tab, nint insertBefore);
        [PreserveSig] int SetTabActive(nint tab, nint owner, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
        [PreserveSig] int ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
        [PreserveSig] int ThumbBarSetImageList(nint hwnd, nint imageList);
        [PreserveSig] int SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out ITaskbarList3? instance);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RegisterWindowMessage(string name);

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
