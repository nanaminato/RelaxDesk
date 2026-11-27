using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace RelaxDesk.Core;

public readonly record struct RegionRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public long Pixels => (long)Width * Height;
    public bool IsValid(int surfaceWidth, int surfaceHeight) =>
        Left >= 0 && Top >= 0 && Right > Left && Bottom > Top && Right <= surfaceWidth && Bottom <= surfaceHeight;
    public bool IntersectsOrTouches(RegionRect other) =>
        Left <= other.Right && Right >= other.Left && Top <= other.Bottom && Bottom >= other.Top;
    public RegionRect Union(RegionRect other) =>
        new(Math.Min(Left, other.Left), Math.Min(Top, other.Top), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
}

public readonly record struct RegionMove(int SourceX, int SourceY, RegionRect Destination)
{
    public bool IsValid(int surfaceWidth, int surfaceHeight) =>
        Destination.IsValid(surfaceWidth, surfaceHeight) &&
        SourceX >= 0 && SourceY >= 0 &&
        (long)SourceX + Destination.Width <= surfaceWidth &&
        (long)SourceY + Destination.Height <= surfaceHeight;
}

public sealed record RegionPatch(RegionRect Bounds, int UncompressedBytes, byte[] CompressedBgra, uint Sha256Prefix);

public sealed record RegionCompensationPacket(
    long DisplayGeneration,
    int VideoEpoch,
    long BaseFrameSequence,
    long RegionVersion,
    int SurfaceWidth,
    int SurfaceHeight,
    RegionMove[] Moves,
    RegionPatch[] Patches);

public enum RegionApplyOutcome
{
    Applied,
    IgnoredDisabled,
    RejectedStaleEpoch,
    RejectedBaseMismatch,
    RejectedDuplicateOrRegressedVersion,
    RejectedBoundsOrLimits,
    RejectedIntegrity
}

// In-memory Stage B prototype (03-hybrid.md), not connected to a Host/Viewer session.
// Receiver objects default to disabled; no production feature switch exists yet.
// Enforces baseFrameSequence/videoEpoch/displayGeneration binding, applies move
// rectangles before dirty patches with overlap-safe scratch copying, and
// invalidates older compensation whenever a newer video frame arrives.
public static class RegionCompensationProtocol
{
    public const int Version = 1;
    public const int DefaultTileSize = 64, AlternateTileSize = 128;
    public const int MaxRectangles = 32;
    public const int MaxUncompressedPatchBytes = 2 * 1024 * 1024;
    public const int MaxTotalUncompressedBytes = 4 * 1024 * 1024;
    public const int MaxTotalMoveBytes = 4 * 1024 * 1024;
    public const int MaxCompressedPayloadBytes = 2 * 1024 * 1024;
    const uint Magic = 0x31524452; // RDR1

