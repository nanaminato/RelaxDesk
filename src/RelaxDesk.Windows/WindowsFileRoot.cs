using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Locally selected root only. Parent handles prevent rename/reparse substitution;
// final files open the reparse point itself, never its target. No elevated access.
public sealed class WindowsFileRoot
{
    readonly string root;
    readonly string identity;
    public WindowsFileRoot(string selectedDirectory)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selectedDirectory));
        if (!Path.IsPathFullyQualified(root) || root.StartsWith("\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(root)!).DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram) || !Directory.Exists(root)) throw new IOException("A local existing directory must be selected");
        using var locked = DirectoryLease.Acquire(root, false);
        if (!FinalName(locked.Last).Equals(Extended(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("Aliased transfer root unsupported");
        identity = Stamp(Information(locked.Last));
    }
    static string Stamp(FileInformation info) => $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}:{info.Created.dwHighDateTime:X8}{info.Created.dwLowDateTime:X8}";
    public bool SameIdentity() { using var lease=DirectoryLease.Acquire(root,false);return Stamp(Information(lease.Last))==identity; }
    internal static string Extended(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
    internal string Resolve(string relative, bool allowRoot = false)
    {
        string safe = TransferPaths.Relative(relative, allowRoot);
        string full = safe.Length == 0 ? root : Path.GetFullPath(Path.Combine(root, safe.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new IOException("Transfer path outside authorized root");
        return full;
    }
    void VerifyIdentity() { if(!SameIdentity())throw new IOException("Selected directory identity changed"); }
    public ClipboardFiles DescribeClipboard(string[] absolutePaths)
    {
        VerifyIdentity();string prefix=Path.EndsInDirectorySeparator(root)?root:root+Path.DirectorySeparatorChar;
        var relative=absolutePaths.Select(path=>{string full=Path.GetFullPath(path);if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("Clipboard path outside authorized root");return TransferPaths.Relative(full[prefix.Length..]);}).ToArray();
        var result=new ClipboardFiles(relative);if(!ClipboardFiles.Valid(result))throw new InvalidDataException("Invalid scoped clipboard file list");
        foreach(string path in relative)_=InspectClipboardPath(path);return result;
    }
    public string[] ResolveClipboard(ClipboardFiles files)
    {if(!ClipboardFiles.Valid(files))throw new InvalidDataException("Invalid clipboard paths");VerifyIdentity();return files.Paths.Select(InspectClipboardPath).ToArray();}
    string InspectClipboardPath(string relative)
    {
        string full=Resolve(relative);var attributes=File.GetAttributes(full);
        if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Clipboard links unsupported");
        if((attributes&FileAttributes.Directory)!=0){using var lease=DirectoryLease.Acquire(full,false);VerifyIdentity();}
        else{var file=OpenRead(relative);file.DisposeAsync().AsTask().GetAwaiter().GetResult();}
        return full;
    }
    public void CreateDirectory(string relative) { using var rootLease=DirectoryLease.Acquire(root,false);VerifyIdentity();string directory = Resolve(relative); EnsureSpace(1024 * 1024); using var lease = DirectoryLease.Acquire(directory, true); }
    public TransferEntry[] List(string relative, int offset)
    {
        if (offset is < 0 or > TransferPaths.MaximumEntries) throw new InvalidDataException("Invalid directory page");
        string directory = Resolve(relative, true); using var lease = DirectoryLease.Acquire(directory, false);
        VerifyIdentity();
        var files = new DirectoryInfo(directory).EnumerateFileSystemInfos().Take(TransferPaths.MaximumEntries + 1).ToArray();
        if (files.Length > TransferPaths.MaximumEntries) throw new IOException("Directory entry limit exceeded");
        var result = new List<TransferEntry>();
        foreach (var item in files.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.Name.StartsWith(".relaxdesk-", StringComparison.OrdinalIgnoreCase)) continue;
            string name = relative.Length == 0 ? item.Name : relative.Replace('\\', '/') + "/" + item.Name;
            try { name = TransferPaths.Relative(name); } catch (InvalidDataException) { continue; }
            bool folder = (item.Attributes & FileAttributes.Directory) != 0;
            long length = folder ? 0 : ((FileInfo)item).Length;
            if (length <= TransferLimits.MaximumFileBytes) result.Add(new(name, folder, length));
        }
        return result.Skip(offset).Take(TransferLimits.ListPage).ToArray();
    }
    public TransferReadFile OpenRead(string relative)
    {
        string file = Resolve(relative); var lease = DirectoryLease.Acquire(Path.GetDirectoryName(file)!, false);
        SafeFileHandle? handle = null;
        try
        {
            VerifyIdentity();
            handle = Open(file, 0x80000000, 1, 3, 0x48200000);
            var info = Information(handle);
            if ((info.Attributes & (0x400u | 0x10u)) != 0 || info.Links != 1 || !FinalName(handle).Equals(Extended(file), StringComparison.OrdinalIgnoreCase)) throw new IOException("Reparse/multilink/aliased file unsupported");
            var stream = new FileStream(handle, FileAccess.Read, TransferLimits.ChunkBytes, true); handle = null;
            try
            {
                if (stream.Length > TransferLimits.MaximumFileBytes) throw new IOException("File exceeds transfer limit");
                return new(stream, lease);
            }
            catch { stream.Dispose(); throw; }
        }
        catch { handle?.Dispose(); lease.Dispose(); throw; }
    }
    public IncomingTransferFile CreateUpload(string relative, long expectedBytes, bool overwrite)
    {
        if (expectedBytes is < 0 or > TransferLimits.MaximumFileBytes) throw new InvalidDataException("File exceeds transfer limit");
        using var rootLease=DirectoryLease.Acquire(root,false);VerifyIdentity();
        string destination = Resolve(relative); EnsureSpace(Math.Max(expectedBytes, 1024 * 1024)); var lease = DirectoryLease.Acquire(Path.GetDirectoryName(destination)!, true);
        try
        {
            if (Directory.Exists(destination) || File.Exists(destination) && !overwrite) throw new TransferRejectedException("exists");
            if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("Destination is a reparse point");
            EnsureSpace(expectedBytes);
            return new(destination, expectedBytes, overwrite, lease, () => EnsureSpace(TransferLimits.ChunkBytes));
        }
        catch { lease.Dispose(); throw; }
    }
    void EnsureSpace(long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(root)!);
        if (drive.AvailableFreeSpace < TransferLimits.MinimumFreeBytes || bytes > drive.AvailableFreeSpace - TransferLimits.MinimumFreeBytes) throw new TransferRejectedException("disk-full");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder name, uint length, uint flags);
    [StructLayout(LayoutKind.Sequential)] internal struct FileInformation
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    internal static SafeFileHandle Open(string path, uint access, uint share, uint disposition, uint flags)
    {
        var handle = CreateFile(Extended(path), access, share, 0, disposition, flags, 0);
        if (!handle.IsInvalid) return handle;
        int code = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException($"Scoped file open failed Win32={code}");
    }
    internal static FileInformation Information(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new IOException($"Scoped file inspection failed Win32={Marshal.GetLastWin32Error()}");
        return info;
    }
    internal static string FinalName(SafeFileHandle handle)
    {
        var name = new StringBuilder(32768); uint length = GetFinalPathNameByHandle(handle, name, (uint)name.Capacity, 0);
        if (length == 0 || length >= name.Capacity) throw new IOException("Scoped file final path unavailable");
        return name.ToString();
    }
    internal sealed class DirectoryLease : IDisposable
    {
        readonly List<SafeFileHandle> handles = [];
        public SafeFileHandle Last => handles[^1];
        public static DirectoryLease Acquire(string path, bool create)
        {
            var lease = new DirectoryLease();
            try
            {
                string full = Path.GetFullPath(path), current = Path.GetPathRoot(full)!;
                void Hold(string directory)
                {
                    var handle = Open(directory, 0x80, 1, 3, 0x02200000); lease.handles.Add(handle);
                    uint attributes = Information(handle).Attributes;
                    if ((attributes & 0x400) != 0 || (attributes & 0x10) == 0 || !FinalName(handle).Equals(Extended(Path.TrimEndingDirectorySeparator(directory)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Reparse/aliased directory unsupported");
                }
                Hold(current);
                foreach (string segment in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (segment.EndsWith(' ') || segment.EndsWith('.')) throw new IOException("Aliased directory unsupported");
                    current = Path.Combine(current, segment);
                    if (create && !Directory.Exists(current)) Directory.CreateDirectory(current);
                    Hold(current);
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public void Dispose() { for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); handles.Clear(); }
    }
}

public sealed class TransferReadFile(FileStream stream, IDisposable directoryLease) : IAsyncDisposable
{
    public FileStream Stream { get; } = stream;
    public async ValueTask DisposeAsync() { try { await Stream.DisposeAsync(); } finally { directoryLease.Dispose(); } }
}
