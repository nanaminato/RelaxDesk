using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace RelaxDesk.Core.Localization;

public sealed record LanguageOption(string Code, string DisplayName, bool IsCustom);

public sealed record LanguagePreference(string LanguageCode);

public sealed class LocalizationManager
{
    public const string DefaultLanguageCode = "en";
    public const long MaxTranslationFileBytes = 1024 * 1024; // 1 MiB

    static readonly Lazy<LocalizationManager> SharedInstance = new(() => CreateDefault());
    public static LocalizationManager Current => SharedInstance.Value;

    readonly object gate = new();
    readonly Dictionary<string, LanguagePack> packs = new(StringComparer.OrdinalIgnoreCase);
    readonly string? preferencePath;
    string currentCode = DefaultLanguageCode;

    public event EventHandler? LanguageChanged;

    public LocalizationManager(
        IEnumerable<string>? externalDirectories = null,
        string? preferenceFilePath = null,
        CultureInfo? systemCulture = null,
        bool loadEmbedded = true)
    {
        preferencePath = preferenceFilePath;
        if (loadEmbedded)
            LoadEmbeddedPacks();
        if (externalDirectories is not null)
        {
            foreach (string dir in externalDirectories)
                LoadExternalDirectory(dir);
        }

        string? saved = TryLoadSavedLanguage();
        if (!string.IsNullOrWhiteSpace(saved) && packs.ContainsKey(saved))
        {
            currentCode = packs[saved].Code;
        }
        else
        {
            currentCode = ResolveSystemLanguage(systemCulture ?? CultureInfo.CurrentUICulture);
        }
    }

    public static LocalizationManager CreateDefault()
    {
        string appLocales = Path.Combine(AppContext.BaseDirectory, "locales");
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userRoot = Path.Combine(localAppData, "RelaxDesk");
        string userLocales = Path.Combine(userRoot, "locales");
        string prefFile = Path.Combine(userRoot, "language.json");
        return new LocalizationManager([appLocales, userLocales], prefFile, CultureInfo.CurrentUICulture, loadEmbedded: true);
    }

    public string CurrentLanguageCode
    {
        get { lock (gate) return currentCode; }
    }

