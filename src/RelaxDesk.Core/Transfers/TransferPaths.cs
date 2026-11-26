namespace RelaxDesk.Core;

public static class TransferPaths
{
    static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    public const int MaximumLength = 512, MaximumEntries = 10000;
    public static string Relative(string path, bool allowRoot = false)
    {
        if (path is null) throw new InvalidDataException("Missing relative transfer path");
        try { _ = StrictUtf8.GetByteCount(path); } catch (System.Text.EncoderFallbackException) { throw new InvalidDataException("Invalid Unicode transfer path"); }
        if (path.Length == 0 && allowRoot) return "";
        if (path.Length is 0 or > MaximumLength || path.StartsWith('/') || path.StartsWith('\\') || path.Contains(':')) throw new InvalidDataException("Invalid relative transfer path");
        string[] segments = path.Replace('\\', '/').Split('/');
        if (segments.Length > 64) throw new InvalidDataException("Transfer path too deep");
        foreach (string segment in segments)
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)) || segment.StartsWith(".relaxdesk-", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid transfer path component");
            string device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(device[3])) throw new InvalidDataException("Reserved transfer path component");
        }
        return string.Join('/', segments);
    }
}

public static class TransferLimits
{
    public const int ChunkBytes = 64 * 1024, ListPage = 64;
    public const long MaximumFileBytes = 50L * 1024 * 1024 * 1024, MinimumFreeBytes = 128L * 1024 * 1024;
    public static void ValidateChunk(long expectedSize, long received, long offset, int length)
    {
        if (expectedSize is < 0 or > MaximumFileBytes || received < 0 || offset != received || length is < 1 or > ChunkBytes || offset > expectedSize || length > expectedSize - offset)
            throw new InvalidDataException("Invalid file chunk boundary");
    }
}
