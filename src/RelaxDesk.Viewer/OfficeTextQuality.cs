using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal static class OfficeTextQuality
{
    const int Width = 1280, Height = 720, Frames = 24;

    public static void Run(bool hardware)
    {
        var reference = RenderReference();
        Task.Run(() => Measure(reference, hardware)).GetAwaiter().GetResult();
    }

    static void Measure(byte[] reference, bool hardware)
    {
        foreach (int bitrate in new[] { 2_000_000, 8_000_000 })
        {
            using var encoder = new H264Encoder(null, Width, Height, hardware, fps: 30, bitrate: bitrate);
            using var decoder = new H264Decoder(Width, Height);
            byte[]? decoded = null;
            long encodedBytes = 0;
            int outputFrames = 0;
            for (int index = 0; index < Frames; index++)
            {
                if (encoder.EncodeBgra(reference, index * 10_000_000L / 30, index == 0) is not { } packet) continue;
                encodedBytes += packet.Data.Length;
                if (decoder.Decode(packet.Data, packet.Timestamp) is not { } pixels) continue;
                decoded = pixels;
                outputFrames++;
            }
            if (decoded is null || outputFrames < Frames / 2) throw new InvalidOperationException("Insufficient decoded office text samples");
            var edges = CompareEdges(reference, decoded);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                encoder = encoder.Name, hardware, targetBitsPerSecond = bitrate, outputFrames, encodedBytes,
                edges.HorizontalEdgeSamples, edges.MeanHorizontalEdgeError, edges.ColoredPixelSamples, edges.MeanColorError
            }));
        }
        if (!GraphicsDiagnostics.Resources().Empty) throw new InvalidOperationException("Office text codec resources leaked");
    }

    static byte[] RenderReference()
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, Width, Height));
            var font = new Typeface("Consolas");
            for (int row = 0; row < 24; row++)
            {
                double size = 11 + 2 * (row % 3);
                double y = 20 + 27 * row;
                var prefix = new FormattedText($"{row + 1:D2}   static string Message({row}) => ", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, font, size, Brushes.Black, 1);
                var suffix = new FormattedText("\"中文 日本語 editor_0123456789\";", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, font, size, Brushes.DarkBlue, 1);
                for (int column = 0; column < 3; column++)
                {
                    double x = 20 + 410 * column;
                    context.DrawText(prefix, new Point(x, y));
                    context.DrawText(suffix, new Point(x + prefix.WidthIncludingTrailingWhitespace, y));
                }
            }
        }
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        return pixels;
    }

    static (int HorizontalEdgeSamples, double MeanHorizontalEdgeError, int ColoredPixelSamples, double MeanColorError)
        CompareEdges(byte[] reference, byte[] decoded)
    {
        static int Luma(byte[] pixels, int offset) => (19 * pixels[offset] + 183 * pixels[offset + 1] + 54 * pixels[offset + 2]) >> 8;
        long edgeError = 0, colorError = 0;
        int edges = 0, colored = 0;
        for (int y = 0; y < Height; y++)
        for (int x = 1; x < Width; x++)
        {
            int offset = (y * Width + x) * 4;
            int edge = Luma(reference, offset) - Luma(reference, offset - 4);
            if (Math.Abs(edge) >= 20)
            {
                int outputEdge = Luma(decoded, offset) - Luma(decoded, offset - 4);
                edgeError += Math.Abs(edge - outputEdge);
                edges++;
            }
            if (reference[offset] - reference[offset + 2] >= 20)
            {
                for (int channel = 0; channel < 3; channel++)
                    colorError += Math.Abs(reference[offset + channel] - decoded[offset + channel]);
                colored++;
            }
        }
        if (edges == 0 || colored == 0) throw new InvalidOperationException("Office text reference has no expected edges or color");
        return (edges, (double)edgeError / edges, colored, (double)colorError / (3 * colored));
    }
}
