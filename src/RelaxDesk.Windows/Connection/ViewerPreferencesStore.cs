using System.Text.Json;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public sealed record ViewerLoginName(string Endpoint, string UserName);

// Local preferences and login names only; never passwords or Host grants.
public sealed record ViewerPreferences
{
    public int Format { get; init; } = 1;
    public bool FullScreen { get; init; } = true;
    public bool Gpu { get; init; } = true;
    public bool Topology { get; init; } = true;
    public bool ClipboardText { get; init; } = true;
    public bool ClipboardImage { get; init; } = true;
    public bool ClipboardHtml { get; init; } = true;
    public bool FileClipboard { get; init; } = true;
    public bool Audio { get; init; } = true;
    public bool RememberConnections { get; init; } = true;
    public int Network { get; init; }
    public int FileRate { get; init; }
    public int Zoom { get; init; } = 100;
    public int FramesPerSecond { get; init; } = 30;
    public int Bitrate { get; init; } = 8_000_000;
    public ViewerLoginName[] LoginNames { get; init; } = [];

    public void Validate()
    {
        if (Format != 1 || Network is < 0 or > 2 || FileRate is < 0 or > 3 || !new ViewportOptions(Zoom).Valid)
            throw new InvalidDataException("Invalid Viewer preferences");
        VideoSettingsProtocol.Validate(new(FramesPerSecond, Bitrate, 1));
        if (LoginNames is null || LoginNames.Length > 20 || LoginNames.Any(entry => entry is null ||
            !System.Net.IPEndPoint.TryParse(entry.Endpoint, out var endpoint) || endpoint.Port is < 1 or > 65535 ||
            endpoint.ToString() != entry.Endpoint || !PasswordVerifier.ValidUserName(entry.UserName)) ||
            LoginNames.Select(entry => entry.Endpoint).Distinct(StringComparer.Ordinal).Count() != LoginNames.Length)
            throw new InvalidDataException("Invalid remembered login names");
    }
}

public sealed class ViewerPreferencesStore(string file)
{
    readonly string path = Path.GetFullPath(file);
    void Guard()
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Preferences path is redirected"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public ViewerPreferences Load()
    {
        Guard();
        if (!File.Exists(path)) return new();
        using var input = File.OpenRead(path);
        if (input.Length > 16_384) throw new InvalidDataException("Preferences file too large");
        var result = JsonSerializer.Deserialize<ViewerPreferences>(input) ?? throw new InvalidDataException("Missing preferences");
        result.Validate();
        return result;
    }
    public void Save(ViewerPreferences value)
    {
        value.Validate(); Guard();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); Guard();
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, "viewer-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value));
            Guard(); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
