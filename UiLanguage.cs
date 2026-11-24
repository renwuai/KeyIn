namespace KeyLend;

internal static class UiLanguage
{
    private const string FileName = "language.txt";
    internal static bool English { get; private set; }
    internal static string Brand => T("借键", "KeyIn");
    internal static string T(string zh, string en) => English ? en : zh;
    internal static event Action? Changed;

    internal static void Load() => English = UserPreference.Read(FileName) == "en";

    internal static bool TrySet(bool english, out string error)
    {
        error = "";
        if (English == english) return true;
        if (!UserPreference.TryWrite(FileName, english ? "en" : "zh-CN"))
        {
            error = T("无法保存界面语言，请检查配置目录的写入权限。",
                      "Could not save the language. Check write access to the settings folder.");
            return false;
        }
        English = english;
        Changed?.Invoke();
        return true;
    }
}
