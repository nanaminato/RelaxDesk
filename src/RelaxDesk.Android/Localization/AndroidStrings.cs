using System.Text.Json;
using Android.Content;
using Java.Util;

namespace RelaxDesk.Mobile;

/// <summary>
/// Loads localization strings from independent JSON files in Assets/Locales/{code}.json,
/// aligned with RelaxDesk desktop locales (zh-CN, en, ja, fr, es).
/// </summary>
internal static class AndroidStrings
{
    const string PrefsName = "relaxdesk_settings_v1";
    const string KeyLocale = "locale";

    public static readonly (string Code, string DisplayName)[] SupportedLanguages =
    [
        ("zh-CN", "简体中文"),
        ("en", "English"),
        ("ja", "日本語"),
        ("fr", "Français"),
        ("es", "Español")
    ];

    static readonly Dictionary<string, Dictionary<string, string>> Cache = new(StringComparer.OrdinalIgnoreCase);
    static string current = "zh-CN";
    static Dictionary<string, string> activeMap = [];
    static Dictionary<string, string> fallbackMap = [];

    public static string CurrentLanguage => current;

    public static void Init(Context context)
    {
        if (fallbackMap.Count == 0)
            fallbackMap = LoadLocaleFile(context, "en");

        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        string? saved = prefs?.GetString(KeyLocale, null);
        if (!string.IsNullOrEmpty(saved) && IsSupported(saved))
        {
            current = saved;
        }
        else
        {
            string sysLang = Locale.Default?.Language ?? "en";
            current = sysLang switch
            {
                "zh" => "zh-CN",
                "ja" => "ja",
                "fr" => "fr",
                "es" => "es",
                _ => "en"
            };
        }

        activeMap = LoadLocaleFile(context, current);
    }

    public static void SetLanguage(Context context, string code)
    {
        if (!IsSupported(code)) return;
        current = code;
        activeMap = LoadLocaleFile(context, code);

        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        using var editor = prefs?.Edit();
        editor?.PutString(KeyLocale, code);
        editor?.Apply();
    }

    public static string Get(string key)
    {
        if (activeMap.TryGetValue(key, out var val)) return val;
        if (fallbackMap.TryGetValue(key, out var fb)) return fb;
        return key;
    }

    public static string Format(string key, params object[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    static bool IsSupported(string code) =>
        SupportedLanguages.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    static Dictionary<string, string> LoadLocaleFile(Context context, string code)
    {
        if (Cache.TryGetValue(code, out var cached))
            return cached;

        try
        {
            using var stream = context.Assets!.Open($"Locales/{code}.json");
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            Cache[code] = parsed;
            return parsed;
        }
        catch
        {
            return [];
        }
    }
}
