using System.Runtime.InteropServices;
namespace RelaxDesk.Windows;

public static class WindowsContracts
{
    public static void Run()
    {
        int count = 0;
        void Check(bool result) { if (!result) throw new InvalidDataException($"Windows contract {count + 1} failed"); count++; }
        Check(Marshal.SizeOf<NativeMethods.FrameInfo>() == 72);
        Check(Marshal.SizeOf<EncodeTiming>() == 96);
        Check(Marshal.SizeOf<DecodeTiming>() == 48);
        Check(Marshal.SizeOf<RenderTiming>() == 80);
        Check(Marshal.SizeOf<NativeResources>() == 56);
        var metadata = new CaptureMetadata(123, 1, new byte[16], new byte[24], 0, 0, true, 2, 1, 1, 4, 0, 0, [10, 20, 30, 255]);
        var roundtrip = CaptureMetadataCodec.Decode(CaptureMetadataCodec.Encode(metadata));
        Check(roundtrip.Timestamp == 123 && roundtrip.DirtyRectangles.Length == 16 && roundtrip.MoveRectangles.Length == 24 && roundtrip.PointerShape.SequenceEqual(metadata.PointerShape));
        byte[] pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata); Check(pixel.SequenceEqual(new byte[] { 10, 20, 30, 255 }));
        pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerType = 4, PointerShape = [255, 0, 255, 255] }); Check(pixel.SequenceEqual(new byte[] { 155, 100, 155, 255 }));
        pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerType = 1, PointerHeight = 2, PointerPitch = 1, PointerShape = [128, 128] }); Check(pixel.SequenceEqual(new byte[] { 155, 155, 155, 255 }));
        pixel = [100, 100, 100, 255]; PointerCompositor.Apply(pixel, 1, 1, metadata with { PointerX = -1 }); Check(pixel.SequenceEqual(new byte[] { 100, 100, 100, 255 }));
        foreach (var invalid in new[] { metadata with { PointerPitch = 1 }, metadata with { PointerType = 3 }, metadata with { PointerShape = [1] }, metadata with { PointerType = 4, PointerShape = [0, 0, 0, 128] } })
        { try { PointerCompositor.Validate(invalid); throw new Exception("Malformed pointer accepted"); } catch (InvalidDataException) { count++; } }
        try { CaptureMetadataCodec.Decode(new byte[63]); throw new Exception("Short metadata accepted"); } catch (InvalidDataException) { count++; }
        Console.WriteLine($"PASS {count} native ABI/metadata/pointer contracts; no capture or input");
    }
}
