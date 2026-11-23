using System.Security.Cryptography;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Process-local only. Authorization is checked by the current data worker before
// every lookup; a ticket never grants a new session access to this directory.
public sealed class FileTransferResumeStore : IAsyncDisposable
{
    public const int MaximumEntries = 8;
    public const long MaximumRetainedBytes = 64L * 1024 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(5);
    readonly string directory;
    readonly object sync = new();
    readonly List<Entry> entries = [];
    readonly TimeProvider clock;
    readonly Action<string>? diagnostic;
    public int CleanupFailures { get; private set; }
    readonly CancellationTokenSource stop = new();
    readonly Task cleanup;
    bool disposed;
    string scope = Guid.NewGuid().ToString("N");
    WindowsFileRoot root;
    internal sealed class Entry
    {
        public required string Path;
        public bool Download, Overwrite, Active = true, Committed;
        public long Length, SentBytes;
        public string Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), FinalHash = "";
        public DateTimeOffset Expires;
        public IncomingTransferFile? Upload;
        public TransferReadFile? Source;
        public IncrementalHash? CurrentHash, PreviousHash;
        public long CurrentOffset, PreviousOffset;
        public override string ToString() => "Resume entry [paths/tickets redacted]";
    }
    public FileTransferResumeStore(string directory, TimeProvider? clock = null, Action<string>? diagnostic = null)
    {
        this.directory = Path.GetFullPath(directory); root = new(this.directory); this.clock = clock ?? TimeProvider.System; this.diagnostic = diagnostic;
        cleanup = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            try { while (await timer.WaitForNextTickAsync(stop.Token)) await Prune(); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
    }
    public async Task<string> Scope()
    {
        if (!root.SameIdentity()) { await Clear(); root = new(directory); }
        await Prune(); lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); return scope; }
    }
    internal async Task<Entry> Begin(string path, long length, bool overwrite, bool download)
    {
        await Prune(); path = TransferPaths.Relative(path);
        var entry = new Entry { Path = path, Length = length, Overwrite = overwrite, Download = download };
        lock (sync) { ObjectDisposedException.ThrowIf(disposed, this); if (entries.Count >= MaximumEntries) throw new TransferRejectedException("busy"); entries.Add(entry); }
        try
        {
            if (download) { entry.Source = root.OpenRead(path); entry.Length = entry.Source.Stream.Length; }
            else entry.Upload = root.CreateUpload(path, length, overwrite);
            return entry;
        }
        catch { await Remove(entry); throw; }
    }
    internal async Task<Entry> Resume(string token, string path, long length, bool overwrite, bool download)
    {
        await Prune(); path = TransferPaths.Relative(path); Entry? found;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            found = entries.FirstOrDefault(entry => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(entry.Token), Convert.FromHexString(token)));
            if (found is null || found.Active || found.Download != download || !found.Path.Equals(path, StringComparison.Ordinal) || !download && (found.Length != length || found.Overwrite != overwrite)) throw new TransferRejectedException("resume-unavailable");
            found.Active = true; found.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        }
        return found;
    }
    internal async Task Suspend(Entry entry, bool allowed)
    {
        bool keep;
        lock (sync)
        {
            keep = allowed && !disposed && entries.Contains(entry);
            entry.Active = false; entry.Expires = clock.GetUtcNow() + Retention;
            if (entries.Sum(item => item.Upload?.ReceivedBytes ?? 0) > MaximumRetainedBytes) keep = false;
        }
        if (!keep) await Remove(entry);
    }
    internal async Task Remove(Entry entry)
    {
        lock (sync) entries.Remove(entry);
        await Release(entry);
    }
    internal async Task Forget(string token)
    {
        Entry? entry; lock (sync)
        {
            entry = entries.FirstOrDefault(item => !item.Active && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(item.Token), Convert.FromHexString(token)));
            if (entry is null) throw new TransferRejectedException("resume-unavailable"); entries.Remove(entry);
        }
        await Release(entry);
    }
    async Task Release(Entry entry)
    {
        void Failed(Exception error) { CleanupFailures++; diagnostic?.Invoke($"file_resume_cleanup_failed error_type={error.GetType().Name} hresult=0x{error.HResult:X8}"); }
        if (entry.Upload is { } upload) { entry.Upload = null; try { await upload.DisposeAsync(); } catch (Exception error) { Failed(error); } }
        if (entry.Source is { } source) { entry.Source = null; try { await source.DisposeAsync(); } catch (Exception error) { Failed(error); } }
        entry.CurrentHash?.Dispose(); entry.PreviousHash?.Dispose(); entry.CurrentHash = entry.PreviousHash = null;
    }
    public async Task Clear()
    {
        Entry[] abandoned;
        lock (sync)
        {
            // The Host joins its file worker before clearing/rebinding scope.
            if (entries.Any(entry => entry.Active)) throw new InvalidOperationException("Cannot clear an active file worker");
            abandoned = entries.ToArray(); entries.Clear(); scope = Guid.NewGuid().ToString("N");
        }
        foreach (var entry in abandoned) await Release(entry);
    }
    async Task Prune()
    {
        Entry[] expired;
        lock (sync) { expired = entries.Where(entry => !entry.Active && entry.Expires <= clock.GetUtcNow()).ToArray(); foreach (var entry in expired) entries.Remove(entry); }
        foreach (var entry in expired) await Release(entry);
    }
    public async ValueTask DisposeAsync()
    {
        lock (sync) { if (disposed) return; disposed = true; }
        stop.Cancel(); await cleanup; await Clear(); stop.Dispose();
    }
}
