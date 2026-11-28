using System.Buffers.Binary;

namespace RelaxDesk.Core;

public enum ServiceDesktopKind { Default = 0, Secure = 1, Transition = 2 }
public sealed record ServiceDesktopFrame(long Sequence, long Epoch, ServiceDesktopKind Kind, int Width, int Height, byte[] Pixels, int PointerX=0, int PointerY=0);

// Local authenticated IPC only. Raw BGRA has an exact, bounded length; no JSON
// base64 copy and no payload allocation before the header is validated.
public static class ServiceDesktopProtocol
{
    public const string Ready = "service-desktop-ready-v1", Request = "service-desktop-frame-v1";
    public const int HeaderSize = 48, MaximumPixels = 8_294_400;
    const uint Magic = 0x31445352;
    public static int Validate(ServiceDesktopFrame frame)
    {
        if (frame.Sequence <= 0 || frame.Epoch is < 1 or > 1_000_000 ||
            frame.Kind is not (ServiceDesktopKind.Default or ServiceDesktopKind.Secure or ServiceDesktopKind.Transition))
            throw new InvalidDataException("Invalid service desktop identity/state");
        if (frame.Kind != ServiceDesktopKind.Secure)
        {
            if (frame.Width != 0 || frame.Height != 0 || frame.Pixels.Length != 0 || frame.PointerX != 0 || frame.PointerY != 0) throw new InvalidDataException("Unexpected desktop status payload");
            return 0;
        }
        if (frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > MaximumPixels)
            throw new InvalidDataException("Service desktop geometry exceeds bound");
        int length = checked(frame.Width * frame.Height * 4);
        if (frame.Pixels.Length != length) throw new InvalidDataException("Service desktop pixel length mismatch");
        return length;
    }
    public static async Task Write(Stream stream, ServiceDesktopFrame frame, CancellationToken cancellation)
    {
        int length = Validate(frame);
        byte[] header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), frame.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), frame.Epoch);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24), (int)frame.Kind);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(32), frame.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(36), length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40), frame.PointerX);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(44), frame.PointerY);
        await stream.WriteAsync(header, cancellation).ConfigureAwait(false);
        if (length != 0) await stream.WriteAsync(frame.Pixels, cancellation).ConfigureAwait(false);
    }
    public static async Task<ServiceDesktopFrame> Read(Stream stream, long expectedSequence, CancellationToken cancellation)
    {
        byte[] header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, cancellation).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != 1)
            throw new InvalidDataException("Unknown service desktop frame");
        var frame = new ServiceDesktopFrame(BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)), (ServiceDesktopKind)BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(28)), BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(32)), [],
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(40)),BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(44)));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(36));
        if (frame.Sequence != expectedSequence || frame.Epoch is < 1 or > 1_000_000 ||
            frame.Kind is not (ServiceDesktopKind.Default or ServiceDesktopKind.Secure or ServiceDesktopKind.Transition) ||
            (frame.Kind == ServiceDesktopKind.Secure
                ? frame.Width <= 0 || frame.Height <= 0 || (long)frame.Width * frame.Height > MaximumPixels || length != (long)frame.Width * frame.Height * 4
                : frame.Width != 0 || frame.Height != 0 || length != 0 || frame.PointerX != 0 || frame.PointerY != 0))
            throw new InvalidDataException("Service desktop header/binding rejected");
        byte[] pixels = new byte[length];
        if (length != 0) await stream.ReadExactlyAsync(pixels, cancellation).ConfigureAwait(false);
        return frame with { Pixels = pixels };
    }
}

// Worker-thread state. A desktop change invalidates all input stamped while the
// previous image was visible. Only the same unlocked interactive session is eligible.
public sealed class ServiceDesktopEpoch
{
    string? desktop;
    public long Epoch { get; private set; } = 1;
    public bool Secure => desktop == "Winlogon";
    public bool Observe(string name)
    {
        if (name is not ("Default" or "Winlogon")) throw new InvalidDataException("Unsupported service desktop");
        if (desktop == name) return false;
        if (desktop is not null)
        {
            if (Epoch == 1_000_000) throw new IOException("Desktop epoch exhausted");
            Epoch++;
        }
        desktop = name;
        return true;
    }
    public void BeginTransition()
    {
        if (desktop is null) return;
        if (Epoch == 1_000_000) throw new IOException("Desktop epoch exhausted");
        Epoch++; desktop = null;
    }
    public bool Accept(long epoch) => desktop is not null && epoch == Epoch;
}
