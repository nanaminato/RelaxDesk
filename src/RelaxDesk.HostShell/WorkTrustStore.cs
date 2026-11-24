using System.IO;
using System.Text.Json;
using RelaxDesk.Core;

namespace RelaxDesk.HostShell;

internal sealed record WorkTrustEntry(string Fingerprint, string[] Features, string? FileRoot);

internal sealed class WorkTrustStore(string file, SemaphoreSlim? operationGate = null)
{
    const int MaximumEntries = 32;
    const int MaximumBytes = 64 * 1024;
    readonly string path = Path.GetFullPath(file);
    readonly SemaphoreSlim operations = operationGate ?? new(1);

    internal static WorkTrustStore CurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "work-trust.json"));

    internal static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');

    public Task<WorkTrustEntry[]> Load() => Run(Read);

    public Task<WorkTrustEntry[]> Save(WorkTrustEntry item) => Run(() =>
    {
        Validate(item);
        var items = Read().Where(entry => entry.Fingerprint != item.Fingerprint).Prepend(item).ToArray();
        if (items.Length > MaximumEntries) throw new IOException("Trusted device list is full");
        return Write(items);
    });

    public Task<WorkTrustEntry[]> Remove(string fingerprint) => Run(() =>
    {
        if (!IsFingerprint(fingerprint)) throw new ArgumentException("Invalid device fingerprint", nameof(fingerprint));
        var previous = Read();
        var next = previous.Where(entry => entry.Fingerprint != fingerprint).ToArray();
        return next.Length == previous.Length ? previous : Write(next);
    });

    async Task<T> Run<T>(Func<T> action)
    {
        await operations.WaitAsync();
        try { return await Task.Run(action); }
        finally { operations.Release(); }
    }

    WorkTrustEntry[] Read()
    {
        Guard();
        if (!File.Exists(path)) return [];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 2 or > MaximumBytes) throw new InvalidDataException("Work trust file length invalid");
        var result = JsonSerializer.Deserialize<WorkTrustEntry[]>(input) ?? throw new InvalidDataException("Missing Work trust records");
        if (result.Length > MaximumEntries) throw new InvalidDataException("Too many Work trust records");
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in result)
        {
            Validate(entry);
            if (!fingerprints.Add(entry.Fingerprint)) throw new InvalidDataException("Duplicate Work device");
        }
        return result;
    }

    static void Validate(WorkTrustEntry? entry)
    {
        if (entry is null || !IsFingerprint(entry.Fingerprint) || entry.Features is null ||
            entry.Features.Contains(SessionCapabilities.FileTransfer) != (entry.FileRoot is not null) ||
            entry.FileRoot is { } root && (root.Length > 2048 || !Path.IsPathFullyQualified(root)))
            throw new InvalidDataException("Invalid Work trust record");
        SessionCapabilities.Validate(entry.Features);
    }

    WorkTrustEntry[] Write(WorkTrustEntry[] entries)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Work trust file too large");
        Guard();
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        Guard();
        string temporary = Path.Combine(directory, $"work-trust-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(true);
            }
            Guard();
            File.Move(temporary, path, true);
            return entries;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    void Guard()
    {
        if (Directory.Exists(path)) throw new IOException("Work trust path is a directory");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Work trust path is redirected");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
