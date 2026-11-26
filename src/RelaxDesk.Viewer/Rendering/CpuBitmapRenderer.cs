using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed class CpuBitmapRenderer(Image image)
{
    WriteableBitmap? bitmap;
    DecodedMedia? retained;
    byte[]? composited;
    bool pointerVisible = true;
    public void SetPointerVisible(bool visible)
    {
        if (pointerVisible == visible) return;
        pointerVisible = visible;
        if (retained is not null) Submit(retained);
    }
    public double Submit(DecodedMedia frame)
    {
        var timer = Stopwatch.StartNew();
        retained = frame;
        byte[] pixels = frame.Pixels;
        if (pointerVisible && frame.Metadata.PointerVisible && frame.Metadata.PointerShape.Length != 0)
        {
            if (composited?.Length != pixels.Length) composited = new byte[pixels.Length];
            pixels.CopyTo(composited, 0); pixels = composited;
            PointerCompositor.Apply(pixels, frame.Frame.Width, frame.Frame.Height, frame.Metadata);
        }
        var cursor = pointerVisible && frame.Metadata.UsesHostPointer ? Cursors.None : Cursors.Arrow;
        if (image.Cursor != cursor) image.Cursor = cursor;
        if (bitmap is null || bitmap.PixelWidth != frame.Frame.Width || bitmap.PixelHeight != frame.Frame.Height)
        {
            bitmap = new(frame.Frame.Width, frame.Frame.Height, 96, 96, PixelFormats.Bgr32, null); image.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), pixels, bitmap.PixelWidth * 4, 0);
        return timer.Elapsed.TotalMilliseconds;
    }
    public void Clear() { image.ClearValue(FrameworkElement.CursorProperty); image.Source = null; bitmap = null; retained = null; composited = null; }
}
