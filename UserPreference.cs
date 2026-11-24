namespace KeyLend;

internal static class UserPreference
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyLend");

    internal static string? Read(string name)
    {
        try { return File.ReadAllText(Path.Combine(Folder, name)).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool TryWrite(string name, string value)
    {
        try
        {
            // 单实例程序先完整写入临时文件，再替换上一次的设置。
            var staging = Directory.CreateDirectory(Path.Combine(Folder, "_temp"));
            string next = Path.Combine(staging.FullName, name + ".next");
            File.WriteAllText(next, value);
            File.Move(next, Path.Combine(Folder, name), true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }
}

internal static class ConnectionNotifications
{
    private const string FileName = "connection-notifications.txt";
    internal static bool Enabled { get; private set; } = UserPreference.Read(FileName) == "on";

    internal static bool TrySet(bool enabled, out string error)
    {
        error = "";
        if (enabled == Enabled) return true;
        if (!UserPreference.TryWrite(FileName, enabled ? "on" : "off"))
        {
            error = UiLanguage.T("无法保存通知设置，请检查配置目录的写入权限。",
                "Could not save notifications. Check write access to the settings folder.");
            return false;
        }
        Enabled = enabled;
        return true;
    }
}
