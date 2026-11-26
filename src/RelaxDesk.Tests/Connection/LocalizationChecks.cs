using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RelaxDesk.Core.Localization;

internal static class LocalizationChecks
{
    public static int Run()
    {
        int checks = 0;
        void Check(bool value) { if (!value) throw new Exception($"Localization catalog check {checks + 1}"); checks++; }
        var assembly = typeof(LocalizationManager).Assembly;
        var packs = new Dictionary<string, Dictionary<string, string>>();
        foreach (string name in assembly.GetManifestResourceNames().Where(name => name.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var input = assembly.GetManifestResourceStream(name)!;
            using var json = JsonDocument.Parse(input);
            var root = json.RootElement;
            var entries = root.GetProperty("strings").EnumerateObject().ToArray();
            Check(entries.Select(entry => entry.Name).Distinct().Count() == entries.Length);
            packs.Add(root.GetProperty("code").GetString()!, entries.ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!));
        }
        Check(packs.Count == 5);
        var baseline = packs["en"];
        Check(baseline.Count >= 450);
        Check(baseline.ContainsKey("Session.AllAccess") && baseline.ContainsKey("Session.ComponentUnavailable"));
        Check(!baseline.Keys.Any(key=>key.StartsWith("Ui.FileTransferWindow.")||key.StartsWith("Ui.FileClipboardWindow.")));
        var manager = new LocalizationManager([], null, CultureInfo.GetCultureInfo("en-US"));
        foreach (var (code, entries) in packs)
        {
            Check(baseline.Keys.ToHashSet().SetEquals(entries.Keys));
            Check(manager.SetLanguage(code, persist:false));
            foreach (var (key, text) in entries)
            {
                Check(!string.IsNullOrWhiteSpace(text));
                var format = CompositeFormat.Parse(text);
                Check(format.MinimumArgumentCount == CompositeFormat.Parse(baseline[key]).MinimumArgumentCount);
                string[] Slots(string value) => Regex.Matches(value, @"(?<!\{)\{\d+(?:,[^:}]+)?(?::[^}]+)?\}")
                    .Select(match => match.Value).Order().ToArray();
                Check(Slots(text).SequenceEqual(Slots(baseline[key])));
                Check(manager.Get(key) == text);
                if (code is "en" or "fr" or "es") Check(!Regex.IsMatch(text, @"[\u4e00-\u9fff]"));
            }
        }
        Console.WriteLine($"PASS localization catalog: {baseline.Count} messages in five languages; key parity, placeholders, formats and no CJK leakage into English/French/Spanish");
        return checks;
    }
}
