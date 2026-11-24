using System.Buffers.Binary;
using System.Buffers;
using System.IO.Compression;
using RelaxDesk.Core;
namespace RelaxDesk.Windows;

public static class CaptureMetadataCodec
{
    const int Header = 64;
    public sealed class PointerEncoder
    {
        byte[] previous = [];
        byte[]? compressed;

        public byte[] Encode(CaptureMetadata metadata)
        {
            if (!metadata.PointerShape.AsSpan().SequenceEqual(previous))
            {
                previous = metadata.PointerShape;
                compressed = null;
                if (previous.Length is >= 64 and <= MediaProtocol.MetadataLimit)
                {
                    byte[] scratch = ArrayPool<byte>.Shared.Rent(previous.Length);
                    try
                    {
                        if (BrotliEncoder.TryCompress(previous, scratch.AsSpan(4, previous.Length - 5), out int bytes, quality: 1, window: 10))
                        {
                            BinaryPrimitives.WriteInt32LittleEndian(scratch, previous.Length);
                            compressed = scratch.AsSpan(0, bytes + 4).ToArray();
                        }
                    }
                    finally { ArrayPool<byte>.Shared.Return(scratch); }
                }
            }
            return CaptureMetadataCodec.Encode(metadata, compressed);
        }
    }

    public static byte[] Encode(CaptureMetadata m) => Encode(m, null);

    public static byte[] ForNativeRenderer(byte[] metadata)
    {
        if (metadata.Length < Header || BinaryPrimitives.ReadInt32LittleEndian(metadata) != 2) return metadata;
        Span<int> fields = stackalloc int[14];
        ReadFields(metadata, fields);
        int pointerOffset = Header + fields[1] + fields[2];
        var pointer = metadata.AsSpan(pointerOffset);
        int size = PointerLength(pointer, pointerOffset);
        byte[] expanded = new byte[pointerOffset + size];
        metadata.AsSpan(0, pointerOffset).CopyTo(expanded);
        BinaryPrimitives.WriteInt32LittleEndian(expanded, 1);
        BinaryPrimitives.WriteInt32LittleEndian(expanded.AsSpan(12), size);
        ExpandPointer(pointer, expanded.AsSpan(pointerOffset));
        return expanded;
    }

    static byte[] Encode(CaptureMetadata m, byte[]? compressed)
    {
        int pointerOffset = checked(Header + m.DirtyRectangles.Length + m.MoveRectangles.Length);
        if ((long)pointerOffset + m.PointerShape.Length > MediaProtocol.MetadataLimit)
            throw new InvalidDataException("Capture metadata cap exceeded");
        byte[] pointer = compressed ?? m.PointerShape;
        int size = pointerOffset + pointer.Length;
        byte[] data = new byte[size];
        ReadOnlySpan<int> fields = [compressed is null ? 1 : 2, m.DirtyRectangles.Length, m.MoveRectangles.Length, pointer.Length, m.PointerX, m.PointerY,
            m.PointerVisible ? 1 : 0, (int)m.PointerType, (int)m.PointerWidth, (int)m.PointerHeight, (int)m.PointerPitch, m.HotspotX, m.HotspotY, (int)m.Accumulated];
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), fields[i]);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(56), m.Timestamp);
        m.DirtyRectangles.CopyTo(data, Header); m.MoveRectangles.CopyTo(data, Header + m.DirtyRectangles.Length);
        pointer.CopyTo(data, Header + m.DirtyRectangles.Length + m.MoveRectangles.Length); return data;
    }
    public static CaptureMetadata Decode(byte[] data)
    {
        Span<int> f = stackalloc int[14];
        ReadFields(data, f);
        int pointerOffset = Header + f[1] + f[2];
        var pointer = data.AsSpan(pointerOffset);
        byte[] shape = f[0] == 2 ? new byte[PointerLength(pointer, pointerOffset)] : pointer.ToArray();
        if (f[0] == 2) ExpandPointer(pointer, shape);
        return new(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56)), unchecked((uint)f[13]), data[Header..(Header + f[1])],
            data[(Header + f[1])..pointerOffset], f[4], f[5], f[6] != 0, (uint)f[7], (uint)f[8], (uint)f[9], (uint)f[10], f[11], f[12], shape);
    }

    static void ReadFields(byte[] data, Span<int> f)
    {
        if (data.Length < Header || data.Length > MediaProtocol.MetadataLimit) throw new InvalidDataException("Invalid capture metadata");
        for (int i = 0; i < f.Length; i++) f[i] = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4));
        if (f[0] is not (1 or 2) || f[1] < 0 || f[2] < 0 || f[3] < 0 || f[1] % 16 != 0 || f[2] % 24 != 0 ||
            (long)Header + f[1] + f[2] + f[3] != data.Length || f[6] is < 0 or > 1 || f[7] is < 0 or > 4 ||
            f[8] is < 0 or > 4096 || f[9] is < 0 or > 8192 || f[10] is < 0 or > 32768)
            throw new InvalidDataException("Invalid capture metadata fields");
    }

    static int PointerLength(ReadOnlySpan<byte> pointer, int pointerOffset)
    {
        if (pointer.Length <= 4) throw new InvalidDataException("Missing compressed pointer");
        int size = BinaryPrimitives.ReadInt32LittleEndian(pointer);
        if (size <= pointer.Length || size > MediaProtocol.MetadataLimit - pointerOffset) throw new InvalidDataException("Invalid compressed pointer length");
        return size;
    }

    static void ExpandPointer(ReadOnlySpan<byte> pointer, Span<byte> pixels)
    {
        using var decoder = new BrotliDecoder();
        if (decoder.Decompress(pointer[4..], pixels, out int read, out int written) != OperationStatus.Done ||
            read != pointer.Length - 4 || written != pixels.Length) throw new InvalidDataException("Invalid compressed pointer data");
    }
}
