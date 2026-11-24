using System.Runtime.InteropServices;

namespace KeyLend;

internal sealed class InputLanguageSession
{
    private nint rememberedLayout;
    private nint sessionWindow;
    private nint restoreLayout;
    private readonly Dictionary<nint, nint> detachedContexts = new();

    public void RememberForeground()
    {
        if (sessionWindow != 0)
            return;

        // 只读取原前台线程的布局。输入法上下文属于窗口，不能跨线程取得或修改。
        var window = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(window, out _);
        rememberedLayout = thread == 0 ? 0 : GetKeyboardLayout(thread);
    }

    public void Enter(nint appWindow)
    {
        if (sessionWindow != 0 || appWindow == 0 || GetForegroundWindow() != appWindow ||
            GetWindowThreadProcessId(appWindow, out _) != GetCurrentThreadId())
            return;

        sessionWindow = appWindow;
        var original = GetKeyboardLayout(0);
        // Windows 支持为每个窗口独立选输入法，优先保留本窗口的真实布局。
        restoreLayout = original != 0 ? original : rememberedLayout;
        rememberedLayout = 0;

        // 暂时摘开本窗口及子控件的输入上下文，保留原对象里的开关和转换状态。
        // 只有中文输入法时，按键同样可以直接输入字母，无需为系统安装英文布局。
        DetachContext(appWindow);
        EnumChildWindows(appWindow, (child, parameter) =>
        {
            if (GetWindowThreadProcessId(child, out _) == GetCurrentThreadId())
                DetachContext(child);
            return true;
        }, 0);

        if (IsEnglish(original))
            return;
        int count = GetKeyboardLayoutList(0, null);
        if (count <= 0)
            return;
        var installed = new nint[count];
        int copied = GetKeyboardLayoutList(count, installed);
        for (int i = 0; i < copied; i++)
        {
            if (IsEnglish(installed[i]) && ActivateKeyboardLayout(installed[i], 0) != 0)
                return;
        }
    }

    public void Restore()
    {
        if (sessionWindow == 0)
        {
            rememberedLayout = 0;
            return;
        }

        // ActivateKeyboardLayout 的目标是调用线程，切出到其他程序时也只还原本线程。
        if (restoreLayout != 0 && GetKeyboardLayout(0) != restoreLayout)
            ActivateKeyboardLayout(restoreLayout, 0);
        foreach (var (window, context) in detachedContexts)
        {
            if ((sessionWindow == window || IsChild(sessionWindow, window)) &&
                GetWindowThreadProcessId(window, out _) == GetCurrentThreadId())
                ImmAssociateContext(window, context);
        }
        detachedContexts.Clear();
        sessionWindow = 0;
        restoreLayout = 0;
        rememberedLayout = 0;
    }

    private void DetachContext(nint window)
    {
        var context = ImmAssociateContext(window, 0);
        if (context != 0)
            detachedContexts[window] = context;
    }

    // HKL 低字中的主语言编号 0x09 包括美国、英国等英文布局。
    private static bool IsEnglish(nint layout) => (layout.ToInt64() & 0x3ff) == 9;

    private delegate bool EnumWindowsProc(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] private static extern int GetKeyboardLayoutList(int size, [Out] nint[]? layouts);
    [DllImport("user32.dll")] private static extern nint ActivateKeyboardLayout(nint layout, uint flags);
    [DllImport("imm32.dll")] private static extern nint ImmAssociateContext(nint window, nint context);
    [DllImport("user32.dll")] private static extern bool IsChild(nint parent, nint window);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumWindowsProc callback, nint parameter);
}
