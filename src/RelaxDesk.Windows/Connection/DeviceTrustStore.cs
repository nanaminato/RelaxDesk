using System.IO;
using System.Text.Json;

namespace RelaxDesk.Windows;

public sealed class DeviceTrustStore(string file, SemaphoreSlim? operationGate = null)
{
    const int MaximumEntries = 32;
    const int MaximumBytes = 64 * 1024;
    readonly string path = Path.GetFullPath(file);
    readonly SemaphoreSlim operations = operationGate ?? new(1);

    public static DeviceTrustStore CurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "password-device-trust.json"));

    public static bool IsFingerprint(string? value) =>
        value is { Length: 64 } && value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');

    public Task<string[]> Load() => Run(Read);
    public Task<string[]> Clear() => Run(() => Write([]));

    public Task<string[]> Save(string fingerprint) => Run(() =>
    {
        Validate(fingerprint);
        var items = Read().Where(entry => entry != fingerprint).Prepend(fingerprint).ToArray();
        if (items.Length > MaximumEntries) throw new IOException("Trusted device list is full");
        return Write(items);
    });

    public Task<string[]> Remove(string fingerprint) => Run(() =>
    {
        if (!IsFingerprint(fingerprint)) throw new ArgumentException("Invalid device fingerprint", nameof(fingerprint));
        var previous = Read();
        var next = previous.Where(entry => entry != fingerprint).ToArray();
        return next.Length == previous.Length ? previous : Write(next);
    });

    async Task<T> Run<T>(Func<T> action)
    {
        await operations.WaitAsync();
        try { return await Task.Run(action); }
        finally { operations.Release(); }
    }

    string[] Read()
    {
        Guard();
        if (!File.Exists(path)) return [];
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length is < 2 or > MaximumBytes) throw new InvalidDataException("Device trust file length invalid");
        var result = JsonSerializer.Deserialize<string[]>(input) ?? throw new InvalidDataException("Missing Device trust records");
        if (result.Length > MaximumEntries) throw new InvalidDataException("Too many Device trust records");
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in result)
        {
            Validate(entry);
            if (!fingerprints.Add(entry)) throw new InvalidDataException("Duplicate trusted device");
        }
        return result;
    }

    static void Validate(string? fingerprint)
    {
        if (!IsFingerprint(fingerprint)) throw new InvalidDataException("Invalid trusted device fingerprint");
    }

    string[] Write(string[] entries)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entries);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Device trust file too large");
        Guard();
        string directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        Guard();
        string temporary = Path.Combine(directory, $"full-control-trust-{Guid.NewGuid():N}.tmp");
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
        if (Directory.Exists(path)) throw new IOException("Device trust path is a directory");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Device trust path is redirected");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
