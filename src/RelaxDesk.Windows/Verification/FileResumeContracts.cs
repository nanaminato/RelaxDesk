using System.Net;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class FileResumeContracts
{
    sealed class Clock : TimeProvider
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
    sealed class Link : IAsyncDisposable
    {
        public readonly TcpClient Client, Server;
        public readonly FileTransferClient Files;
        readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));
        readonly Task worker;
        public Exception? Error;
        public Link(TcpClient client, TcpClient server, WindowsFileRoot root, FileTransferResumeStore store, string? dropKind = null)
        {
            Client = client; Server = server; Client.NoDelay = Server.NoDelay = true;
            var gate = new AccessGate(); gate.Approve([SessionCapabilities.FileTransfer]);
            worker = Task.Run(async () => { try { await new FileTransferServer(root, gate, store, () => true).Run(server.GetStream(), stop.Token); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { Error = e; } finally { Server.Dispose(); } });
            Files = new(dropKind is null ? client.GetStream() : new DropEnvelope(client.GetStream(), dropKind), stop.Token, supportsResume: true);
        }
        public static async Task<Link> Open(WindowsFileRoot root, FileTransferResumeStore store, string? dropKind = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try { var client = new TcpClient(); await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint); var server = await listener.AcceptTcpClientAsync(); return new(client, server, root, store, dropKind); }
            finally { listener.Stop(); }
        }
        public void Cut() => Client.Close();
        public Task Ended => worker;
        public async ValueTask DisposeAsync() { stop.Cancel(); Client.Dispose(); await worker; await Files.DisposeAsync(); stop.Dispose(); }
    }
    sealed class DropEnvelope(Stream inner, string kind) : Stream
    {
        readonly byte[] header = new byte[4]; byte[]? body; int copied;
        bool writingBody;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int size = await inner.ReadAsync(buffer, ct); if (size == 0) return 0;
            if (body is null)
            {
                buffer.Span[..size].CopyTo(header.AsSpan(copied)); copied += size;
                if (copied == 4) { int length = BinaryPrimitives.ReadInt32LittleEndian(header); if (length is < 2 or > FileDataProtocol.EnvelopeLimit) throw new InvalidDataException("Test envelope invalid"); body = new byte[length]; copied = 0; }
            }
            else
            {
                buffer.Span[..size].CopyTo(body.AsSpan(copied)); copied += size;
                if (copied == body.Length)
                {
                    using var json = JsonDocument.Parse(body); bool cut = json.RootElement.GetProperty("Kind").GetString() == kind; body = null; copied = 0;
                    if (cut) { inner.Dispose(); throw new IOException("Injected lost final envelope"); }
                }
            }
            return size;
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (kind == "chunk-write" && writingBody) { using var json = JsonDocument.Parse(buffer); if (json.RootElement.GetProperty("Kind").GetString() == "chunk") { inner.Dispose(); throw new IOException("Injected unsent chunk payload"); } }
            await inner.WriteAsync(buffer, ct); writingBody = !writingBody;
        }
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    public static async Task Run(string fixtures)
    {
        string fixture = Path.Combine(Path.GetFullPath(fixtures), "resume-" + Guid.NewGuid().ToString("N"));
        string host = Path.Combine(fixture, "host"), local = Path.Combine(fixture, "local"); Directory.CreateDirectory(host); Directory.CreateDirectory(local);
        var root = new WindowsFileRoot(host); var localRoot = new WindowsFileRoot(local); int count = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException($"Resume contract {count + 1}"); count++; }
        async Task Interrupted(Task task) { bool failed = false; try { await task; } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { failed = true; } Check(failed); }
        byte[] bytes = RandomNumberGenerator.GetBytes(4 * 1024 * 1024 + 17); string digest = Convert.ToHexString(SHA256.HashData(bytes));
        await using (var store = new FileTransferResumeStore(host))
        {
            string scope = await store.Scope();
            await using var uploadState = new TransferResumeState();
            await using (var first = await Link.Open(root, store))
            {
                bool cut = false; using var source = new MemoryStream(bytes);
                await Interrupted(first.Files.Upload("uploaded.bin", source, false, default, progress: p => { if (!cut && p.Completed >= 512 * 1024) { cut = true; first.Cut(); } }, resume: uploadState));
                await first.Ended; Check(uploadState.Available && !File.Exists(Path.Combine(host, "uploaded.bin")));
            }
            Check(Directory.GetFiles(host, ".relaxdesk-*.partial").Length == 1 && await store.Scope() == scope);
            bool pinned = false; try { Directory.Move(host, host + "-moved"); } catch (IOException) { pinned = true; }
            Check(pinned);
            await using (var second = await Link.Open(root, store))
            {
                using var source = new MemoryStream(bytes); await second.Files.Upload("uploaded.bin", source, false, default, resume: uploadState);
                _ = await second.Files.List("", 0, default); // Ensure commit acknowledgement is processed.
                Check(uploadState.LastStartOffset >= 512 * 1024 && second.Files.UploadedBytes == bytes.LongLength - uploadState.LastStartOffset);
                Check(!uploadState.Available && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(host, "uploaded.bin")))) == digest);
            }
            Check(!Directory.GetFiles(host, ".relaxdesk-*.partial").Any());
            await using var downloadState = new TransferResumeState();
            await using (var first = await Link.Open(root, store))
            {
                bool cut = false;
                await Interrupted(first.Files.Download("uploaded.bin", size => localRoot.CreateUpload("downloaded.bin", size, false), default, progress: p => { if (!cut && p.Completed >= 512 * 1024) { cut = true; first.Cut(); } }, resume: downloadState));
                await first.Ended; Check(downloadState.Available && !File.Exists(Path.Combine(local, "downloaded.bin")));
            }
            Check(Directory.GetFiles(local, ".relaxdesk-*.partial").Length == 1);
            bool locked = false; try { using var writer = File.OpenWrite(Path.Combine(host, "uploaded.bin")); } catch (IOException) { locked = true; }
            Check(locked);
            await using (var second = await Link.Open(root, store))
            {
                await second.Files.Download("uploaded.bin", size => localRoot.CreateUpload("downloaded.bin", size, false), default, resume: downloadState);
                _ = await second.Files.List("", 0, default);
                Check(downloadState.LastStartOffset >= 512 * 1024 && second.Files.DownloadedBytes == bytes.LongLength - downloadState.LastStartOffset);
                Check(!downloadState.Available && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(local, "downloaded.bin")))) == digest);
            }
            Check(!Directory.GetFiles(local, ".relaxdesk-*.partial").Any());

            // A different source prefix cannot append to somebody else's partial.
            await using var changed = new TransferResumeState();
            await using (var first = await Link.Open(root, store))
            {
                bool cut = false; using var source = new MemoryStream(bytes);
                await Interrupted(first.Files.Upload("changed.bin", source, false, default, progress: p => { if (!cut && p.Completed >= 512 * 1024) { cut = true; first.Cut(); } }, resume: changed)); await first.Ended;
            }
            await using (var next = await Link.Open(root, store))
            {
                byte[] altered = bytes.ToArray(); altered[0] ^= 0xFF; using var source = new MemoryStream(altered); bool rejected = false;
                try { await next.Files.Upload("changed.bin", source, false, default, resume: changed); } catch (TransferRejectedException e) when (e.Code == "source-changed") { rejected = true; }
                Check(rejected && !changed.Available && !File.Exists(Path.Combine(host, "changed.bin")));
            }
            Check(!Directory.GetFiles(host, ".relaxdesk-*.partial").Any());

            // Explicit cancellation still removes the partial and frees both scopes.
            await using var cancelled = new TransferResumeState(); using var cancel = new CancellationTokenSource();
            await using (var link = await Link.Open(root, store))
            {
                using var source = new MemoryStream(bytes);
                await Interrupted(link.Files.Upload("cancelled.bin", source, false, cancel.Token, progress: p => { if (p.Completed >= 512 * 1024) cancel.Cancel(); }, resume: cancelled));
                _ = await link.Files.List("", 0, default); Check(!cancelled.Available && !Directory.GetFiles(host, ".relaxdesk-*.partial").Any());
            }
            await using var forgotten = new TransferResumeState();
            await using (var link = await Link.Open(root, store))
            {
                using var source = new MemoryStream(bytes); bool cut = false;
                await Interrupted(link.Files.Upload("forgotten.bin", source, false, default, progress: p => { if (!cut && p.Completed >= 512 * 1024) { cut = true; link.Cut(); } }, resume: forgotten)); await link.Ended;
            }
            await using (var link = await Link.Open(root, store))
            { Check(await link.Files.Forget(forgotten, default)); Check(!forgotten.Available && !Directory.GetFiles(host, ".relaxdesk-*.partial").Any()); }

            await using var receiptState = new TransferResumeState();
            await using (var link = await Link.Open(root, store, "result"))
            { using var source = new MemoryStream(bytes); await Interrupted(link.Files.Upload("lost-result.bin", source, false, default, resume: receiptState)); await link.Ended; }
            Check(File.Exists(Path.Combine(host, "lost-result.bin")) && receiptState.Available);
            File.WriteAllBytes(Path.Combine(host, "lost-result.bin"), [9]);
            await using (var link = await Link.Open(root, store))
            { using var source = new MemoryStream(bytes); await link.Files.Upload("lost-result.bin", source, false, default, resume: receiptState); _ = await link.Files.List("", 0, default); Check(link.Files.UploadedBytes == 0 && File.ReadAllBytes(Path.Combine(host, "lost-result.bin")).SequenceEqual(new byte[] { 9 })); }
            await using var finalState = new TransferResumeState();
            await using (var link = await Link.Open(root, store, "complete"))
            { await Interrupted(link.Files.Download("uploaded.bin", size => localRoot.CreateUpload("lost-complete.bin", size, false), default, resume: finalState)); await link.Ended; }
            await using (var link = await Link.Open(root, store))
            { await link.Files.Download("uploaded.bin", size => localRoot.CreateUpload("lost-complete.bin", size, false), default, resume: finalState); _ = await link.Files.List("", 0, default); Check(link.Files.DownloadedBytes == 0 && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(local, "lost-complete.bin")))) == digest); }
            foreach (string failure in new[] { "progress", "chunk-write" })
            {
                await using var cached = new TransferResumeState(immutableUploadSource: true); await using var immutable = localRoot.OpenRead("downloaded.bin");
                await using (var first = await Link.Open(root, store, failure)) { await Interrupted(first.Files.Upload("cached-" + failure + ".bin", immutable.Stream, false, default, resume: cached)); await first.Ended; }
                await using (var next = await Link.Open(root, store)) { await next.Files.Upload("cached-" + failure + ".bin", immutable.Stream, false, default, resume: cached); _ = await next.Files.List("", 0, default); Check(cached.LastPrefixReadBytes == 0 && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(host, "cached-" + failure + ".bin")))) == digest); }
            }
            await using var droppedChunk = new TransferResumeState();
            await using (var first = await Link.Open(root, store, "chunk")) { await Interrupted(first.Files.Download("uploaded.bin", size => localRoot.CreateUpload("dropped-chunk.bin", size, false), default, resume: droppedChunk)); await first.Ended; }
            await using (var next = await Link.Open(root, store)) { await next.Files.Download("uploaded.bin", size => localRoot.CreateUpload("dropped-chunk.bin", size, false), default, resume: droppedChunk); _ = await next.Files.List("", 0, default); Check(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(local, "dropped-chunk.bin")))) == digest); }
        }
        var clock = new Clock();
        await using (var store = new FileTransferResumeStore(host, clock))
        {
            string scope = await store.Scope(); var entry = await store.Begin("ticket.bin", 3, false, false); await entry.Upload!.Write(0, [1, 2], default); await store.Suspend(entry, true);
            string token = entry.Token; var resumed = await store.Resume(token, "ticket.bin", 3, false, false);
            Check(resumed.Token != token && resumed.Upload!.ReceivedBytes == 2); await store.Suspend(resumed, true);
            bool replay = false; try { await store.Resume(token, "ticket.bin", 3, false, false); } catch (TransferRejectedException e) when (e.Code == "resume-unavailable") { replay = true; }
            Check(replay);
            bool mismatch = false; try { await store.Resume(resumed.Token, "different.bin", 3, false, false); } catch (TransferRejectedException) { mismatch = true; }
            Check(mismatch);
            bool changedCase=false;try{await store.Resume(resumed.Token,"Ticket.bin",3,false,false);}catch(TransferRejectedException){changedCase=true;}Check(changedCase);
            clock.Advance(FileTransferResumeStore.Retention + TimeSpan.FromSeconds(1)); _ = await store.Scope();
            bool expired = false; try { await store.Resume(resumed.Token, "ticket.bin", 3, false, false); } catch (TransferRejectedException) { expired = true; }
            Check(expired && !Directory.GetFiles(host, ".relaxdesk-*.partial").Any());

            for (int i = 0; i < FileTransferResumeStore.MaximumEntries; i++) { var item = await store.Begin($"bounded-{i}.bin", 1, false, false); await store.Suspend(item, true); }
            bool bounded = false; try { await store.Begin("overflow.bin", 1, false, false); } catch (TransferRejectedException e) when (e.Code == "busy") { bounded = true; }
            Check(bounded);
            await store.Clear(); Check(!Directory.GetFiles(host, ".relaxdesk-*.partial").Any() && await store.Scope() != scope);

            // Lost upload result: a receipt reports the already committed result,
            // without replacing a destination that the local owner later changed.
            var receipt = await store.Begin("receipt.bin", 2, false, false); await receipt.Upload!.Write(0, [3, 4], default);
            string hash = Convert.ToHexString(SHA256.HashData(new byte[] { 3, 4 })); await receipt.Upload.Commit(hash, default); await receipt.Upload.DisposeAsync(); receipt.Upload = null; receipt.Committed = true; receipt.FinalHash = hash;
            await store.Suspend(receipt, true); File.WriteAllBytes(Path.Combine(host, "receipt.bin"), [9]);
            var known = await store.Resume(receipt.Token, "receipt.bin", 2, false, false); Check(known.Committed && known.FinalHash == hash && File.ReadAllBytes(Path.Combine(host, "receipt.bin")).SequenceEqual(new byte[] { 9 })); await store.Remove(known);
        }
        // A new directory object at the same path invalidates the old scope.
        string replace = Path.Combine(fixture, "replace"); Directory.CreateDirectory(replace);
        await using (var store = new FileTransferResumeStore(replace))
        {
            string old = await store.Scope(); var selected = new WindowsFileRoot(replace); Directory.Move(replace, replace + "-old"); Directory.CreateDirectory(replace);
            Check(!selected.SameIdentity() && await store.Scope() != old);
            bool rejected = false; try { selected.CreateUpload("wrong.bin", 1, false); } catch (IOException) { rejected = true; }
            Check(rejected && !Directory.EnumerateFileSystemEntries(replace).Any());
        }
        Check(!Directory.EnumerateFiles(fixture, ".relaxdesk-*.partial", SearchOption.AllDirectories).Any());
        Console.WriteLine($"PASS {count} actual file-resume contracts: real TCP cut and byte-saving upload/download, hashes, locked immutable source, changed prefix rejection, cancel cleanup, single-use rotated tickets, deterministic expiry, bounded cache, committed receipt, root-object replacement; generated fixtures={fixture}; no TLS/physical LAN claim");
    }
}
