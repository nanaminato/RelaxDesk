using System.Buffers.Binary;
using System.IO.Compression;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

public enum RemotePointerType : uint
{
    None = 0,
    Monochrome = 1,
    Color = 2,
    HostManaged = 3,
    MaskedColor = 4
}

public sealed record RemotePointerState(
    bool Visible,
    int X,
    int Y,
    RemotePointerType Type,
    int Width,
    int Height,
    int Pitch,
    int HotspotX,
    int HotspotY,
    ReadOnlyMemory<byte> Shape,
    long ShapeVersion)
{
    public bool UsesHostPointer => Type == RemotePointerType.HostManaged || !Shape.IsEmpty;
}

/// <summary>
/// Platform-neutral parser for RelaxDesk CaptureMetadata binary payloads
/// (v1 raw and v2 Brotli-compressed pointer shapes), reusing cached shapes
/// to avoid per-frame allocations when the cursor bitmap has not changed.
/// </summary>
public sealed class PointerMetadataParser
{
    const int HeaderSize = 64;
    byte[] previousCompressed = [];
    byte[] previousRaw = [];
    long shapeVersion;

    public bool TryParse(ReadOnlySpan<byte> metadata, out RemotePointerState? state)
    {
        state = null;
        if (metadata.Length < HeaderSize || metadata.Length > MediaProtocol.MetadataLimit)
            return false;

        int version = BinaryPrimitives.ReadInt32LittleEndian(metadata);
        int dirtyLen = BinaryPrimitives.ReadInt32LittleEndian(metadata[4..]);
        int moveLen = BinaryPrimitives.ReadInt32LittleEndian(metadata[8..]);
        int pointerLen = BinaryPrimitives.ReadInt32LittleEndian(metadata[12..]);
        int pointerX = BinaryPrimitives.ReadInt32LittleEndian(metadata[16..]);
        int pointerY = BinaryPrimitives.ReadInt32LittleEndian(metadata[20..]);
        int visible = BinaryPrimitives.ReadInt32LittleEndian(metadata[24..]);
        uint pointerType = BinaryPrimitives.ReadUInt32LittleEndian(metadata[28..]);
        int width = BinaryPrimitives.ReadInt32LittleEndian(metadata[32..]);
        int height = BinaryPrimitives.ReadInt32LittleEndian(metadata[36..]);
        int pitch = BinaryPrimitives.ReadInt32LittleEndian(metadata[40..]);
        int hotspotX = BinaryPrimitives.ReadInt32LittleEndian(metadata[44..]);
        int hotspotY = BinaryPrimitives.ReadInt32LittleEndian(metadata[48..]);

        if (version is not (1 or 2) || dirtyLen < 0 || moveLen < 0 || pointerLen < 0 ||
            dirtyLen % 16 != 0 || moveLen % 24 != 0 ||
            (long)HeaderSize + dirtyLen + moveLen + pointerLen != metadata.Length ||
            visible is < 0 or > 1 || pointerType > 4 ||
            width is < 0 or > 4096 || height is < 0 or > 8192 || pitch is < 0 or > 32768 ||
            (pointerType == (uint)RemotePointerType.HostManaged && (pointerLen != 0 || visible != 0)))
            return false;

        int pointerOffset = HeaderSize + dirtyLen + moveLen;
        ReadOnlySpan<byte> pointerSpan = metadata.Slice(pointerOffset, pointerLen);
        ReadOnlyMemory<byte> shapeMemory = ReadOnlyMemory<byte>.Empty;

        if (pointerLen > 0)
        {
            if (version == 2)
            {
                if (pointerSpan.Length <= 4) return false;
                int uncompressedSize = BinaryPrimitives.ReadInt32LittleEndian(pointerSpan);
                if (uncompressedSize <= pointerSpan.Length || uncompressedSize > MediaProtocol.MetadataLimit - pointerOffset)
                    return false;

                if (!pointerSpan.SequenceEqual(previousCompressed))
                {
                    byte[] expanded = new byte[uncompressedSize];
                    if (!BrotliDecoder.TryDecompress(pointerSpan[4..], expanded, out int written) || written != uncompressedSize)
                        return false;
                    previousCompressed = pointerSpan.ToArray();
                    previousRaw = expanded;
                    shapeVersion++;
                }
                shapeMemory = previousRaw;
            }
            else
            {
                if (!pointerSpan.SequenceEqual(previousRaw))
                {
                    previousCompressed = [];
                    previousRaw = pointerSpan.ToArray();
                    shapeVersion++;
                }
                shapeMemory = previousRaw;
            }
        }

        state = new RemotePointerState(
            Visible: visible != 0,
            X: pointerX,
            Y: pointerY,
            Type: (RemotePointerType)pointerType,
            Width: width,
            Height: height,
            Pitch: pitch,
            HotspotX: hotspotX,
            HotspotY: hotspotY,
            Shape: shapeMemory,
            ShapeVersion: shapeVersion);
        return true;
    }
}
