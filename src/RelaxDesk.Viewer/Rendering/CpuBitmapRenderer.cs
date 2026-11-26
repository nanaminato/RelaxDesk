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
    public double Submit(DecodedMedia frame)
    {
        var timer = Stopwatch.StartNew();
        PointerCompositor.Apply(frame.Pixels, frame.Frame.Width, frame.Frame.Height, frame.Metadata);
        var cursor = frame.Metadata.UsesHostPointer ? Cursors.None : Cursors.Arrow;
        if (image.Cursor != cursor) image.Cursor = cursor;
        if (bitmap is null || bitmap.PixelWidth != frame.Frame.Width || bitmap.PixelHeight != frame.Frame.Height)
        {
            bitmap = new(frame.Frame.Width, frame.Frame.Height, 96, 96, PixelFormats.Bgr32, null); image.Source = bitmap;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight), frame.Pixels, bitmap.PixelWidth * 4, 0);
        return timer.Elapsed.TotalMilliseconds;
    }
    public void Clear() { image.ClearValue(FrameworkElement.CursorProperty); image.Source = null; bitmap = null; }
}
