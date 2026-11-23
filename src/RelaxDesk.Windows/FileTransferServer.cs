using System.Security.Cryptography;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// One bounded data-channel operation at a time. No control/media thread does IO.
public sealed class FileTransferServer(WindowsFileRoot root, AccessGate gate)
{
    public async Task Run(Stream data, CancellationToken ct)
    {
        IncomingTransferFile? upload = null; string? uploadId = null;
        long incomingSequence = 0, outgoingSequence = 0;
        async Task Send(TransferMessage message)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await FileDataProtocol.Write(data, message with { Sequence = ++outgoingSequence }, deadline.Token);
        }
        void Authorize() { ct.ThrowIfCancellationRequested(); if (!gate.Allows(SessionCapabilities.FileTransfer)) throw new UnauthorizedAccessException("File transfer grant revoked"); }
        try
        {
            while (true)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
                var message = await FileDataProtocol.Read(data, deadline.Token); Authorize();
                if (message.Sequence != ++incomingSequence) throw new InvalidDataException("File data sequence mismatch");
                try
                {
                    switch (message.Kind)
                    {
                        case "ping": await Send(new("pong", message.Id)); break;
                        case "list" when upload is null:
                            await Send(new("entries", message.Id, Offset: message.Offset, Entries: root.List(message.Path!, checked((int)message.Offset)))); break;
                        case "mkdir" when upload is null:
                            root.CreateDirectory(message.Path!); await Send(new("result", message.Id)); break;
                        case "upload" when upload is null:
                            upload = root.CreateUpload(message.Path!, message.Length, message.Overwrite); uploadId = message.Id;
                            await Send(new("ready", message.Id, Length: message.Length)); break;
                        case "chunk" when upload is not null && message.Id == uploadId:
                            await upload.Write(message.Offset, message.Data!, ct); await Send(new("progress", message.Id, Offset: upload.ReceivedBytes, Length: upload.ExpectedBytes)); break;
                        case "complete" when upload is not null && message.Id == uploadId:
                            await upload.Commit(message.Hash!, ct); await upload.DisposeAsync(); upload = null; uploadId = null;
                            await Send(new("result", message.Id)); break;
                        case "cancel" when upload is not null && message.Id == uploadId:
                            await upload.DisposeAsync(); upload = null; uploadId = null; await Send(new("result", message.Id, Error: "cancelled")); break;
                        case "download" when upload is null:
                            await using (var source = root.OpenRead(message.Path!))
                            {
                                await Send(new("ready", message.Id, Length: source.Stream.Length));
                                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                                byte[] buffer = new byte[TransferLimits.ChunkBytes]; long offset = 0; bool cancelled = false;
                                while (true)
                                {
                                    Authorize(); int size = await source.Stream.ReadAsync(buffer, ct); if (size == 0) break;
                                    hash.AppendData(buffer, 0, size);
                                    await Send(new("chunk", message.Id, Offset: offset, Data: buffer.AsSpan(0, size).ToArray())); offset += size;
                                    while (true)
                                    {
                                        using var acknowledgementDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct); acknowledgementDeadline.CancelAfter(TimeSpan.FromSeconds(30));
                                        var acknowledgement = await FileDataProtocol.Read(data, acknowledgementDeadline.Token); Authorize();
                                        if (acknowledgement.Sequence != ++incomingSequence) throw new InvalidDataException("File acknowledgement sequence mismatch");
                                        if (acknowledgement.Kind == "ping") { await Send(new("pong", acknowledgement.Id)); continue; }
                                        if (acknowledgement.Id != message.Id) throw new InvalidDataException("File acknowledgement request mismatch");
                                        if (acknowledgement.Kind == "cancel") { cancelled = true; await Send(new("result", message.Id, Error: "cancelled")); break; }
                                        if (acknowledgement.Kind != "progress" || acknowledgement.Offset != offset) throw new InvalidDataException("File acknowledgement offset mismatch");
                                        break;
                                    }
                                    if (cancelled) break;
                                }
                                if (!cancelled) await Send(new("complete", message.Id, Length: offset, Hash: Convert.ToHexString(hash.GetHashAndReset())));
                            }
                            break;
                        default: throw new InvalidDataException("Invalid transfer state/operation");
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    if (upload is not null) { await upload.DisposeAsync(); upload = null; uploadId = null; }
                    string reason = error is TransferRejectedException rejected ? rejected.Code : error is InvalidDataException ? "invalid-data" : error is UnauthorizedAccessException ? "access-denied" : "file-io";
                    await Send(new("result", message.Id, Error: reason));
                }
            }
        }
        finally { if (upload is not null) await upload.DisposeAsync(); }
    }
}
