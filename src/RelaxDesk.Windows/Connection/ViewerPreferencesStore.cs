using System.Text.Json;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Local presentation/request preferences only; never credentials or Host grants.
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

    public void Validate()
    {
        if (Format != 1 || Network is < 0 or > 2 || FileRate is < 0 or > 3 || !new ViewportOptions(Zoom).Valid)
            throw new InvalidDataException("Invalid Viewer preferences");
        VideoSettingsProtocol.Validate(new(FramesPerSecond, Bitrate, 1));
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
