using System.Buffers.Binary;
using RelaxDesk.Core;
namespace RelaxDesk.Windows;

public static class CaptureMetadataCodec
{
    const int Header = 64;
    public static byte[] Encode(CaptureMetadata m)
    {
        int size = checked(Header + m.DirtyRectangles.Length + m.MoveRectangles.Length + m.PointerShape.Length);
        if (size > MediaProtocol.MetadataLimit) throw new InvalidDataException("Capture metadata cap exceeded");
        byte[] data = new byte[size];
        int[] fields = [1, m.DirtyRectangles.Length, m.MoveRectangles.Length, m.PointerShape.Length, m.PointerX, m.PointerY,
            m.PointerVisible ? 1 : 0, (int)m.PointerType, (int)m.PointerWidth, (int)m.PointerHeight, (int)m.PointerPitch, m.HotspotX, m.HotspotY, (int)m.Accumulated];
        for (int i = 0; i < fields.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), fields[i]);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(56), m.Timestamp);
        m.DirtyRectangles.CopyTo(data, Header); m.MoveRectangles.CopyTo(data, Header + m.DirtyRectangles.Length);
        m.PointerShape.CopyTo(data, Header + m.DirtyRectangles.Length + m.MoveRectangles.Length); return data;
    }
    public static CaptureMetadata Decode(byte[] data)
    {
        if (data.Length < Header || data.Length > MediaProtocol.MetadataLimit) throw new InvalidDataException("Invalid capture metadata");
        int[] f = Enumerable.Range(0, 14).Select(i => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(i * 4))).ToArray();
        if (f[0] != 1 || f[1] < 0 || f[2] < 0 || f[3] < 0 || f[1] % 16 != 0 || f[2] % 24 != 0 ||
            (long)Header + f[1] + f[2] + f[3] != data.Length || f[6] is < 0 or > 1 || f[7] is < 0 or > 4 ||
            f[8] is < 0 or > 4096 || f[9] is < 0 or > 8192 || f[10] is < 0 or > 32768)
            throw new InvalidDataException("Invalid capture metadata fields");
        return new(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(56)), unchecked((uint)f[13]), data[Header..(Header + f[1])],
            data[(Header + f[1])..(Header + f[1] + f[2])], f[4], f[5], f[6] != 0, (uint)f[7], (uint)f[8], (uint)f[9], (uint)f[10], f[11], f[12], data[(Header + f[1] + f[2])..]);
    }
}
