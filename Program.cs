using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Shell;

namespace KeyLend;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        bool exit = args.Contains("--exit", StringComparer.OrdinalIgnoreCase);
        // 同一登录会话内共享一个键盘服务；不同用户的命令管道彼此隔离。
        using var identity = WindowsIdentity.GetCurrent();
        string channel = $"KeyLend.{identity.User?.Value}.{System.Diagnostics.Process.GetCurrentProcess().SessionId}";
        using var single = new Mutex(true, @"Local\" + channel, out bool first);
        if (!first)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", channel, PipeDirection.Out,
                    PipeOptions.CurrentUserOnly);
                client.Connect(2000);
                client.WriteByte(exit ? (byte)2 : (byte)1);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException) { }
            return;
        }
        if (exit) return;
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        UiLanguage.Load();
        using var form = new DeskForm();
        using var stopping = new CancellationTokenSource();
        form.Shown += (_, _) =>
        {
            RegisterExitTask();
            _ = ListenAsync(channel, form, stopping.Token);
        };
        form.FormClosed += (_, _) => stopping.Cancel();
        UiLanguage.Changed += RegisterExitTask;
        try { Application.Run(form); }
        finally { UiLanguage.Changed -= RegisterExitTask; }
    }

    private static async Task ListenAsync(string channel, DeskForm form, CancellationToken token)
    {
        // 只接收显示/退出两个固定命令；在窗口线程处理，让退出仍走键盘恢复流程。
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(channel, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token);
                var command = new byte[1];
                if (await server.ReadAsync(command.AsMemory(), token) == 0 || form.IsDisposed) continue;
                if (command[0] == 1) form.Reveal();
                if (command[0] == 2) form.ExitGracefully();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (IOException) { if (!token.IsCancellationRequested) await Task.Delay(100); }
        }
    }

    private static void RegisterExitTask()
    {
        if (Environment.ProcessPath is not { } exe) return;
        LocalizeStartMenuShortcut(exe);
        var exit = new JumpTask { ApplicationPath = exe, Title = UiLanguage.T("退出借键", "Quit KeyIn"), Arguments = "--exit",
            IconResourcePath = exe, IconResourceIndex = 0 };
        // JumpList 使用当前进程的 Windows 任务栏身份，直接关联新程序图标。
        new JumpList(new[] { exit }, false, false).Apply();
    }

    private static void LocalizeStartMenuShortcut(string exe)
    {
        string folder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        string target = Path.Combine(folder, UiLanguage.Brand + ".lnk");
        if (File.Exists(target)) return;
        // 仅改名已有、指向当前程序的开始菜单入口，保留用户设置的快捷方式属性。
        foreach (string name in new[] { "借键 KeyIn", UiLanguage.T("KeyIn", "借键") })
        {
            string previous = Path.Combine(folder, name + ".lnk");
            if (!File.Exists(previous)) continue;
            object? shell = null, link = null;
            try
            {
                if (Type.GetTypeFromProgID("WScript.Shell") is not { } type) return;
                shell = Activator.CreateInstance(type);
                if (shell is null) return;
                link = ((dynamic)shell).CreateShortcut(previous);
                string appPath = ((dynamic)link).TargetPath;
                if (!string.Equals(appPath, exe, StringComparison.OrdinalIgnoreCase)) continue;
                File.Move(previous, target);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
            {
                // 系统策略或文件占用可能阻止重命名，应用内的语言设置仍然有效。
                return;
            }
            finally
            {
                if (link is not null) Marshal.ReleaseComObject(link);
                if (shell is not null) Marshal.ReleaseComObject(shell);
            }
        }
    }
}
