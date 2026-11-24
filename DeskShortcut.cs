using System.Runtime.InteropServices;
using System.Text.Json;

namespace KeyLend;

internal sealed class DeskShortcut
{
    internal const Keys Default = Keys.Control | Keys.I;
    private const Keys WindowsModifier = (Keys)0x80000;
    private const int FirstId = 0x4B01;
    private const int SecondId = 0x4B02;
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KeyLend");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "shortcut.json");
    private static readonly (string Zh, string En) InvalidShortcut = ("组合须包含 Ctrl、Alt 或 Win，可再加 Shift。", "Use Ctrl, Alt or Win with a key. Shift is optional.");
    private static readonly (string Zh, string En) ShortcutTaken = ("该组合已被占用或受系统保留，请换一组。", "This shortcut is in use or reserved by Windows. Choose another.");
    private static readonly (string Zh, string En) SaveFailed = ("无法保存快捷键，请检查本地配置目录的写入权限。", "Could not save the shortcut. Check write access to the settings folder.");
    private Func<string>? _loadWarning;

    internal Keys Value { get; private set; } = Default;
    internal int Id { get; private set; }
    internal string? LoadWarning => _loadWarning?.Invoke();

    internal string? Load(nint window)
    {
        _loadWarning = null;
        try
        {
            if (File.Exists(FilePath))
            {
                var saved = (Keys)JsonSerializer.Deserialize<int>(File.ReadAllText(FilePath));
                if (IsValid(saved))
                    Value = saved;
                else
                    _loadWarning = () => UiLanguage.T("快捷键配置无效，已使用默认组合。", "The saved shortcut is invalid. Using the default.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _loadWarning = () => UiLanguage.T("快捷键配置读取失败，已使用默认组合。", "Could not read the saved shortcut. Using the default.");
        }

        if (!Register(window, FirstId, Value))
        {
            _loadWarning = () => UiLanguage.T($"{Format(Value)} 当前无法注册。请在设置中更换快捷键。", $"{Format(Value)} is unavailable. Choose another shortcut in Settings.");
            return LoadWarning;
        }
        Id = FirstId;
        return LoadWarning;
    }

    internal string? Change(nint window, Keys value)
    {
        if (!IsValid(value))
            return UiLanguage.T(InvalidShortcut.Zh, InvalidShortcut.En);
        if (value == Value && Id != 0)
            return null;

        // 先占用新组合再写入配置，失败时原来的快捷键继续生效。
        int nextId = Id == FirstId ? SecondId : FirstId;
        if (!Register(window, nextId, value))
            return UiLanguage.T(ShortcutTaken.Zh, ShortcutTaken.En);
        try
        {
            string stagingDirectory = Path.Combine(DirectoryPath, "_temp");
            Directory.CreateDirectory(stagingDirectory);
            string stagingFile = Path.Combine(stagingDirectory, "shortcut.json");
            File.WriteAllText(stagingFile, JsonSerializer.Serialize((int)value));
            File.Move(stagingFile, FilePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            UnregisterHotKey(window, nextId);
            return UiLanguage.T(SaveFailed.Zh, SaveFailed.En);
        }

        Release(window);
        Id = nextId;
        Value = value;
        _loadWarning = null;
        return null;
    }

    // 已显示的注册错误也随界面语言更新，不能为了翻译再次注册快捷键。
    internal static string TranslateChangeError(string text)
    {
        foreach (var issue in new[] { InvalidShortcut, ShortcutTaken, SaveFailed })
            if (text == issue.Zh || text == issue.En) return UiLanguage.T(issue.Zh, issue.En);
        return text;
    }

    internal void Release(nint window)
    {
        if (Id != 0)
            UnregisterHotKey(window, Id);
        Id = 0;
    }

    internal static string Format(Keys value)
    {
        var parts = new List<string>();
        if (value.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (value.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (value.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (value.HasFlag(WindowsModifier)) parts.Add("Win");
        var key = value & Keys.KeyCode;
        int code = (int)key;
        parts.Add(code >= (int)Keys.D0 && code <= (int)Keys.D9 ? (code - (int)Keys.D0).ToString() : key.ToString());
        return string.Join(" + ", parts);
    }

    internal static bool TryParse(string text, out Keys value)
    {
        value = Keys.None;
        foreach (string part in text.Split('+', StringSplitOptions.TrimEntries))
        {
            Keys key = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => Keys.Control,
                "ALT" => Keys.Alt,
                "SHIFT" => Keys.Shift,
                "WIN" => WindowsModifier,
                _ => Keys.None
            };
            if (key != Keys.None) { value |= key; continue; }
            if ((value & Keys.KeyCode) != 0 || part.Length == 0) return false;
            if (part.Length == 1 && part[0] is >= '0' and <= '9') key = (Keys)part[0];
            else if (!Enum.TryParse(part, true, out key) || !Enum.IsDefined(key) || (key & Keys.Modifiers) != 0)
                return false;
            value |= key;
        }
        return IsValid(value);
    }

    private static bool IsValid(Keys value)
    {
        var key = value & Keys.KeyCode;
        var modifiers = value & Keys.Modifiers;
        const Keys required = Keys.Control | Keys.Alt | WindowsModifier;
        const Keys allowed = required | Keys.Shift;
        return (modifiers & required) != 0 && (modifiers & ~allowed) == 0 &&
            (int)key is >= 8 and <= 254 &&
            key is not (Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LControlKey or
                Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or
                Keys.RMenu or Keys.LWin or Keys.RWin or Keys.ProcessKey or Keys.Packet);
    }

    private static bool Register(nint window, int id, Keys value)
    {
        const uint noRepeat = 0x4000;
        uint modifiers = noRepeat;
        if (value.HasFlag(Keys.Alt)) modifiers |= 1;
        if (value.HasFlag(Keys.Control)) modifiers |= 2;
        if (value.HasFlag(Keys.Shift)) modifiers |= 4;
        if (value.HasFlag(WindowsModifier)) modifiers |= 8;
        return RegisterHotKey(window, id, modifiers, (uint)(value & Keys.KeyCode));
    }

    // WinForms 的 Keys.Modifiers 没有 Win 位，读取处理当前消息时的键盘状态。
    internal static Keys WithWindowsModifier(Keys value) =>
        GetKeyState((int)Keys.LWin) < 0 || GetKeyState((int)Keys.RWin) < 0 ? value | WindowsModifier : value;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
}
