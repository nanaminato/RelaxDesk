using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace RelaxDesk.Windows;

// Diagnostic text only: never pass protocol bodies, credentials or user content.
// File work stays on one worker; callers never wait for disk or acquire its mutex.
public sealed class DiagnosticFileLog : IAsyncDisposable
{
    sealed record Entry(string? Line, TaskCompletionSource? Barrier = null);
    const int MaximumMessageCharacters = 2048;
    readonly string path;
    readonly long maximumBytes;
    readonly int archives;
    readonly Channel<Entry> queue;
    readonly Task worker;
    long dropped, failures;
    public string FilePath => path;
    public long Dropped => Interlocked.Read(ref dropped);
    public long FileFailures => Interlocked.Read(ref failures);
    public int QueuedRecords => queue.Reader.Count;
    public DiagnosticFileLog(string file, long maximumBytes = 2 * 1024 * 1024, int archives = 3, int capacity = 64)
    {
        if (maximumBytes is < 8192 or > 16 * 1024 * 1024 || archives is < 1 or > 8 || capacity is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        path = Path.GetFullPath(file); this.maximumBytes = maximumBytes; this.archives = archives;
        queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        worker = Task.Run(Consume);
    }
    public static string Normalize(string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        var text = new StringBuilder(Math.Min(diagnostic.Length, MaximumMessageCharacters));
        foreach (char value in diagnostic.AsSpan(0, Math.Min(diagnostic.Length, MaximumMessageCharacters)))
            text.Append(char.IsControl(value) ? ' ' : value);
        if (diagnostic.Length > MaximumMessageCharacters) text.Append(" [truncated]");
        return text.ToString();
    }
    public bool TryWrite(string diagnostic)
    {
        string line = $"{DateTimeOffset.UtcNow:O} {Normalize(diagnostic)}{Environment.NewLine}";
        if (queue.Writer.TryWrite(new(line))) return true;
        Interlocked.Increment(ref dropped); return false;
    }
    public async Task<bool> Flush(TimeSpan? timeout = null)
    {
        using var deadline = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await queue.Writer.WriteAsync(new(null, barrier), deadline.Token);
            await barrier.Task.WaitAsync(deadline.Token); return true;
        }
        catch (Exception error) when (error is OperationCanceledException or ChannelClosedException) { return false; }
    }
    void Guard(string file)
    {
        if (Directory.Exists(file)) throw new IOException("Diagnostic file is a directory");
        for (string? current = file; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Diagnostic location is redirected"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    string Archive(int index) => path + "." + index;
    void Append(string line)
    {
        // Serialize rotation between processes sharing this same-user log path.
        string name = "Local\\RelaxDesk.Log." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        using var mutex = new Mutex(false, name); bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(100)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) throw new IOException("Diagnostic writer busy");
        try
        {
            Guard(path); for (int i = 1; i <= archives; i++) Guard(Archive(i));
            foreach (string owned in Enumerable.Range(0, archives + 1).Select(i => i == 0 ? path : Archive(i)))
                if (File.Exists(owned) && new FileInfo(owned).Length > maximumBytes) throw new IOException("Existing diagnostic file exceeds limit");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); Guard(path);
            int bytes = Encoding.UTF8.GetByteCount(line);
            if (File.Exists(path) && new FileInfo(path).Length > maximumBytes - bytes)
            {
                if (File.Exists(Archive(archives))) File.Delete(Archive(archives));
                for (int i = archives - 1; i >= 1; i--) if (File.Exists(Archive(i))) File.Move(Archive(i), Archive(i + 1));
                File.Move(path, Archive(1));
            }
            using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            byte[] content = Encoding.UTF8.GetBytes(line); file.Write(content);
        }
        finally { mutex.ReleaseMutex(); }
    }
    async Task Consume()
    {
        await foreach (var entry in queue.Reader.ReadAllAsync())
        {
            if (entry.Barrier is { } barrier) { barrier.TrySetResult(); continue; }
            try { Append($"logger_dropped={Dropped} logger_file_failures={FileFailures} " + entry.Line!); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException or WaitHandleCannotBeOpenedException)
            { Interlocked.Increment(ref failures); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await worker.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { } // Bounded shutdown; a blocked filesystem cannot block app exit.
    }
}

public static class DiagnosticCorrelation
{
    static readonly AsyncLocal<string?> current = new();
    public static string Current => current.Value ?? "none";
    public static IDisposable Begin()
    {
        string? previous = current.Value; current.Value = Guid.NewGuid().ToString("N");
        return new Scope(previous);
    }
    sealed class Scope(string? previous) : IDisposable
    { bool disposed; public void Dispose() { if (!disposed) { current.Value = previous; disposed = true; } } }
}
