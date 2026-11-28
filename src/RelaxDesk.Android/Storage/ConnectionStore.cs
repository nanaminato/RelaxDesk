using System.Text.Json;
using Android.Content;

namespace RelaxDesk.Mobile;

internal sealed record SavedHostEntry(string Name, string Host, int Port, string Fingerprint, string Username, long LastUsedUtcTicks);

/// <summary>
/// Persists recent Windows Host endpoints (excluding passwords) in private SharedPreferences.
/// </summary>
internal static class ConnectionStore
{
    const string PrefsName = "relaxdesk_hosts_v1";
    const string KeyEntries = "entries";
    const int MaxEntries = 16;

    public static List<SavedHostEntry> Load(Context context)
    {
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        string? json = prefs?.GetString(KeyEntries, null);
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<SavedHostEntry>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void SaveOrUpdate(Context context, SavedHostEntry entry)
    {
        var list = Load(context);
        list.RemoveAll(e => string.Equals(e.Host, entry.Host, StringComparison.OrdinalIgnoreCase) && e.Port == entry.Port);
        list.Insert(0, entry);
        if (list.Count > MaxEntries) list.RemoveRange(MaxEntries, list.Count - MaxEntries);

        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        using var editor = prefs?.Edit();
        editor?.PutString(KeyEntries, JsonSerializer.Serialize(list));
        editor?.Apply();
    }
}
