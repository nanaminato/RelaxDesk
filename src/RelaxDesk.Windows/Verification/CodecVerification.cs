using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Compression;
namespace RelaxDesk.Windows;

public static class CodecVerification
{
    static void StampTemporalMarker(byte[] pixels, int width, int index)
    {
        for (int y = 8; y < 72; y++) for (int x = 16; x < 80; x++)
        {
            int at = (y * width + x) * 4; pixels[at] = (byte)(index % 2 == 0 ? 32 : 220); pixels[at + 1] = (byte)(40 + index * 31 % 180); pixels[at + 2] = (byte)(40 + index * 73 % 180);
        }
    }
    // Synthetic codec validation is deliberately independent of screen capture and remote input.
    public static void Run(bool hardware, int width = 1280, int height = 720)
    {
        using var encoder = new H264Encoder(null, width, height, hardware);
        using var decoder = new H264Decoder(width, height, hardware);
        var encode = new List<double>(); var decode = new List<double>();
        long bytes = 0; int decoded = 0; double squared = 0; long compared = 0;
        for (int index = 0; index < 60; index++)
        {
            byte[] pixels = Pattern(width, height, index);
            // A per-frame marker catches stale GPU uploads/conversions that static-band PSNR cannot detect.
            StampTemporalMarker(pixels, width, index);
            var timer = Stopwatch.StartNew();
            EncodedVideo? packet;
            try { packet = encoder.EncodeBgra(pixels, index * 333333L); }
            catch (ExternalException e) { throw new InvalidDataException($"Encode failed at synthetic frame {index}: {e.Message}", e); }
            encode.Add(timer.Elapsed.TotalMilliseconds);
            if (packet is null) continue;
            if (packet.Timestamp != index * 333333L) throw new InvalidDataException("Encoder added sample delay/reordering despite low-latency request");
            bytes += packet.Data.Length; timer.Restart();
            byte[]? output;
            try { output = decoder.Decode(packet.Data, packet.Timestamp); }
            catch (ExternalException e) { throw new InvalidDataException($"Decode failed at synthetic frame {index}, bytes={packet.Data.Length}, keyframe={packet.KeyFrame}: {e.Message}", e); }
            decode.Add(timer.Elapsed.TotalMilliseconds);
            if (output is null) continue;
            int marker = (40 * width + 48) * 4;
            for (int c = 0; c < 3; c++) if (Math.Abs(pixels[marker + c] - output[marker + c]) > 25) throw new InvalidDataException($"Stale/corrupt temporal marker at frame {index}; hardware={hardware}");
            decoded++;
            // Stable color bands are compared away from the moving square/4:2:0 boundaries.
            for (int y = 100; y < height - 100; y += 13)
                for (int x = 400; x < width - 100; x += 17)
                    for (int c = 0; c < 3; c++)
                    {
                        int at = (y * width + x) * 4 + c;
                        int difference = pixels[at] - output[at];
                        squared += difference * difference; compared++;
                    }
        }
        if (decoded < 50 || compared == 0) throw new InvalidDataException($"Insufficient decoded frames: {decoded}");
        double psnr = 10 * Math.Log10(255 * 255 / Math.Max(squared / compared, 0.000001));
        if (psnr < 25) throw new InvalidDataException($"Decoded color PSNR too low: {psnr:F2}");
        encode.Sort(); decode.Sort();
        Console.WriteLine($"PASS synthetic H264 hardware_encode={hardware} hardware_decode={decoder.Hardware} resolution={width}x{height} encoded={encode.Count} decoded={decoded} temporal_marker_verified={decoded} encode_p50_ms={Percentile(encode, .50):F2} encode_p95_ms={Percentile(encode, .95):F2} decode_p50_ms={Percentile(decode, .50):F2} decode_p95_ms={Percentile(decode, .95):F2} encoded_bytes={bytes} sampled_psnr_db={psnr:F2}");
    }
    public static void Compare1080p()
    {
        var encode = new List<double>(); var decode = new List<double>(); long bytes = 0;
        for (int i = 0; i < 60; i++)
        {
            byte[] pixels = Pattern(1920, 1080, i); StampTemporalMarker(pixels, 1920, i); var timer = Stopwatch.StartNew();
            using var output = new MemoryStream(); using (var zip = new DeflateStream(output, CompressionLevel.Fastest, true)) zip.Write(pixels);
            encode.Add(timer.Elapsed.TotalMilliseconds); bytes += output.Length; output.Position = 0; timer.Restart();
            using var unzip = new DeflateStream(output, CompressionMode.Decompress); byte[] decoded = new byte[pixels.Length]; unzip.ReadExactly(decoded);
            decode.Add(timer.Elapsed.TotalMilliseconds); if (!pixels.AsSpan().SequenceEqual(decoded)) throw new InvalidDataException("Lossless baseline mismatch");
        }
        encode.Sort(); decode.Sort();
        Console.WriteLine($"PASS synthetic Deflate baseline capture_excluded=true resolution=1920x1080 n=60 encode_p50_ms={Percentile(encode, .5):F2} encode_p95_ms={Percentile(encode, .95):F2} decode_p50_ms={Percentile(decode, .5):F2} decode_p95_ms={Percentile(decode, .95):F2} encoded_bytes={bytes} lossless=true");
        Run(false, 1920, 1080); Run(true, 1920, 1080);
    }
    public static double Percentile(List<double> sorted, double quantile) => sorted.Count == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Count * quantile) - 1, 0, sorted.Count - 1)];
    public static byte[] Pattern(int width, int height, int frame)
    {
        byte[] pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            pixels[i] = (byte)(50 + x / 160 * 16); pixels[i + 1] = (byte)(70 + y / 120 * 16); pixels[i + 2] = 160; pixels[i + 3] = 255;
            if (x >= frame % 200 && x < frame % 200 + 80 && y < 80) pixels[i + 2] = 50;
        }
        return pixels;
    }
}
