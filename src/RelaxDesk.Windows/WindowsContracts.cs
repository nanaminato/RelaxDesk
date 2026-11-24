using System.Runtime.InteropServices;
namespace RelaxDesk.Windows;

public static class WindowsContracts
{
    public static unsafe void Run()
    {
        int count = 0;
        void Check(bool result) { if (!result) throw new InvalidDataException($"Windows contract {count + 1} failed"); count++; }
        Check(KeyboardInjectionFlags.ForVirtualKey(0x41, false) == 0);
        Check(KeyboardInjectionFlags.ForVirtualKey(0x08, true) == 2);
        Check(KeyboardInjectionFlags.ForVirtualKey(0xA2, false) == 0);
        Check(KeyboardInjectionFlags.ForVirtualKey(0xA3, false) == 1);
        Check(KeyboardInjectionFlags.ForVirtualKey(0xA5, true) == 3);
        Check(KeyboardInjectionFlags.ForVirtualKey(0x25, false) == 1);
        Check(Marshal.SizeOf<NativeMethods.FrameInfo>() == 72);
        Check(Marshal.SizeOf<EncodeTiming>() == 96);
        Check(Marshal.SizeOf<DecodeTiming>() == 48);
        Check(Marshal.SizeOf<RenderTiming>() == 80);
        Check(Marshal.SizeOf<NativeResources>() == 56);
        byte[] native = [1, 2, 3, 4];
        fixed (byte* address = native)
        {
            byte[] firstShape = NativeMethods.Copy((nint)address, (uint)native.Length);
            Check(ReferenceEquals(firstShape, NativeMethods.Copy((nint)address, (uint)native.Length, firstShape)));
            native[0] = 9;
            byte[] secondShape = NativeMethods.Copy((nint)address, (uint)native.Length, firstShape);
            Check(!ReferenceEquals(firstShape, secondShape) && firstShape[0] == 1 && secondShape[0] == 9);
        }
        var metadata = new CaptureMetadata(123, 1, new byte[16], new byte[24], 0, 0, true, 2, 1, 1, 4, 0, 0, [10, 20, 30, 255]);
        var roundtrip = CaptureMetadataCodec.Decode(CaptureMetadataCodec.Encode(metadata));
        Check(roundtrip.Timestamp == 123 && roundtrip.DirtyRectangles.Length == 16 && roundtrip.MoveRectangles.Length == 24 && roundtrip.PointerShape.SequenceEqual(metadata.PointerShape));
        var largerPointer = metadata with { PointerWidth = 32, PointerHeight = 32, PointerPitch = 128, PointerShape = new byte[4096] };
        var pointerEncoder = new CaptureMetadataCodec.PointerEncoder();
        byte[] compact = pointerEncoder.Encode(largerPointer);
        Check(compact[0] == 2 && compact.Length < 256 && CaptureMetadataCodec.Decode(compact).PointerShape.SequenceEqual(largerPointer.PointerShape));
        Check(CaptureMetadataCodec.Encode(largerPointer)[0] == 1);
        Check(CaptureMetadataCodec.ForNativeRenderer(compact).SequenceEqual(CaptureMetadataCodec.Encode(largerPointer)));
        Check(CaptureMetadataCodec.Decode(pointerEncoder.Encode(largerPointer with { PointerX = 23 })).PointerX == 23);
        compact[64 + 16 + 24 + 3] = 127;
        try { CaptureMetadataCodec.Decode(compact); throw new Exception("Oversized pointer was accepted"); }
        catch (InvalidDataException) { count++; }
        byte[] pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata); Check(pixel.SequenceEqual(new byte[] { 10, 20, 30, 255 }));
        pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerType = 4, PointerShape = [255, 0, 255, 255] }); Check(pixel.SequenceEqual(new byte[] { 155, 100, 155, 255 }));
        pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerType = 1, PointerHeight = 2, PointerPitch = 1, PointerShape = [128, 128] }); Check(pixel.SequenceEqual(new byte[] { 155, 155, 155, 255 }));
        pixel = [0, 255, 0, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerType = 1, PointerHeight = 2, PointerPitch = 1, PointerShape = [128, 128] }); Check(pixel.SequenceEqual(new byte[] { 73, 73, 73, 255 }));
        pixel = [0, 255, 0, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerType = 4, PointerShape = [255, 255, 255, 255] }); Check(pixel.SequenceEqual(new byte[] { 73, 73, 73, 255 }));
        pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerX = -1 }); Check(pixel.SequenceEqual(new byte[] { 100, 100, 100, 255 }));
        foreach (var invalid in new[] { metadata with { PointerPitch = 1 }, metadata with { PointerType = 3 }, metadata with { PointerShape = [1] }, metadata with { PointerType = 4, PointerShape = [0, 0, 0, 128] } })
        { try { PointerCompositor.Validate(invalid); throw new Exception("Malformed pointer accepted"); } catch (InvalidDataException) { count++; } }
        try { CaptureMetadataCodec.Decode(new byte[63]); throw new Exception("Short metadata accepted"); } catch (InvalidDataException) { count++; }
        Console.WriteLine($"PASS {count} native ABI/metadata/pointer contracts; no capture or input");
    }
}