    public IReadOnlyList<LanguageOption> AvailableLanguages
    {
        get
        {
            lock (gate)
            {
                return packs.Values
                    .OrderBy(p => p.IsCustom ? 1 : 0)
                    .ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new LanguageOption(p.Code, p.DisplayName, p.IsCustom))
                    .ToArray();
            }
        }
    }

    public bool SetLanguage(string code, bool persist = true)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        string resolved;
        lock (gate)
        {
            if (!packs.TryGetValue(code.Trim(), out var pack)) return false;
            resolved = pack.Code;
            if (string.Equals(currentCode, resolved, StringComparison.OrdinalIgnoreCase))
            {
                if (persist) TrySaveLanguage(resolved);
                return true;
            }
            currentCode = resolved;
        }
        if (persist) TrySaveLanguage(resolved);
        LanguageChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;
        lock (gate)
        {
            if (packs.TryGetValue(currentCode, out var current) &&
                current.Strings.TryGetValue(key, out string? value) &&
                !string.IsNullOrEmpty(value))
            {
                return value;
            }
            if (!string.Equals(currentCode, DefaultLanguageCode, StringComparison.OrdinalIgnoreCase) &&
                packs.TryGetValue(DefaultLanguageCode, out var fallback) &&
                fallback.Strings.TryGetValue(key, out string? fallbackValue) &&
                !string.IsNullOrEmpty(fallbackValue))
            {
                return fallbackValue;
            }
        }
        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string template = Get(key);
        if (args.Length == 0) return template;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    public string ResolveSystemLanguage(CultureInfo culture)
    {
        lock (gate)
        {
            if (packs.Count == 0) return DefaultLanguageCode;

            // 1. Exact match (e.g., "zh-CN", "en-US")
            if (!string.IsNullOrWhiteSpace(culture.Name) && packs.TryGetValue(culture.Name, out var exact))
                return exact.Code;

            // 2. Parent chain match (e.g., "zh-Hans-CN" -> "zh-Hans" -> "zh")
            for (var parent = culture.Parent; parent is not null && !string.IsNullOrEmpty(parent.Name); parent = parent.Parent)
            {
                if (packs.TryGetValue(parent.Name, out var parentMatch))
                    return parentMatch.Code;
            }

            // 3. Two-letter ISO language prefix match (e.g., "zh-TW" or "zh" -> "zh-CN", "es-MX" -> "es", "ja-JP" -> "ja")
            string iso = culture.TwoLetterISOLanguageName;
            if (!string.IsNullOrWhiteSpace(iso) && !string.Equals(iso, "iv", StringComparison.OrdinalIgnoreCase))
            {
                if (packs.TryGetValue(iso, out var isoExact))
                    return isoExact.Code;

                var prefixMatch = packs.Values
                    .OrderBy(p => p.IsCustom ? 1 : 0)
                    .ThenBy(p => p.Code, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault(p => p.Code.StartsWith(iso + "-", StringComparison.OrdinalIgnoreCase));
                if (prefixMatch is not null)
                    return prefixMatch.Code;
            }

            // 4. Fallback to English if completely unmatched
            return packs.TryGetValue(DefaultLanguageCode, out var english) ? english.Code : DefaultLanguageCode;
        }
    }

    void LoadEmbeddedPacks()
    {
        var assembly = typeof(LocalizationManager).Assembly;
        const string prefix = "RelaxDesk.Core.Localization.Locales.";
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string fileStem = resourceName[prefix.Length..^5];
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;
            if (TryParsePack(stream, fileStem, isCustom: false, out var pack) && pack is not null)
                MergePack(pack);
        }
    }

    public void LoadExternalDirectory(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
            return;

        string[] files;
        try
        {
            files = Directory.GetFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length == 0 || info.Length > MaxTranslationFileBytes)
                    continue;
                using var stream = File.OpenRead(file);
                string fileStem = Path.GetFileNameWithoutExtension(file);
                if (TryParsePack(stream, fileStem, isCustom: true, out var pack) && pack is not null)
                    MergePack(pack);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore malformed or unreadable user translation files so startup never fails.
            }
        }
    }

    void MergePack(LanguagePack incoming)
    {
        lock (gate)
        {
            if (packs.TryGetValue(incoming.Code, out var existing))
            {
                foreach (var kv in incoming.Strings)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value is not null)
                        existing.Strings[kv.Key] = kv.Value;
                }
                if (!string.IsNullOrWhiteSpace(incoming.DisplayName))
                    existing.DisplayName = incoming.DisplayName;
            }
            else
            {
                packs[incoming.Code] = incoming;
            }
        }
    }

    static bool TryParsePack(Stream stream, string fallbackCode, bool isCustom, out LanguagePack? pack)
    {
        pack = null;
        try
        {
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            string code = fallbackCode.Trim();
            string displayName = code;
            var strings = new Dictionary<string, string>(StringComparer.Ordinal);

            if (doc.RootElement.TryGetProperty("strings", out var stringsElement) &&
                stringsElement.ValueKind == JsonValueKind.Object)
            {
                if (doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
                {
                    string? rawCode = codeEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(rawCode)) code = rawCode;
                }
                if (doc.RootElement.TryGetProperty("displayName", out var nameEl) && nameEl.ValueKind == JsonValueKind.String)
                {
                    string? rawName = nameEl.GetString()?.Trim();
                    if (!string.IsNullOrWhiteSpace(rawName)) displayName = rawName;
                }
                foreach (var prop in stringsElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        strings[prop.Name] = prop.Value.GetString() ?? string.Empty;
                }
            }
            else
            {
                // Flat JSON dictionary support: { "Key": "Value", ... }
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.ValueKind != JsonValueKind.String) continue;
                    string val = prop.Value.GetString() ?? string.Empty;
                    if (string.Equals(prop.Name, "_code", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                        code = val.Trim();
                    else if (string.Equals(prop.Name, "_displayName", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(val))
                        displayName = val.Trim();
                    else
                        strings[prop.Name] = val;
                }
            }

            if (string.IsNullOrWhiteSpace(code)) return false;
            pack = new LanguagePack(code, string.IsNullOrWhiteSpace(displayName) ? code : displayName, isCustom, strings);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    string? TryLoadSavedLanguage()
    {
        if (string.IsNullOrWhiteSpace(preferencePath) || !File.Exists(preferencePath))
            return null;
        try
        {
            var info = new FileInfo(preferencePath);
            if (info.Length == 0 || info.Length > 4096) return null;
            using var stream = File.OpenRead(preferencePath);
            var pref = JsonSerializer.Deserialize<LanguagePreference>(stream);
            return pref?.LanguageCode?.Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    void TrySaveLanguage(string code)
    {
        if (string.IsNullOrWhiteSpace(preferencePath)) return;
        try
        {
            string? dir = Path.GetDirectoryName(preferencePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            string temp = preferencePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new LanguagePreference(code)));
                File.Move(temp, preferencePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: keep running with the in-memory language selection.
        }
    }

    sealed class LanguagePack(string code, string displayName, bool isCustom, Dictionary<string, string> strings)
    {
        public string Code { get; } = code;
        public string DisplayName { get; set; } = displayName;
        public bool IsCustom { get; } = isCustom;
        public Dictionary<string, string> Strings { get; } = strings;
    }
}

public static class Strings
{
    public static string Get(string key) => LocalizationManager.Current.Get(key);
    public static string Format(string key, params object?[] args) => LocalizationManager.Current.Format(key, args);
}
