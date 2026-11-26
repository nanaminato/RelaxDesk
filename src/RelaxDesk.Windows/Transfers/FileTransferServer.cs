using System.Security.Cryptography;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// One independently authorized operation at a time. Resumable resources remain
// inside the process-local, bounded store; a new grant is mandatory to reattach.
public sealed class FileTransferServer(WindowsFileRoot root, AccessGate gate, FileTransferResumeStore? resumes = null, Func<bool>? canRetain = null,Func<CancellationToken,Task<IFileClipboardPublisher?>>? clipboardPublisher=null, ClipboardFileSources? fileSources=null)
{
    sealed class TransportFailure(Exception cause) : IOException("File transport interrupted", cause);
    public async Task Run(Stream data, CancellationToken ct)
    {
        IncomingTransferFile? upload = null; FileTransferResumeStore.Entry? retained = null; string? activeId = null;
        var clipboardStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool interrupted = false;
        long incomingSequence = 0, outgoingSequence = 0;
        async Task Send(TransferMessage message)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            try { await FileDataProtocol.Write(data, message with { Sequence = ++outgoingSequence }, deadline.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { throw new TransportFailure(error); }
        }
        void Authorize()
        {
            ct.ThrowIfCancellationRequested();
            if (gate.Allows(SessionCapabilities.FileTransfer)) return;
            ct.ThrowIfCancellationRequested();
            throw new UnauthorizedAccessException("File transfer grant revoked");
        }
        async Task<TransferMessage> Read()
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            TransferMessage message;
            try { message = await FileDataProtocol.Read(data, deadline.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { throw new TransportFailure(error); }
            Authorize();
            if (message.Sequence != ++incomingSequence) throw new InvalidDataException("File data sequence mismatch");
            return message;
        }
        async Task Discard()
        {
            if (retained is { } entry) { retained = null; upload = null; await resumes!.Remove(entry); }
            else if (upload is { } partial) { upload = null; await partial.DisposeAsync(); }
            activeId = null;
        }
        async Task<TransferMessage> Acknowledgement(string id)
        {
            while (true)
            {
                var acknowledgement = await Read();
                if (acknowledgement.Kind == "ping") { await Send(new("pong", acknowledgement.Id)); continue; }
                if (acknowledgement.Id != id) throw new InvalidDataException("File acknowledgement request mismatch");
                return acknowledgement;
            }
        }
        bool IsClipboardSource(string path) => fileSources is not null && ClipboardFileSources.IsOfferPath(path);
        void AuthorizeClipboardSource()
        {
            if (!gate.Allows(SessionCapabilities.ClipboardFiles))
                throw new UnauthorizedAccessException("File clipboard grant absent");
        }
        void AuthorizeClipboardStage(string path, bool createStage = false)
        {
            if (fileSources is null) return;
            AuthorizeClipboardSource();
            int separator = path.IndexOf('/');
            string stage = separator < 0 ? path : path[..separator];
            if (clipboardStages.Contains(stage)) return;
            const string prefix = "RelaxDesk-Clipboard-";
            if (!createStage || separator >= 0 || clipboardStages.Count >= TransferPaths.MaximumEntries ||
                !stage.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(stage[prefix.Length..], "N", out _))
                throw new UnauthorizedAccessException("Path is outside this session's clipboard staging area");
            root.CreateDirectory(stage, requireNew: true);
            clipboardStages.Add(stage);
        }
        async Task Download(TransferMessage request)
        {
            bool fromClipboard = IsClipboardSource(request.Path!);
            TransferReadFile? legacySource = null;
            try
            {
                if (fileSources is not null && !fromClipboard)
                    throw new UnauthorizedAccessException("Downloads require a copied file selection");
                if (fromClipboard)
                {
                    AuthorizeClipboardSource();
                    if (request.Resumable) throw new TransferRejectedException("resume-unavailable");
                    legacySource = fileSources!.OpenRead(request.Path!);
                }
                else if (request.Resumable)
                {
                    retained = request.ResumeToken is null ? await resumes!.Begin(request.Path!, 0, false, true) : await resumes!.Resume(request.ResumeToken, request.Path!, 0, false, true);
                    activeId = request.Id;
                }
                else legacySource = root.OpenRead(request.Path!);
                var source = retained?.Source ?? legacySource!; long length = source.Stream.Length;
                long offset = request.ResumeToken is null ? 0 : request.Offset;
                if (offset > length || retained is not null && offset > retained.SentBytes) throw new TransferRejectedException("resume-unavailable");
                bool cached = retained is not null && (retained.CurrentHash is not null && retained.CurrentOffset == offset || retained.PreviousHash is not null && retained.PreviousOffset == offset);
                using var hash = cached ? (retained!.CurrentOffset == offset && retained.CurrentHash is not null ? retained.CurrentHash.Clone() : retained.PreviousHash!.Clone()) : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[TransferLimits.ChunkBytes]; source.Stream.Position = cached ? offset : 0;
                for (long done = cached ? offset : 0; done < offset;)
                {
                    Authorize(); int size = await source.Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, offset - done)), ct);
                    if (size == 0) throw new TransferRejectedException("source-changed"); hash.AppendData(buffer, 0, size); done += size;
                }
                string prefix = Convert.ToHexString(hash.GetCurrentHash());
                if (request.ResumeToken is not null && !CryptographicOperations.FixedTimeEquals(Convert.FromHexString(prefix), Convert.FromHexString(request.Hash!))) throw new TransferRejectedException("source-changed");
                await Send(new("ready", request.Id, Length: length, Offset: offset, ResumeToken: retained?.Token, Hash: retained is null ? null : prefix));
                while (offset < length)
                {
                    Authorize();
                    if (fromClipboard) AuthorizeClipboardSource();
                    int size = await source.Stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - offset)), ct); if (size == 0) throw new IOException("Source size changed");
                    if (retained is not null) { retained.PreviousHash?.Dispose(); retained.PreviousHash = hash.Clone(); retained.PreviousOffset = offset; }
                    hash.AppendData(buffer, 0, size);
                    if (retained is not null) { retained.CurrentHash?.Dispose(); retained.CurrentHash = hash.Clone(); retained.CurrentOffset = offset + size; }
                    if (retained is not null) retained.SentBytes = Math.Max(retained.SentBytes, offset + size);
                    await Send(new("chunk", request.Id, Offset: offset, Data: buffer.AsSpan(0, size).ToArray())); offset += size;
                    var acknowledgement = await Acknowledgement(request.Id);
                    if (acknowledgement.Kind == "cancel") { await Discard(); await Send(new("result", request.Id, Error: "cancelled")); return; }
                    if (acknowledgement.Kind != "progress" || acknowledgement.Offset != offset) throw new InvalidDataException("File acknowledgement offset mismatch");
                }
                await Send(new("complete", request.Id, Length: offset, Hash: Convert.ToHexString(hash.GetHashAndReset())));
                if (retained is not null)
                {
                    var final = await Acknowledgement(request.Id);
                    if (final.Kind is not ("committed" or "cancel")) throw new InvalidDataException("Download commit acknowledgement missing");
                    await Discard(); if (final.Kind == "cancel") await Send(new("result", request.Id, Error: "cancelled"));
                }
            }
            finally { if (legacySource is not null) await legacySource.DisposeAsync(); }
        }
        try
        {
            while (true)
            {
                var message = await Read();
                try
                {
                    if (message.Resumable && resumes is null) throw new InvalidDataException("File resume not negotiated");
                    switch (message.Kind)
                    {
                        case "clipboard-get" or "clipboard-set" when upload is null&&retained is null:
                        {
                            if(clipboardPublisher is null||!gate.Allows(SessionCapabilities.ClipboardFiles))throw new UnauthorizedAccessException("File clipboard grant absent");
                            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(3));
                            try
                            {
                                var publisher=await clipboardPublisher(deadline.Token)??throw new UnauthorizedAccessException("Clipboard provider unavailable");
                                if(!gate.Allows(SessionCapabilities.ClipboardFiles))throw new UnauthorizedAccessException("Clipboard grant revoked");
                                if(message.Kind=="clipboard-get")await Send(new("clipboard-version",message.Id,Offset:await publisher.ClipboardVersion(deadline.Token)));
                                else{foreach(string path in message.Clipboard!.Paths)AuthorizeClipboardStage(path);_=root.ResolveClipboard(message.Clipboard);if(!await publisher.PrepareFiles(message.Clipboard,message.Offset,deadline.Token))throw new TransferRejectedException("clipboard-changed");await Send(new("result",message.Id));}
                            }
                            catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new TransferRejectedException("busy");}
                            break;
                        }
                        case "ping": await Send(new("pong", message.Id)); break;
                        case "list" when upload is null && retained is null:
                            bool offered = IsClipboardSource(message.Path!);
                            if (offered) AuthorizeClipboardSource();
                            else if (fileSources is not null) throw new UnauthorizedAccessException("Listing requires a copied file selection");
                            var entries = offered ? fileSources!.List(message.Path!, checked((int)message.Offset)) : root.List(message.Path!, checked((int)message.Offset));
                            await Send(new("entries", message.Id, Offset: message.Offset, Entries: entries)); break;
                        case "mkdir" when upload is null && retained is null:
                            bool createStage = fileSources is not null && !message.Path!.Contains('/');
                            AuthorizeClipboardStage(message.Path!, createStage);
                            if (!createStage) root.CreateDirectory(message.Path!);
                            await Send(new("result", message.Id)); break;
                        case "forget" when upload is null && retained is null && resumes is not null:
                            await resumes.Forget(message.ResumeToken!); await Send(new("result", message.Id)); break;
                        case "upload" when upload is null && retained is null:
                            AuthorizeClipboardStage(message.Path!);
                            if (message.Resumable)
                            {
                                retained = message.ResumeToken is null ? await resumes!.Begin(message.Path!, message.Length, message.Overwrite, false) : await resumes!.Resume(message.ResumeToken, message.Path!, message.Length, message.Overwrite, false);
                                upload = retained.Upload;
                            }
                            else upload = root.CreateUpload(message.Path!, message.Length, message.Overwrite);
                            activeId = message.Id;
                            await Send(new("ready", message.Id, Length: message.Length, Offset: retained?.Committed == true ? retained.Length : upload!.ReceivedBytes,
                                ResumeToken: retained?.Token, Hash: retained is null ? null : retained.Committed ? retained.FinalHash : upload!.PrefixHash, Completed: retained?.Committed == true)); break;
                        case "chunk" when upload is not null && message.Id == activeId:
                            if (fileSources is not null) AuthorizeClipboardSource();
                            await upload.Write(message.Offset, message.Data!, ct); await Send(new("progress", message.Id, Offset: upload.ReceivedBytes, Length: upload.ExpectedBytes)); break;
                        case "complete" when upload is not null && message.Id == activeId:
                            if (fileSources is not null) AuthorizeClipboardSource();
                            await upload.Commit(message.Hash!, ct); await upload.DisposeAsync(); upload = null;
                            if (retained is not null) { retained.Upload = null; retained.Committed = true; retained.FinalHash = message.Hash!; } else activeId = null;
                            await Send(new("result", message.Id)); break;
                        case "committed" when retained is { Download: false, Committed: true } && message.Id == activeId:
                            await Discard(); break;
                        case "cancel" when (upload is not null || retained is not null) && message.Id == activeId:
                            await Discard(); await Send(new("result", message.Id, Error: "cancelled")); break;
                        case "download" when upload is null && retained is null: await Download(message); break;
                        default: throw new InvalidDataException("Invalid transfer state/operation");
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // Transport IO escapes to suspension, not an attempted write
                    // of a per-file error on an already broken stream.
                    if (error is TransportFailure) throw;
                    await Discard();
                    string reason = error is TransferRejectedException rejected ? rejected.Code : error is InvalidDataException ? "invalid-data" : error is UnauthorizedAccessException ? "access-denied" : "file-io";
                    await Send(new("result", message.Id, Error: reason));
                }
            }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        { interrupted = error is not TransferRejectedException; throw; }
        finally
        {
            if (retained is { } entry) { retained = null; upload = null; await resumes!.Suspend(entry, interrupted && canRetain?.Invoke() == true); }
            else if (upload is not null) await upload.DisposeAsync();
        }
    }
}