    // Quantize dirty rectangles to 64 or 128-pixel tiles and merge touching/overlapping tiles
    // when the merged bounding box does not inflate pixel area beyond 25%.
    public static RegionRect[] PlanTiles(ReadOnlySpan<RegionRect> dirty, int surfaceWidth, int surfaceHeight, int tileSize = DefaultTileSize)
    {
        if (tileSize is not (DefaultTileSize or AlternateTileSize))
            throw new ArgumentOutOfRangeException(nameof(tileSize));
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || (long)surfaceWidth * surfaceHeight > 8_294_400)
            throw new ArgumentOutOfRangeException(nameof(surfaceWidth));
        var tiles = new List<RegionRect>();
        foreach (var raw in dirty)
        {
            int left = Math.Clamp(raw.Left, 0, surfaceWidth) / tileSize * tileSize;
            int top = Math.Clamp(raw.Top, 0, surfaceHeight) / tileSize * tileSize;
            int right = Math.Min(surfaceWidth, (Math.Clamp(raw.Right, 0, surfaceWidth) + tileSize - 1) / tileSize * tileSize);
            int bottom = Math.Min(surfaceHeight, (Math.Clamp(raw.Bottom, 0, surfaceHeight) + tileSize - 1) / tileSize * tileSize);
            if (right <= left || bottom <= top) continue;
            var candidate = new RegionRect(left, top, right, bottom);
            if (!tiles.Contains(candidate)) tiles.Add(candidate);
        }
        bool mergedAny = true;
        while (mergedAny && tiles.Count > 1)
        {
            mergedAny = false;
            for (int i = 0; i < tiles.Count && !mergedAny; i++)
            {
                for (int j = i + 1; j < tiles.Count; j++)
                {
                    if (!tiles[i].IntersectsOrTouches(tiles[j])) continue;
                    var union = tiles[i].Union(tiles[j]);
                    if (union.Pixels <= (tiles[i].Pixels + tiles[j].Pixels) * 5 / 4)
                    {
                        tiles[i] = union;
                        tiles.RemoveAt(j);
                        mergedAny = true;
                        break;
                    }
                }
            }
        }
        if (tiles.Count > MaxRectangles)
        {
            var bounding = tiles[0];
            for (int i = 1; i < tiles.Count; i++) bounding = bounding.Union(tiles[i]);
            return [bounding];
        }
        return tiles.ToArray();
    }

    public static RegionPatch CreatePatch(ReadOnlySpan<byte> fullBgra, int surfaceWidth, int surfaceHeight, RegionRect bounds)
    {
        if (!bounds.IsValid(surfaceWidth, surfaceHeight) || fullBgra.Length != checked(surfaceWidth * surfaceHeight * 4))
            throw new InvalidDataException("Invalid region patch source bounds");
        int rawBytes = checked((int)(bounds.Pixels * 4));
        if (rawBytes is <= 0 or > MaxUncompressedPatchBytes)
            throw new InvalidDataException("Region patch exceeds uncompressed cap");
        byte[] raw = ArrayPool<byte>.Shared.Rent(rawBytes);
        try
        {
            int rowBytes = bounds.Width * 4;
            for (int y = 0; y < bounds.Height; y++)
            {
                int srcOffset = ((bounds.Top + y) * surfaceWidth + bounds.Left) * 4;
                fullBgra.Slice(srcOffset, rowBytes).CopyTo(raw.AsSpan(y * rowBytes, rowBytes));
            }
            var slice = raw.AsSpan(0, rawBytes);
            uint prefix = DigestPrefix(slice);
            using var output = new MemoryStream();
            using (var compressor = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(slice);
            byte[] compressed = output.ToArray();
            if (compressed.Length is <= 0 or > MaxCompressedPayloadBytes)
                throw new InvalidDataException("Compressed region patch exceeds limit");
            return new(bounds, rawBytes, compressed, prefix);
        }
        finally { ArrayPool<byte>.Shared.Return(raw); }
    }

    public static byte[] Encode(RegionCompensationPacket packet)
    {
        ValidatePacket(packet);
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], packet.DisplayGeneration);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], packet.VideoEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(header[20..], packet.BaseFrameSequence);
        BinaryPrimitives.WriteInt64LittleEndian(header[28..], packet.RegionVersion);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], packet.SurfaceWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header[40..], packet.SurfaceHeight);
        BinaryPrimitives.WriteInt32LittleEndian(header[44..], packet.Moves.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[48..], packet.Patches.Length);
        stream.Write(header);

        Span<byte> moveBuf = stackalloc byte[24];
        foreach (var move in packet.Moves)
        {
            BinaryPrimitives.WriteInt32LittleEndian(moveBuf, move.SourceX);
            BinaryPrimitives.WriteInt32LittleEndian(moveBuf[4..], move.SourceY);
            BinaryPrimitives.WriteInt32LittleEndian(moveBuf[8..], move.Destination.Left);
            BinaryPrimitives.WriteInt32LittleEndian(moveBuf[12..], move.Destination.Top);
            BinaryPrimitives.WriteInt32LittleEndian(moveBuf[16..], move.Destination.Right);
            BinaryPrimitives.WriteInt32LittleEndian(moveBuf[20..], move.Destination.Bottom);
            stream.Write(moveBuf);
        }

        Span<byte> patchBuf = stackalloc byte[28];
        foreach (var patch in packet.Patches)
        {
            BinaryPrimitives.WriteInt32LittleEndian(patchBuf, patch.Bounds.Left);
            BinaryPrimitives.WriteInt32LittleEndian(patchBuf[4..], patch.Bounds.Top);
            BinaryPrimitives.WriteInt32LittleEndian(patchBuf[8..], patch.Bounds.Right);
            BinaryPrimitives.WriteInt32LittleEndian(patchBuf[12..], patch.Bounds.Bottom);
            BinaryPrimitives.WriteInt32LittleEndian(patchBuf[16..], patch.UncompressedBytes);
            BinaryPrimitives.WriteInt32LittleEndian(patchBuf[20..], patch.CompressedBgra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(patchBuf[24..], patch.Sha256Prefix);
            stream.Write(patchBuf);
            stream.Write(patch.CompressedBgra);
        }
        return stream.ToArray();
    }

    public static RegionCompensationPacket Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 52 || bytes.Length > MaxCompressedPayloadBytes + 4096 ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[4..]) != Version)
            throw new InvalidDataException("Invalid region compensation header");
        long displayGen = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        int videoEpoch = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]);
        long baseFrame = BinaryPrimitives.ReadInt64LittleEndian(bytes[20..]);
        long regionVer = BinaryPrimitives.ReadInt64LittleEndian(bytes[28..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes[36..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes[40..]);
        int moveCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[44..]);
        int patchCount = BinaryPrimitives.ReadInt32LittleEndian(bytes[48..]);
        if (moveCount is < 0 or > MaxRectangles || patchCount is < 0 or > MaxRectangles || (moveCount == 0 && patchCount == 0))
            throw new InvalidDataException("Invalid region rectangle count");
        int offset = 52;
        if (bytes.Length - offset < moveCount * 24) throw new InvalidDataException("Truncated move rectangles");
        var moves = new RegionMove[moveCount];
        for (int i = 0; i < moveCount; i++, offset += 24)
        {
            var slice = bytes.Slice(offset, 24);
            moves[i] = new(
                BinaryPrimitives.ReadInt32LittleEndian(slice),
                BinaryPrimitives.ReadInt32LittleEndian(slice[4..]),
                new(BinaryPrimitives.ReadInt32LittleEndian(slice[8..]),
                    BinaryPrimitives.ReadInt32LittleEndian(slice[12..]),
                    BinaryPrimitives.ReadInt32LittleEndian(slice[16..]),
                    BinaryPrimitives.ReadInt32LittleEndian(slice[20..])));
        }
        var patches = new RegionPatch[patchCount];
        for (int i = 0; i < patchCount; i++)
        {
            if (bytes.Length - offset < 28) throw new InvalidDataException("Truncated region patch header");
            var slice = bytes.Slice(offset, 28);
            var rect = new RegionRect(
                BinaryPrimitives.ReadInt32LittleEndian(slice),
                BinaryPrimitives.ReadInt32LittleEndian(slice[4..]),
                BinaryPrimitives.ReadInt32LittleEndian(slice[8..]),
                BinaryPrimitives.ReadInt32LittleEndian(slice[12..]));
            int uncompressed = BinaryPrimitives.ReadInt32LittleEndian(slice[16..]);
            int compressedLen = BinaryPrimitives.ReadInt32LittleEndian(slice[20..]);
            uint shaPrefix = BinaryPrimitives.ReadUInt32LittleEndian(slice[24..]);
            offset += 28;
            if (compressedLen is <= 0 or > MaxCompressedPayloadBytes || bytes.Length - offset < compressedLen)
                throw new InvalidDataException("Invalid compressed region length");
            patches[i] = new(rect, uncompressed, bytes.Slice(offset, compressedLen).ToArray(), shaPrefix);
            offset += compressedLen;
        }
        if (offset != bytes.Length) throw new InvalidDataException("Trailing bytes in region compensation packet");
        var packet = new RegionCompensationPacket(displayGen, videoEpoch, baseFrame, regionVer, width, height, moves, patches);
        ValidatePacket(packet);
        return packet;
    }

    static void ValidatePacket(RegionCompensationPacket packet)
    {
        if (packet.DisplayGeneration is < 0 or > 1_000_000 ||
            packet.VideoEpoch is < 0 or > 1_000_000 ||
            packet.BaseFrameSequence <= 0 ||
            packet.RegionVersion <= 0 ||
            packet.SurfaceWidth <= 0 || packet.SurfaceHeight <= 0 ||
            (long)packet.SurfaceWidth * packet.SurfaceHeight > 8_294_400 ||
            packet.Moves.Length > MaxRectangles || packet.Patches.Length > MaxRectangles ||
            (packet.Moves.Length == 0 && packet.Patches.Length == 0))
            throw new InvalidDataException("Region compensation metadata out of bounds");
        long totalMoveBytes = 0;
        foreach (var move in packet.Moves)
        {
            if (!move.IsValid(packet.SurfaceWidth, packet.SurfaceHeight))
                throw new InvalidDataException("Invalid move rectangle in compensation");
            totalMoveBytes += move.Destination.Pixels * 4;
        }
        if (totalMoveBytes > MaxTotalMoveBytes)
            throw new InvalidDataException("Region move staging budget exceeded");
        long totalUncompressed = 0, totalCompressed = 0;
        foreach (var patch in packet.Patches)
        {
            if (!patch.Bounds.IsValid(packet.SurfaceWidth, packet.SurfaceHeight) ||
                patch.UncompressedBytes != patch.Bounds.Pixels * 4 ||
                patch.UncompressedBytes is <= 0 or > MaxUncompressedPatchBytes ||
                patch.CompressedBgra.Length is <= 0 or > MaxCompressedPayloadBytes)
                throw new InvalidDataException("Invalid dirty patch bounds or length");
            totalUncompressed += patch.UncompressedBytes;
            totalCompressed += patch.CompressedBgra.Length;
        }
        if (totalUncompressed > MaxTotalUncompressedBytes || totalCompressed > MaxCompressedPayloadBytes)
            throw new InvalidDataException("Region compensation total budget exceeded");
    }

    static uint DigestPrefix(ReadOnlySpan<byte> data)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(data, hash);
        return BinaryPrimitives.ReadUInt32LittleEndian(hash);
    }

    public sealed class ReceiverState(Action requestFullRefresh, bool enabled = false)
    {
        long currentDisplayGeneration;
        int currentVideoEpoch;
        long currentBaseFrame;
        long lastRegionVersion;
        int surfaceWidth, surfaceHeight;
        public bool Enabled { get; set; } = enabled;
        public long BaseFrameSequence => currentBaseFrame;
        public long LastRegionVersion => lastRegionVersion;
        public long AppliedPackets { get; private set; }
        public long RejectedPackets { get; private set; }

        // Every new decoded video frame establishes a fresh base image and invalidates older region versions.
        public void OnVideoFrameDecoded(long sequence, long displayGeneration, int videoEpoch, int width, int height)
        {
            currentBaseFrame = sequence;
            currentDisplayGeneration = displayGeneration;
            currentVideoEpoch = videoEpoch;
            surfaceWidth = width;
            surfaceHeight = height;
            lastRegionVersion = 0;
        }

        public RegionApplyOutcome TryApply(RegionCompensationPacket packet, Span<byte> baseBgra)
        {
            if (!Enabled) return RegionApplyOutcome.IgnoredDisabled;
            try { ValidatePacket(packet); }
            catch (InvalidDataException)
            {
                RejectedPackets++;
                return RegionApplyOutcome.RejectedBoundsOrLimits;
            }
            if (packet.DisplayGeneration != currentDisplayGeneration || packet.VideoEpoch != currentVideoEpoch ||
                packet.SurfaceWidth != surfaceWidth || packet.SurfaceHeight != surfaceHeight ||
                baseBgra.Length != checked(surfaceWidth * surfaceHeight * 4))
            {
                RejectedPackets++;
                return RegionApplyOutcome.RejectedStaleEpoch;
            }
            if (packet.BaseFrameSequence != currentBaseFrame || currentBaseFrame <= 0)
            {
                RejectedPackets++;
                if (packet.BaseFrameSequence > currentBaseFrame) requestFullRefresh();
                return RegionApplyOutcome.RejectedBaseMismatch;
            }
            if (packet.RegionVersion <= lastRegionVersion)
            {
                RejectedPackets++;
                return RegionApplyOutcome.RejectedDuplicateOrRegressedVersion;
            }

            // Verify all patches and decompress into bounded buffers before mutating baseBgra.
            var decodedPatches = new byte[packet.Patches.Length][];
            for (int i = 0; i < packet.Patches.Length; i++)
            {
                var patch = packet.Patches[i];
                byte[] pixels = new byte[patch.UncompressedBytes];
                using var decoder = new BrotliDecoder();
                var status = decoder.Decompress(patch.CompressedBgra, pixels, out int consumed, out int written);
                if (status != OperationStatus.Done || consumed != patch.CompressedBgra.Length ||
                    written != pixels.Length || DigestPrefix(pixels) != patch.Sha256Prefix)
                {
                    RejectedPackets++;
                    requestFullRefresh();
                    return RegionApplyOutcome.RejectedIntegrity;
                }
                decodedPatches[i] = pixels;
            }

            // Apply moves first using an overlap-safe staging snapshot of all move sources.
            if (packet.Moves.Length > 0)
            {
                var stagedMoves = new byte[packet.Moves.Length][];
                for (int i = 0; i < packet.Moves.Length; i++)
                {
                    var move = packet.Moves[i];
                    int rowBytes = move.Destination.Width * 4;
                    byte[] copy = new byte[rowBytes * move.Destination.Height];
                    for (int y = 0; y < move.Destination.Height; y++)
                    {
                        int srcOffset = ((move.SourceY + y) * surfaceWidth + move.SourceX) * 4;
                        baseBgra.Slice(srcOffset, rowBytes).CopyTo(copy.AsSpan(y * rowBytes, rowBytes));
                    }
                    stagedMoves[i] = copy;
                }
                for (int i = 0; i < packet.Moves.Length; i++)
                {
                    var dest = packet.Moves[i].Destination;
                    int rowBytes = dest.Width * 4;
                    byte[] copy = stagedMoves[i];
                    for (int y = 0; y < dest.Height; y++)
                    {
                        int dstOffset = ((dest.Top + y) * surfaceWidth + dest.Left) * 4;
                        copy.AsSpan(y * rowBytes, rowBytes).CopyTo(baseBgra.Slice(dstOffset, rowBytes));
                    }
                }
            }

            // Apply dirty patches after moves.
            for (int i = 0; i < packet.Patches.Length; i++)
            {
                var bounds = packet.Patches[i].Bounds;
                int rowBytes = bounds.Width * 4;
                byte[] pixels = decodedPatches[i];
                for (int y = 0; y < bounds.Height; y++)
                {
                    int dstOffset = ((bounds.Top + y) * surfaceWidth + bounds.Left) * 4;
                    pixels.AsSpan(y * rowBytes, rowBytes).CopyTo(baseBgra.Slice(dstOffset, rowBytes));
                }
            }

            lastRegionVersion = packet.RegionVersion;
            AppliedPackets++;
            return RegionApplyOutcome.Applied;
        }
    }
}
