using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// A session exposes only the files explicitly copied in Explorer and their descendants.
public sealed class ClipboardFileSources
{
    const string Prefix = "RelaxDesk-Offer-";
    sealed record Source(WindowsFileRoot Root, TransferEntry Entry);
    sealed record Offer(string Id, Dictionary<string, Source> Sources);
    Offer? current;

    public static bool IsOfferPath(string path) => path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public ClipboardFiles Describe(string[] absolutePaths)
    {
        Volatile.Write(ref current, null);
        string id = Prefix + Guid.NewGuid().ToString("N");
        var sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in absolutePaths)
        {
            string full = Path.GetFullPath(path);
            string name = Path.GetFileName(full);
            var root = new WindowsFileRoot(Path.GetDirectoryName(full) ?? throw new IOException("Cannot copy a drive root"));
            _ = root.DescribeClipboard([full]);
            bool directory = Directory.Exists(full);
            long length = directory ? 0 : new FileInfo(full).Length;
            if (!sources.TryAdd(name, new(root, new(id + "/" + name, directory, length))))
                throw new IOException("Copied entries must have distinct names");
        }
        var files = new ClipboardFiles(sources.Values.Select(source => source.Entry.Path).ToArray());
        if (!ClipboardFiles.Valid(files)) throw new InvalidDataException("Invalid clipboard file list");
        Volatile.Write(ref current, new(id, sources));
        return files;
    }

    public void Clear() => Volatile.Write(ref current, null);

    Offer Find(string path)
    {
        var offer = Volatile.Read(ref current);
        if (offer is null || !(path.Equals(offer.Id, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(offer.Id + "/", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Clipboard selection is no longer available");
        return offer;
    }

    static Source FindSource(Offer offer, string relative)
    {
        int separator = relative.IndexOf('/');
        string name = separator < 0 ? relative : relative[..separator];
        if (!offer.Sources.TryGetValue(name, out var source) || separator >= 0 && !source.Entry.Directory)
            throw new IOException("Path is outside the copied selection");
        return source;
    }

    public TransferEntry[] List(string path, int offset)
    {
        path = TransferPaths.Relative(path);
        var offer = Find(path);
        if (path.Length == offer.Id.Length)
            return offer.Sources.Values.Select(source => source.Entry).Skip(offset).Take(TransferLimits.ListPage).ToArray();
        string relative = path[(offer.Id.Length + 1)..];
        var folder = FindSource(offer, relative);
        return folder.Root.List(relative, offset).Select(entry => entry with { Path = offer.Id + "/" + entry.Path })
            .Where(entry => TransferPaths.Relative(entry.Path) == entry.Path).ToArray();
    }

    public TransferReadFile OpenRead(string path)
    {
        path = TransferPaths.Relative(path);
        var offer = Find(path);
        if (path.Length == offer.Id.Length) throw new IOException("Expected a copied file");
        string relative = path[(offer.Id.Length + 1)..];
        return FindSource(offer, relative).Root.OpenRead(relative);
    }
}
