using System.Buffers.Binary;
using System.Text.Json;

namespace RelaxDesk.Core;

public sealed record TransferEntry(string Path, bool Directory, long Length);
public sealed record TransferMessage(string Kind, string Id, long Sequence = 0, string? Path = null, long Offset = 0,
    long Length = 0, byte[]? Data = null, string? Hash = null, bool Overwrite = false, TransferEntry[]? Entries = null, string? Error = null,
    bool Resumable = false, string? ResumeToken = null, bool Completed = false)
{
    public override string ToString() => $"TransferMessage kind={Kind} id={Id} sequence={Sequence} (payload/path redacted)";
}

public static class FileDataProtocol
{
    public const int EnvelopeLimit = 256 * 1024;
    public static void Validate(TransferMessage message)
    {
        if (message.Error is not null && message.Error is not ("exists" or "cancelled" or "invalid-data" or "access-denied" or "file-io" or "disk-full" or "busy" or "path-denied" or "resume-unavailable" or "source-changed")) throw new InvalidDataException("Unknown file error code");
        if (!Guid.TryParseExact(message.Id, "N", out _) || message.Id.Length != 32 || message.Sequence <= 0 || message.Offset < 0 || message.Length is < 0 or > TransferLimits.MaximumFileBytes ||
            message.Data is { Length: > TransferLimits.ChunkBytes } || message.Entries is { Length: > TransferLimits.ListPage } ||
            message.Hash is not null && (message.Hash.Length != 64 || !message.Hash.All(char.IsAsciiHexDigit)) || message.Error is { Length: > 64 }) throw new InvalidDataException("Invalid file data envelope");
        if (message.Path is not null) _ = TransferPaths.Relative(message.Path, message.Kind == "list");
        if (message.ResumeToken is not null && (message.ResumeToken.Length != 64 || !message.ResumeToken.All(char.IsAsciiHexDigit) || message.Kind is not ("upload" or "download" or "ready" or "forget")) ||
            message.Resumable && message.Kind is not ("upload" or "download") || message.Completed && (message.Kind != "ready" || message.ResumeToken is null) ||
            message.ResumeToken is not null && message.Kind is "upload" or "download" && !message.Resumable ||
            message.Kind == "ready" && message.ResumeToken is not null && (message.Hash is null || message.Offset > message.Length)) throw new InvalidDataException("Invalid resume envelope");
        if (message.Data is not null && message.Kind != "chunk" || message.Entries is not null && message.Kind != "entries" || message.Hash is not null && !(message.Kind == "complete" || message.Kind is "ready" or "download" && message.ResumeToken is not null) || message.Error is not null && message.Kind != "result" || message.Overwrite && message.Kind != "upload") throw new InvalidDataException("Unexpected file data fields");
        if (message.Entries is { } entries)
            foreach (var entry in entries)
                if (entry is null || TransferPaths.Relative(entry.Path).Length == 0 || entry.Length is < 0 or > TransferLimits.MaximumFileBytes) throw new InvalidDataException("Invalid directory entry");
        bool valid = message.Kind switch
        {
            "upload" => message.Path is not null && message.Data is null && message.Hash is null && message.Offset == 0,
            "download" => message.Path is not null && message.Data is null && (message.ResumeToken is null ? message.Hash is null && message.Offset == 0 : message.Hash is not null),
            "mkdir" => message.Path is not null && message.Data is null,
            "list" => message.Path is not null && message.Data is null && message.Offset <= TransferPaths.MaximumEntries,
            "chunk" => message.Data is { Length: > 0 } && message.Path is null && message.Hash is null,
            "complete" => message.Hash is not null && message.Data is null && message.Path is null,
            "committed" => message.Data is null && message.Path is null && message.Offset == 0 && message.Length == 0,
            "forget" => message.ResumeToken is not null && message.Data is null && message.Path is null && message.Offset == 0 && message.Length == 0,
            "ready" or "progress" or "result" or "cancel" or "ping" or "pong" => message.Data is null && message.Path is null,
            "entries" => message.Entries is not null && message.Data is null && message.Path is null,
            _ => false
        };
        if (!valid) throw new InvalidDataException("Invalid file data operation");
    }
    public static async Task Write(Stream stream, TransferMessage message, CancellationToken ct)
    {
        Validate(message); byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > EnvelopeLimit) throw new InvalidDataException("File data envelope too large");
        byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, ct); await stream.WriteAsync(payload, ct);
    }
    public static async Task<TransferMessage> Read(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, ct);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 2 or > EnvelopeLimit) throw new InvalidDataException("Invalid file data envelope size");
        byte[] payload = new byte[size]; await stream.ReadExactlyAsync(payload, ct);
        var message = JsonSerializer.Deserialize<TransferMessage>(payload) ?? throw new InvalidDataException("Missing file operation");
        Validate(message); return message;
    }
}
