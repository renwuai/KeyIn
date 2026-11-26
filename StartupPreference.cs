using Microsoft.Win32;
using System.Security;

namespace KeyLend;

internal static class StartupPreference
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "KeyIn";

    internal static bool TryRead(out bool enabled)
    {
        enabled = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            enabled = key?.GetValue(AppName) is string { Length: > 0 };
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    internal static bool TrySet(bool enabled, out Func<string>? error)
    {
        error = null;
        try
        {
            if (enabled)
            {
                // Windows 的 Run 命令最多 260 个字符，路径两端的引号也计入长度。
                if (Environment.ProcessPath is not { Length: <= 258 } exe)
                {
                    error = () => UiLanguage.T("无法设置开机自启，请从较短的路径运行借键。",
                        "Could not enable startup. Run KeyIn from a shorter file path.");
                    return false;
                }
                using var key = Registry.CurrentUser.CreateSubKey(RunKey);
                key.SetValue(AppName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(AppName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            error = () => UiLanguage.T("无法保存开机自启设置。", "Could not save the startup setting.");
            return false;
        }
    }
}
