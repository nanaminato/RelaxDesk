using System.Security.Cryptography;

namespace RelaxDesk.Core;

// Owned by one logical queue job, never persisted/logged. The caller either
// reuses it after fresh authorization or disposes it to delete local partials.
public sealed class TransferResumeState : IAsyncDisposable
{
    readonly bool immutableUploadSource;
    Stream? sourceIdentity;
    IncrementalHash? currentHash, previousHash;
    long currentOffset, previousOffset;
    public long LastPrefixReadBytes { get; internal set; }
    public TransferResumeState(bool immutableUploadSource = false) { this.immutableUploadSource = immutableUploadSource; }
    internal IncrementalHash UploadHash(Stream source, long offset, out bool cached)
    {
        cached = false;
        if (immutableUploadSource && ReferenceEquals(source, sourceIdentity))
        {
            if (currentHash is not null && currentOffset == offset) { cached = true; source.Position = offset; return currentHash.Clone(); }
            if (previousHash is not null && previousOffset == offset) { cached = true; source.Position = offset; return previousHash.Clone(); }
        }
        return IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    }
    internal void BeforeChunk(Stream source, IncrementalHash hash, long offset)
    { if (immutableUploadSource) { sourceIdentity = source; previousHash?.Dispose(); previousHash = hash.Clone(); previousOffset = offset; } }
    internal void AfterChunk(IncrementalHash hash, long offset)
    { if (immutableUploadSource) { currentHash?.Dispose(); currentHash = hash.Clone(); currentOffset = offset; } }
    internal string? Token, Relative;
    internal bool Download;
    internal long Length;
    internal IResumableIncomingTransferFile? Sink;
    public long LastStartOffset { get; internal set; }
    public bool Available => Token is not null;
    internal void Bind(string path, bool download, long length)
    {
        if (Relative is not null && (Relative != path || Download != download || !download && Length != length)) throw new InvalidDataException("Resume job identity changed");
        if (Relative is null || !download) Length = length; Relative = path; Download = download;
    }
    public async ValueTask DisposeAsync()
    {
        Token = null; Relative = null;
        currentHash?.Dispose(); previousHash?.Dispose(); currentHash = previousHash = null; sourceIdentity = null;
        if (Interlocked.Exchange(ref Sink, null) is { } sink) await sink.DisposeAsync();
    }
    public override string ToString() => "TransferResumeState [ticket/path redacted]";
    internal static async Task HashPrefix(Stream source, long length, IncrementalHash hash, CancellationToken ct)
    {
        source.Position = 0; byte[] buffer = new byte[TransferLimits.ChunkBytes];
        for (long offset = 0; offset < length;)
        {
            int size = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), ct);
            if (size == 0) throw new TransferRejectedException("source-changed"); hash.AppendData(buffer, 0, size); offset += size;
        }
    }
    internal static bool SameHash(string expected, string actual) => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
}
