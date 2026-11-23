using System.Security.Cryptography;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public sealed class IncomingTransferFile : IIncomingTransferFile
{
    readonly string destination, temporary;
    readonly bool overwrite;
    readonly IDisposable directoryLease;
    readonly Action checkSpace;
    readonly FileStream stream;
    readonly IncrementalHash hash;
    bool committed, disposed;
    public long ExpectedBytes { get; }
    public long ReceivedBytes { get; private set; }
    internal IncomingTransferFile(string destination, long expectedBytes, bool overwrite, IDisposable directoryLease, Action checkSpace)
    {
        this.destination = destination; ExpectedBytes = expectedBytes; this.overwrite = overwrite; this.directoryLease = directoryLease; this.checkSpace = checkSpace;
        temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".relaxdesk-" + Guid.NewGuid().ToString("N") + ".partial");
        var handle = WindowsFileRoot.Open(temporary, 0xC0000000, 0, 1, 0x48200000);
        try { stream = new FileStream(handle, FileAccess.ReadWrite, TransferLimits.ChunkBytes, true); hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); }
        catch { stream?.Dispose(); handle.Dispose(); File.Delete(temporary); throw; }
    }
    public async Task Write(long offset, byte[] data, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed || committed, this);
        TransferLimits.ValidateChunk(ExpectedBytes, ReceivedBytes, offset, data.Length); ct.ThrowIfCancellationRequested(); checkSpace();
        await stream.WriteAsync(data, ct); hash.AppendData(data); ReceivedBytes = checked(ReceivedBytes + data.Length);
    }
    public async Task Commit(string expectedHash, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed || committed, this);
        if (ReceivedBytes != ExpectedBytes || expectedHash.Length != 64 || !expectedHash.All(char.IsAsciiHexDigit)) throw new InvalidDataException("Incomplete file or invalid SHA256");
        byte[] actual = hash.GetHashAndReset();
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedHash))) throw new InvalidDataException("File SHA256 mismatch");
        await stream.FlushAsync(ct); await stream.DisposeAsync(); ct.ThrowIfCancellationRequested();
        if (File.Exists(destination) && (File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0) throw new IOException("Destination changed to reparse point");
        File.Move(temporary, destination, overwrite); committed = true;
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        Exception? first = null;
        try { await stream.DisposeAsync(); } catch (Exception error) { first = error; }
        try { hash.Dispose(); } catch (Exception error) { first ??= error; }
        try { if (!committed) File.Delete(temporary); } catch (Exception error) { first ??= error; }
        finally { directoryLease.Dispose(); }
        if (first is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(first).Throw();
    }
}
