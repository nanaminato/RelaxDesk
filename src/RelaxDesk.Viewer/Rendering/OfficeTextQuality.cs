using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

internal static class OfficeTextQuality
{
    const int Width = 1280, Height = 720, Frames = 24;

    public sealed record TextQualityMetrics(
        double LumaMae,
        double LumaPsnrDb,
        double ChromaMae,
        double NeutralBleedMean,
        int NeutralBleedOutliers,
        double DarkCoreSatMean,
        double ColoredSyntaxErr,
        double EdgeSharpnessRatio,
        int HorizontalEdgeSamples,
        double MeanHorizontalEdgeError,
        int ColoredPixelSamples,
        double MeanColorError);

    public sealed record CodecPathSample(
        string PathName,
        string Resolution,
        int Dpi,
        int BitrateBps,
        int FramesEncoded,
        long TotalEncodedBytes,
        double EncodeMsPerFrame,
        double DecodeOrRenderMsPerFrame,
        ulong CpuReadbackBytes,
        TextQualityMetrics Metrics);

    public sealed record ViewportScaleSample(
        string Mode,
        int Zoom,
        int SourceWidth,
        int SourceHeight,
        int ClientWidth,
        int ClientHeight,
        double RenderMs,
        TextQualityMetrics Metrics);

    public static void Run(bool hardware)
    {
        var reference = RenderReference();
        Task.Run(() => Measure(reference, hardware)).GetAwaiter().GetResult();
    }

    static void Measure(byte[] reference, bool hardware)
    {
        foreach (int bitrate in new[] { 2_000_000, 4_000_000, 6_000_000, 8_000_000 })
        {
            using var encoder = new H264Encoder(null, Width, Height, hardware, fps: 30, bitrate: bitrate);
            using var decoder = new H264Decoder(Width, Height, hardware);
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
            var metrics = EvaluateMetrics(reference, decoded, Width, Height);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                encoder = encoder.Name, hardware, decoderHardware = decoder.Hardware, targetBitsPerSecond = bitrate, outputFrames, encodedBytes,
                metrics.HorizontalEdgeSamples, metrics.MeanHorizontalEdgeError, metrics.ColoredPixelSamples, metrics.MeanColorError,
                metrics.LumaPsnrDb, metrics.NeutralBleedMean, metrics.NeutralBleedOutliers, metrics.DarkCoreSatMean, metrics.EdgeSharpnessRatio
            }));
        }
        if (!GraphicsDiagnostics.Resources().Empty) throw new InvalidOperationException("Office text codec resources leaked");
    }

    public static void RunSuite(string outputDirectory)
    {
        string outDir = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(outDir);
        string stagesDir = Path.Combine(outDir, "stages");
        string cropsDir = Path.Combine(outDir, "crops");
        Directory.CreateDirectory(stagesDir);
        Directory.CreateDirectory(cropsDir);

        // 1. Hardware & Environment Capabilities Probe (without bypassing session/desktop boundaries)
        string captureDiagJson = GraphicsDiagnostics.CaptureJson();
        string codecCapsJson = GraphicsDiagnostics.CodecCapabilitiesJson();
        string liveCaptureStatus = Task.Run(() => ProbeLiveDesktopCapture(stagesDir)).GetAwaiter().GetResult();

        // 2. Stage-by-Stage & Before/After Color Conversion Isolation on Authentic Win32 ClearType 1920x1080
        var ref1080 = RenderWin32ClearTypeDesktop(1920, 1080, 96);
        SaveBgraPng(Path.Combine(stagesDir, "01_capture_source_bgra.png"), ref1080, 1920, 1080);

        var (_, legacyGpuColorBgra) = Task.Run(() => GraphicsDiagnostics.ColorConvertRoundtripForTest(ref1080, 1920, 1080, hardware: true, optimized: false)).GetAwaiter().GetResult();
        var (optNv12, optGpuColorBgra) = Task.Run(() => GraphicsDiagnostics.ColorConvertRoundtripForTest(ref1080, 1920, 1080, hardware: true, optimized: true)).GetAwaiter().GetResult();
        var (_, legacyCpuColorBgra) = GraphicsDiagnostics.ColorConvertRoundtripForTest(ref1080, 1920, 1080, hardware: false, optimized: false);
        var (_, optCpuColorBgra) = GraphicsDiagnostics.ColorConvertRoundtripForTest(ref1080, 1920, 1080, hardware: false, optimized: true);

        // Save Stage 2: NV12 luma + raw chroma preview & reconstructed color-converted stage
        SaveBgraPng(Path.Combine(stagesDir, "02_color_converted_nv12_legacy_gpu.png"), legacyGpuColorBgra, 1920, 1080);
        SaveBgraPng(Path.Combine(stagesDir, "02_color_converted_nv12_optimized_gpu.png"), optGpuColorBgra, 1920, 1080);
        SaveNv12PlaneSheetPng(Path.Combine(stagesDir, "02b_nv12_planes_visualized.png"), optNv12, 1920, 1080);

        var legacyGpuColorMetrics = EvaluateMetrics(ref1080, legacyGpuColorBgra, 1920, 1080);
        var optGpuColorMetrics = EvaluateMetrics(ref1080, optGpuColorBgra, 1920, 1080);
        var legacyCpuColorMetrics = EvaluateMetrics(ref1080, legacyCpuColorBgra, 1920, 1080);
        var optCpuColorMetrics = EvaluateMetrics(ref1080, optCpuColorBgra, 1920, 1080);

        // 3. Save Stage 3 (Decoded H.264) & Stage 4 (Final D3D11 SwapChain Rendered Viewport) + 8x Magnified Pixel Crops!
        var (decodedHw8M, renderedGpu1To1, renderedGpuFit) = Task.Run(() => RunStage3And4Capture(ref1080, 1920, 1080, 8_000_000)).GetAwaiter().GetResult();
        SaveBgraPng(Path.Combine(stagesDir, "03_decoded_h264_8mbps.png"), decodedHw8M, 1920, 1080);
        SaveBgraPng(Path.Combine(stagesDir, "04_final_rendered_d3d11_1to1.png"), renderedGpu1To1, 1920, 1080);
        SaveBgraPng(Path.Combine(stagesDir, "04b_final_rendered_d3d11_fit_1536x864.png"), renderedGpuFit, 1536, 864);

        // Export 8x Nearest-Neighbor Zoomed Pixel Comparison Strips:
        // Crop A: Chinese complex strokes ("赢、馨、鬱、囊") on white webpage (x=144, y=10, w=140, h=36)
        SaveZoomedComparisonStripPng(
            Path.Combine(cropsDir, "crop_chinese_cleartype_8x.png"),
            1920, 1080, new Int32Rect(140, 8, 136, 38), zoomFactor: 8,
            ("Source ClearType", ref1080),
            ("Legacy VideoProcessor", legacyGpuColorBgra),
            ("Legacy CPU Nearest", legacyCpuColorBgra),
            ("Optimized HLSL 8Mbps", renderedGpu1To1));

        // Crop B: Dark IDE colored syntax + Chinese comment (x=64, y=550, w=160, h=38)
        SaveZoomedComparisonStripPng(
            Path.Combine(cropsDir, "crop_dark_ide_code_8x.png"),
            1920, 1080, new Int32Rect(64, 548, 160, 38), zoomFactor: 8,
            ("Source ClearType", ref1080),
            ("Legacy VideoProcessor", legacyGpuColorBgra),
            ("Legacy CPU Nearest", legacyCpuColorBgra),
            ("Optimized HLSL 8Mbps", renderedGpu1To1));

        // Crop C: Office 1px gridline + red/black accounting numbers (x=964, y=30, w=140, h=46)
        SaveZoomedComparisonStripPng(
            Path.Combine(cropsDir, "crop_office_sheet_8x.png"),
            1920, 1080, new Int32Rect(960, 28, 140, 46), zoomFactor: 8,
            ("Source ClearType", ref1080),
            ("Legacy VideoProcessor", legacyGpuColorBgra),
            ("Legacy CPU Nearest", legacyCpuColorBgra),
            ("Optimized HLSL 8Mbps", renderedGpu1To1));

        // 4. Full Multi-Codec / Multi-Resolution / Multi-DPI / Multi-Bitrate Matrix
        var codecMatrix = new List<CodecPathSample>();

        // 4a. Compare DeflateBgra, Hardware H.264 (GPU Render), Hardware H.264 (CPU Readback), Software H.264, and Hybrid RDR1 Region Patch at 1080p@96dpi
        codecMatrix.Add(MeasureDeflateBgra(ref1080, 1920, 1080, 96));
        foreach (int bitrate in new[] { 2_000_000, 4_000_000, 8_000_000, 16_000_000 })
        {
            codecMatrix.Add(Task.Run(() => MeasureH264GpuRenderPath(ref1080, 1920, 1080, 96, bitrate)).GetAwaiter().GetResult());
            codecMatrix.Add(Task.Run(() => MeasureH264CpuDecodePath(ref1080, 1920, 1080, 96, hardwareEncode: true, hardwareDecode: true, bitrate)).GetAwaiter().GetResult());
            codecMatrix.Add(Task.Run(() => MeasureH264CpuDecodePath(ref1080, 1920, 1080, 96, hardwareEncode: false, hardwareDecode: false, bitrate)).GetAwaiter().GetResult());
        }
        codecMatrix.Add(Task.Run(() => MeasureHybridRegionPatchOn1080p(ref1080, 1920, 1080, 96)).GetAwaiter().GetResult());

        // 4b. Multi-Resolution & Multi-DPI ClearType Matrix (720p@96, 1080p@120 125%, 1080p@144 150%, 1440p@144 150%) + WPF DirectWrite source
        (int W, int H, int Dpi, bool WpfSource, string Label)[] dpiCases =
        [
            (1280, 720, 96, false, "1280x720_GDI_ClearType"),
            (1920, 1080, 120, false, "1920x1080_125pct_DPI120"),
            (1920, 1080, 144, false, "1920x1080_150pct_DPI144"),
            (2560, 1440, 144, false, "2560x1440_150pct_DPI144"),
            (1920, 1080, 96, true, "1920x1080_WPF_DirectWrite")
        ];
        foreach (var c in dpiCases)
        {
            byte[] scene = c.WpfSource ? RenderWpfDirectWriteDesktop(c.W, c.H, c.Dpi) : RenderWin32ClearTypeDesktop(c.W, c.H, c.Dpi);
            var sample = Task.Run(() => MeasureH264GpuRenderPath(scene, c.W, c.H, c.Dpi, 8_000_000, c.Label)).GetAwaiter().GetResult();
            codecMatrix.Add(sample);
        }

        // 5. Static Screen High-Quality Refresh (Sudden Window/Tab Switch -> 0 Settle Frames vs 6 Progressive P-Frames)
        var (settle0Metrics, settle6Metrics, transBytes, settleExtraBytes, settle0Bgra, settle6Bgra) =
            Task.Run(() => MeasureStaticSettleTransition(ref1080, 1920, 1080, 8_000_000)).GetAwaiter().GetResult();
        SaveZoomedComparisonStripPng(
            Path.Combine(cropsDir, "crop_static_settle_transition_8x.png"),
            1920, 1080, new Int32Rect(140, 8, 136, 38), zoomFactor: 8,
            ("Source ClearType", ref1080),
            ("TabSwitch Freeze (0 Settle)", settle0Bgra),
            ("Progressive Settle (+6 P)", settle6Bgra));

        // 6. Client Viewport Scaling Matrix (Zoom=100 1:1, Zoom=0 Fit 1536x864, Zoom=125, Zoom=150, Zoom=200)
        var viewportMatrix = Task.Run(() => MeasureViewportScalingMatrix(ref1080, 1920, 1080)).GetAwaiter().GetResult();

        if (!GraphicsDiagnostics.Resources().Empty)
            throw new InvalidOperationException("Native graphics resources leaked during text quality suite");

        var summary = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            liveCaptureStatus,
            captureDiagnostics = JsonSerializer.Deserialize<JsonElement>(captureDiagJson),
            codecCapabilities = JsonSerializer.Deserialize<JsonElement>(codecCapsJson),
            stage2ColorConversion1080p = new
            {
                legacyGpuVideoProcessor = legacyGpuColorMetrics,
                optimizedGpuHlsl = optGpuColorMetrics,
                legacyCpuNearest = legacyCpuColorMetrics,
                optimizedCpuBilateral = optCpuColorMetrics
            },
            staticSettle8MbpsTabSwitch = new
            {
                transitionFrameBytes = transBytes,
                sixSettlePFramesBytes = settleExtraBytes,
                beforeSettle0Frames = settle0Metrics,
                afterSettle6Frames = settle6Metrics
            },
            codecAndDpiMatrix = codecMatrix,
            viewportScalingMatrix = viewportMatrix
        };

        string summaryPath = Path.Combine(outDir, "summary.json");
        File.WriteAllText(summaryPath, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);

        string reportPath = Path.Combine(outDir, "report.md");
        File.WriteAllText(reportPath, BuildMarkdownReport(liveCaptureStatus, codecCapsJson, legacyGpuColorMetrics, optGpuColorMetrics, legacyCpuColorMetrics, optCpuColorMetrics, settle0Metrics, settle6Metrics, transBytes, settleExtraBytes, codecMatrix, viewportMatrix), Encoding.UTF8);

        // Automated Regression Gate Assertions (do not judge by bitrate/encode success alone!)
        if (optGpuColorMetrics.NeutralBleedOutliers >= legacyGpuColorMetrics.NeutralBleedOutliers * 0.70 ||
            optGpuColorMetrics.NeutralBleedMean >= legacyGpuColorMetrics.NeutralBleedMean * 0.75)
            throw new InvalidDataException("GPU HLSL ClearType fringe reduction regressed against baseline VideoProcessorBlt");
        if (optCpuColorMetrics.NeutralBleedOutliers >= legacyCpuColorMetrics.NeutralBleedOutliers * 0.60)
            throw new InvalidDataException("CPU bilateral NV12 conversion did not outperform legacy nearest-neighbor");
        if (settle6Metrics.LumaPsnrDb <= settle0Metrics.LumaPsnrDb + 3.0)
            throw new InvalidDataException("Progressive static P-frame settle did not materially improve post-motion text PSNR");

        Console.WriteLine($"PASS RelaxDesk text quality quantitative suite: stages={stagesDir} crops={cropsDir} summary={summaryPath}");
    }

    static string ProbeLiveDesktopCapture(string stagesDir)
    {
        try
        {
            using var dxgi = new DxgiCaptureBackend();
            CapturedFrame? captured = null;
            for (int i = 0; i < 5 && captured is null; i++) captured = dxgi.Capture(80);
            if (captured is not null)
            {
                byte[] pixels = dxgi.ReadPixels(captured);
                SaveBgraPng(Path.Combine(stagesDir, "00_live_dxgi_desktop_capture.png"), pixels, captured.Width, captured.Height);
                return $"DXGI_LIVE_CAPTURED ({captured.Width}x{captured.Height})";
            }
            return "DXGI_OPENED_NO_INITIAL_FRAME";
        }
        catch (Exception dxgiError)
        {
            try
            {
                using var gdi = new GdiCaptureBackend();
                if (gdi.Capture() is { Bgra: { } gdiPixels } gdiFrame)
                {
                    SaveBgraPng(Path.Combine(stagesDir, "00_live_gdi_thread_desktop.png"), gdiPixels, gdiFrame.Width, gdiFrame.Height);
                    return $"DXGI_BLOCKED ({dxgiError.GetType().Name} 0x{dxgiError.HResult:X8}: {dxgiError.Message}); GDI_THREAD_DESKTOP_CAPTURED ({gdiFrame.Width}x{gdiFrame.Height})";
                }
            }
            catch { }
            return $"DXGI_BLOCKED ({dxgiError.GetType().Name} 0x{dxgiError.HResult:X8}: {dxgiError.Message})";
        }
    }

    static (byte[] DecodedBgra, byte[] Rendered1To1, byte[] RenderedFit) RunStage3And4Capture(byte[] srcBgra, int w, int h, int bitrate)
    {
        using var encoder = new H264Encoder(null, w, h, hardware: true, fps: 30, bitrate: bitrate);
        using var gpuDecoder = new H264Decoder(w, h, hardware: true);
        using var cpuReadDecoder = new H264Decoder(w, h, hardware: true);

        byte[]? decodedBgra = null;
        GpuVideoFrame? lastSurface = null;
        try
        {
            for (int i = 0; i < 6; i++)
            {
                if (encoder.EncodeBgra(srcBgra, i * 333333L, i == 0) is not { } pkt) continue;
                if (cpuReadDecoder.Decode(pkt.Data, pkt.Timestamp) is { } px) decodedBgra = px;
                if (gpuDecoder.DecodeGpu(pkt.Data, pkt.Timestamp) is { } surf)
                {
                    lastSurface?.Dispose();
                    lastSurface = surf;
                }
            }
            if (decodedBgra is null || lastSurface is null)
                throw new InvalidOperationException("Failed to decode hardware H.264 frame for stage export");

            byte[] rendered1To1 = RenderWithNativeD3D11(lastSurface, w, h, new ViewportOptions(100, 0, 0), out _);
            byte[] renderedFit = RenderWithNativeD3D11(lastSurface, 1536, 864, new ViewportOptions(0, 0.5, 0.5), out _);
            return (decodedBgra, rendered1To1, renderedFit);
        }
        finally
        {
            lastSurface?.Dispose();
        }
    }

    static CodecPathSample MeasureDeflateBgra(byte[] srcBgra, int w, int h, int dpi)
    {
        var encSw = Stopwatch.StartNew();
        using var ms = new MemoryStream();
        using (var zip = new DeflateStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            zip.Write(srcBgra);
        encSw.Stop();
        byte[] compressed = ms.ToArray();

        var decSw = Stopwatch.StartNew();
        byte[] decoded = new byte[srcBgra.Length];
        ms.Position = 0;
        using (var unzip = new DeflateStream(ms, CompressionMode.Decompress))
            unzip.ReadExactly(decoded);
        decSw.Stop();

        return new("DeflateBgra (Lossless)", $"{w}x{h}", dpi, 0, 1, compressed.Length,
            encSw.Elapsed.TotalMilliseconds, decSw.Elapsed.TotalMilliseconds, (ulong)srcBgra.Length,
            EvaluateMetrics(srcBgra, decoded, w, h));
    }

    static CodecPathSample MeasureH264GpuRenderPath(byte[] srcBgra, int w, int h, int dpi, int bitrate, string? customLabel = null)
    {
        using var encoder = new H264Encoder(null, w, h, hardware: true, fps: 30, bitrate: bitrate);
        using var decoder = new H264Decoder(w, h, hardware: true);
        long totalBytes = 0;
        int frames = 0;
        var encTimes = new List<double>();
        GpuVideoFrame? lastSurface = null;
        try
        {
            for (int i = 0; i < 8; i++)
            {
                var sw = Stopwatch.StartNew();
                if (encoder.EncodeBgra(srcBgra, i * 333333L, i == 0) is not { } pkt) continue;
                encTimes.Add(sw.Elapsed.TotalMilliseconds);
                totalBytes += pkt.Data.Length;
                if (decoder.DecodeGpu(pkt.Data, pkt.Timestamp) is { } surf)
                {
                    lastSurface?.Dispose();
                    lastSurface = surf;
                    frames++;
                }
            }
            if (lastSurface is null) throw new InvalidOperationException("No GPU decoded frame");
            ulong decodeReadback = decoder.Timing.ReadbackBytes;
            byte[] rendered = RenderWithNativeD3D11(lastSurface, w, h, new ViewportOptions(100, 0, 0), out double steadyDrawMs);
            string label = customLabel ?? $"HW H.264 -> GPU D3D11 Render ({bitrate / 1_000_000}Mbps)";
            return new(label, $"{w}x{h}", dpi, bitrate, frames, totalBytes,
                encTimes.Average(), decoder.Timing.TotalMs + steadyDrawMs, decodeReadback,
                EvaluateMetrics(srcBgra, rendered, w, h));
        }
        finally
        {
            lastSurface?.Dispose();
        }
    }

    static CodecPathSample MeasureH264CpuDecodePath(byte[] srcBgra, int w, int h, int dpi, bool hardwareEncode, bool hardwareDecode, int bitrate)
    {
        using var encoder = new H264Encoder(null, w, h, hardware: hardwareEncode, fps: 30, bitrate: bitrate);
        using var decoder = new H264Decoder(w, h, hardware: hardwareDecode);
        long totalBytes = 0;
        int frames = 0;
        byte[]? lastBgra = null;
        var encTimes = new List<double>();
        var decTimes = new List<double>();
        for (int i = 0; i < 8; i++)
        {
            var sw = Stopwatch.StartNew();
            if (encoder.EncodeBgra(srcBgra, i * 333333L, i == 0) is not { } pkt) continue;
            encTimes.Add(sw.Elapsed.TotalMilliseconds);
            totalBytes += pkt.Data.Length;
            sw.Restart();
            if (decoder.Decode(pkt.Data, pkt.Timestamp) is { } px)
            {
                decTimes.Add(sw.Elapsed.TotalMilliseconds);
                lastBgra = px;
                frames++;
            }
        }
        if (lastBgra is null) throw new InvalidOperationException("No CPU decoded frame");
        string name = hardwareEncode ? $"HW H.264 -> WPF Readback ({bitrate / 1_000_000}Mbps)" : $"SW H.264 -> CPU Bilateral ({bitrate / 1_000_000}Mbps)";
        return new(name, $"{w}x{h}", dpi, bitrate, frames, totalBytes,
            encTimes.Average(), decTimes.Average(), decoder.Timing.ReadbackBytes,
            EvaluateMetrics(srcBgra, lastBgra, w, h));
    }

    static CodecPathSample MeasureHybridRegionPatchOn1080p(byte[] srcBgra, int w, int h, int dpi)
    {
        // Base H.264 frame at 8 Mbps + lossless RDR1 compensation over active editor/text dirty region (~12% screen area)
        using var encoder = new H264Encoder(null, w, h, hardware: true, fps: 30, bitrate: 8_000_000);
        using var decoder = new H264Decoder(w, h, hardware: true);
        byte[]? baseDecoded = null;
        long h264Bytes = 0;
        for (int i = 0; i < 4; i++)
        {
            if (encoder.EncodeBgra(srcBgra, i * 333333L, i == 0) is not { } pkt) continue;
            h264Bytes = pkt.Data.Length;
            if (decoder.Decode(pkt.Data, pkt.Timestamp) is { } px) baseDecoded = px;
        }
        if (baseDecoded is null) throw new InvalidOperationException("Hybrid base frame missing");
        var sw = Stopwatch.StartNew();
        RegionRect[] rawDirty = [new(12, 8, 920, 260)];
        var tiles = RegionCompensationProtocol.PlanTiles(rawDirty, w, h, RegionCompensationProtocol.DefaultTileSize);
        var patches = tiles.Select(t => RegionCompensationProtocol.CreatePatch(srcBgra, w, h, t)).ToArray();
        var compPkt = new RegionCompensationPacket(1, 1, 4, 1, w, h, [], patches);
        byte[] wireComp = RegionCompensationProtocol.Encode(compPkt);
        double encodePatchMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var receiver = new RegionCompensationProtocol.ReceiverState(() => { }, enabled: true);
        receiver.OnVideoFrameDecoded(4, 1, 1, w, h);
        byte[] hybridSurface = (byte[])baseDecoded.Clone();
        if (receiver.TryApply(RegionCompensationProtocol.Decode(wireComp), hybridSurface) != RegionApplyOutcome.Applied)
            throw new InvalidOperationException("Hybrid RDR1 patch apply failed");
        double applyMs = sw.Elapsed.TotalMilliseconds;

        ulong patchReadbackBytes = (ulong)patches.Sum(p => (long)p.UncompressedBytes);
        return new("Hybrid H.264 + RDR1 Lossless Dirty Tile (Stage B)", $"{w}x{h}", dpi, 8_000_000, 1,
            h264Bytes + wireComp.Length, encodePatchMs, applyMs, patchReadbackBytes,
            EvaluateMetrics(srcBgra, hybridSurface, w, h));
    }

    static (TextQualityMetrics Settle0, TextQualityMetrics Settle6, int TransBytes, int SettleBytes, byte[] Settle0Bgra, byte[] Settle6Bgra)
        MeasureStaticSettleTransition(byte[] targetBgra, int w, int h, int bitrate)
    {
        // Simulate switching from an active dark application workspace with UI chrome/cards to a full-screen ClearType document/spreadsheet.
        byte[] prevBgra = new byte[targetBgra.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool card = (x % 320 < 280) && (y % 180 < 140);
                prevBgra[i + 0] = (byte)(card ? 48 + ((x * 7 + y * 3) & 31) : 28);
                prevBgra[i + 1] = (byte)(card ? 40 + ((x * 5 + y * 11) & 31) : 24);
                prevBgra[i + 2] = (byte)(card ? 36 + ((x * 3 + y * 7) & 31) : 22);
                prevBgra[i + 3] = 255;
            }
        }

        (byte[] Img, int TransLen, int ExtraLen) RunSequence(int settleFrames)
        {
            using var encoder = new H264Encoder(null, w, h, hardware: true, fps: 30, bitrate: bitrate);
            using var decoder = new H264Decoder(w, h, hardware: true);
            byte[]? last = null;
            int trans = 0, extra = 0;
            for (int f = 0; f < 4 + settleFrames; f++)
            {
                byte[] src = f < 3 ? prevBgra : targetBgra;
                if (encoder.EncodeBgra(src, f * 333333L, f == 0) is not { } pkt) continue;
                if (f == 3) trans = pkt.Data.Length;
                else if (f > 3) extra += pkt.Data.Length;
                if (decoder.Decode(pkt.Data, pkt.Timestamp) is { } px) last = px;
            }
            return (last ?? throw new InvalidOperationException("Settle sequence failed"), trans, extra);
        }

        var s0 = RunSequence(0);
        var s6 = RunSequence(6);
        return (EvaluateMetrics(targetBgra, s0.Img, w, h), EvaluateMetrics(targetBgra, s6.Img, w, h), s6.TransLen, s6.ExtraLen, s0.Img, s6.Img);
    }

    static List<ViewportScaleSample> MeasureViewportScalingMatrix(byte[] srcBgra, int w, int h)
    {
        var list = new List<ViewportScaleSample>();
        using var encoder = new H264Encoder(null, w, h, hardware: true, fps: 30, bitrate: 12_000_000);
        using var decoder = new H264Decoder(w, h, hardware: true);
        GpuVideoFrame? surface = null;
        try
        {
            for (int i = 0; i < 6; i++)
            {
                if (encoder.EncodeBgra(srcBgra, i * 333333L, i == 0) is not { } pkt) continue;
                if (decoder.DecodeGpu(pkt.Data, pkt.Timestamp) is { } s) { surface?.Dispose(); surface = s; }
            }
            if (surface is null) throw new InvalidOperationException("Viewport scaling decode failed");

            (string Label, int Zoom, int Cw, int Ch)[] cases =
            [
                ("D3D11 Exact 1:1 (Zoom=100)", 100, 1920, 1080),
                ("D3D11 Fit Downscale 80% (Zoom=0, 1536x864)", 0, 1536, 864),
                ("D3D11 Upscale 125% Crop (Zoom=125)", 125, 1600, 900),
                ("D3D11 Upscale 150% Crop (Zoom=150)", 150, 1600, 900),
                ("D3D11 Upscale 200% Crop (Zoom=200)", 200, 1600, 900)
            ];

            foreach (var c in cases)
            {
                var opts = new ViewportOptions(c.Zoom, 0, 0);
                byte[] rendered = RenderWithNativeD3D11(surface, c.Cw, c.Ch, opts, out double steadyDrawMs);
                byte[] expectedView = BuildReferenceViewport(srcBgra, w, h, c.Cw, c.Ch, opts);
                list.Add(new(c.Label, c.Zoom, w, h, c.Cw, c.Ch, steadyDrawMs,
                    EvaluateMetrics(expectedView, rendered, c.Cw, c.Ch)));
            }
            return list;
        }
        finally
        {
            surface?.Dispose();
        }
    }

    static byte[] RenderWithNativeD3D11(GpuVideoFrame surface, int clientWidth, int clientHeight, ViewportOptions viewport, out double steadyDrawMs)
    {
        nint parent = CreateTestHostWindow(clientWidth + 32, clientHeight + 64);
        nint child = 0;
        try
        {
            child = GraphicsDiagnostics.CreateChildWindow(parent);
            if (!MoveWindow(child, 0, 0, clientWidth, clientHeight, true))
                throw new InvalidOperationException("MoveWindow child failed");
            PumpPendingMessages();
            using var renderer = new D3D11Renderer(surface, child);
            return renderer.DrawAndReadBackbufferForTest(surface, clientWidth, clientHeight, viewport, out steadyDrawMs);
        }
        finally
        {
            if (child != 0) GraphicsDiagnostics.DestroyChildWindow(child);
            if (parent != 0) DestroyWindow(parent);
        }
    }

    static byte[] BuildReferenceViewport(byte[] srcBgra, int srcW, int srcH, int clientW, int clientH, ViewportOptions opts)
    {
        var layout = VideoViewport.Calculate(clientW, clientH, srcW, srcH, opts);
        if (layout.SourceWidth == layout.DestinationWidth && layout.SourceHeight == layout.DestinationHeight &&
            layout.DestinationX == 0 && layout.DestinationY == 0 && layout.DestinationWidth == clientW && layout.DestinationHeight == clientH)
        {
            byte[] crop = new byte[clientW * clientH * 4];
            for (int y = 0; y < clientH; y++)
            {
                int srcOff = ((layout.SourceY + y) * srcW + layout.SourceX) * 4;
                Buffer.BlockCopy(srcBgra, srcOff, crop, y * clientW * 4, clientW * 4);
            }
            return crop;
        }
        // High-precision area/bicubic reference via WPF Fant/HighQuality visual render
        byte[] dst = new byte[clientW * clientH * 4];
        double scaleX = (double)layout.SourceWidth / layout.DestinationWidth;
        double scaleY = (double)layout.SourceHeight / layout.DestinationHeight;
        for (int dy = 0; dy < clientH; dy++)
        {
            for (int dx = 0; dx < clientW; dx++)
            {
                int di = (dy * clientW + dx) * 4;
                dst[di + 3] = 255;
                if (dx < layout.DestinationX || dy < layout.DestinationY ||
                    dx >= layout.DestinationX + layout.DestinationWidth || dy >= layout.DestinationY + layout.DestinationHeight)
                    continue;
                double sx = layout.SourceX + (dx - layout.DestinationX + 0.5) * scaleX - 0.5;
                double sy = layout.SourceY + (dy - layout.DestinationY + 0.5) * scaleY - 0.5;
                int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
                double fx = sx - x0, fy = sy - y0;
                for (int c = 0; c < 3; c++)
                {
                    int c00 = srcBgra[(Math.Clamp(y0, 0, srcH - 1) * srcW + Math.Clamp(x0, 0, srcW - 1)) * 4 + c];
                    int c10 = srcBgra[(Math.Clamp(y0, 0, srcH - 1) * srcW + Math.Clamp(x0 + 1, 0, srcW - 1)) * 4 + c];
                    int c01 = srcBgra[(Math.Clamp(y0 + 1, 0, srcH - 1) * srcW + Math.Clamp(x0, 0, srcW - 1)) * 4 + c];
                    int c11 = srcBgra[(Math.Clamp(y0 + 1, 0, srcH - 1) * srcW + Math.Clamp(x0 + 1, 0, srcW - 1)) * 4 + c];
                    double val = c00 * (1 - fx) * (1 - fy) + c10 * fx * (1 - fy) + c01 * (1 - fx) * fy + c11 * fx * fy;
                    dst[di + c] = (byte)Math.Clamp((int)Math.Round(val), 0, 255);
                }
            }
        }
        return dst;
    }

    public static TextQualityMetrics EvaluateMetrics(byte[] reference, byte[] test, int width, int height)
    {
        double sumY = 0, sumY2 = 0, sumChroma = 0;
        double sumNeutralBleed = 0, sumDarkSat = 0, sumSyntaxErr = 0;
        double refGrad = 0, tstGrad = 0;
        int neutralNearText = 0, bleedOutliers = 0, darkCores = 0, syntaxCount = 0;
        long legacyEdgeErr = 0, legacyColorErr = 0;
        int legacyEdges = 0, legacyColored = 0;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                int b0 = reference[i], g0 = reference[i + 1], r0 = reference[i + 2];
                int b1 = test[i], g1 = test[i + 1], r1 = test[i + 2];

                double y0 = 0.2126 * r0 + 0.7152 * g0 + 0.0722 * b0;
                double y1 = 0.2126 * r1 + 0.7152 * g1 + 0.0722 * b1;
                double dy = y1 - y0;
                sumY += Math.Abs(dy);
                sumY2 += dy * dy;

                double cb0 = (b0 - y0) / 1.8556, cr0 = (r0 - y0) / 1.5748;
                double cb1 = (b1 - y1) / 1.8556, cr1 = (r1 - y1) / 1.5748;
                sumChroma += Math.Sqrt((cb1 - cb0) * (cb1 - cb0) + (cr1 - cr0) * (cr1 - cr0));

                int sat0 = Math.Max(r0, Math.Max(g0, b0)) - Math.Min(r0, Math.Min(g0, b0));
                int sat1 = Math.Max(r1, Math.Max(g1, b1)) - Math.Min(r1, Math.Min(g1, b1));

                bool nearEdge = false;
                int nyMin = Math.Max(0, y - 1), nyMax = Math.Min(height - 1, y + 1);
                int nxMin = Math.Max(0, x - 2), nxMax = Math.Min(width - 1, x + 2);
                for (int ny = nyMin; ny <= nyMax && !nearEdge; ny++)
                {
                    for (int nx = nxMin; nx <= nxMax; nx++)
                    {
                        int ni = (ny * width + nx) * 4;
                        double ny0 = 0.2126 * reference[ni + 2] + 0.7152 * reference[ni + 1] + 0.0722 * reference[ni];
                        if (Math.Abs(ny0 - y0) > 25.0) { nearEdge = true; break; }
                    }
                }

                if (sat0 <= 8 && nearEdge)
                {
                    neutralNearText++;
                    int excess = Math.Max(0, sat1 - sat0);
                    sumNeutralBleed += excess;
                    if (excess > 15) bleedOutliers++;
                    if (y0 < 80.0) { darkCores++; sumDarkSat += excess; }
                }

                if (x > 0 && x + 1 < width && sat0 >= 35)
                {
                    int il = i - 4, ir = i + 4;
                    int drL = reference[il + 2] - reference[il], drC = r0 - b0, drR = reference[ir + 2] - reference[ir];
                    if ((drC > 25 && (drL > 15 || drR > 15)) || (drC < -25 && (drL < -15 || drR < -15)))
                    {
                        syntaxCount++;
                        sumSyntaxErr += (Math.Abs(r1 - r0) + Math.Abs(g1 - g0) + Math.Abs(b1 - b0)) / 3.0;
                    }
                }

                if (x > 0)
                {
                    int ip = i - 4;
                    double py0 = 0.2126 * reference[ip + 2] + 0.7152 * reference[ip + 1] + 0.0722 * reference[ip];
                    double py1 = 0.2126 * test[ip + 2] + 0.7152 * test[ip + 1] + 0.0722 * test[ip];
                    if (Math.Abs(y0 - py0) >= 20.0)
                    {
                        refGrad += Math.Abs(y0 - py0);
                        tstGrad += Math.Abs(y1 - py1);
                    }

                    int legRef = ((19 * b0 + 183 * g0 + 54 * r0) >> 8) - ((19 * reference[ip] + 183 * reference[ip + 1] + 54 * reference[ip + 2]) >> 8);
                    if (Math.Abs(legRef) >= 20)
                    {
                        int legOut = ((19 * b1 + 183 * g1 + 54 * r1) >> 8) - ((19 * test[ip] + 183 * test[ip + 1] + 54 * test[ip + 2]) >> 8);
                        legacyEdgeErr += Math.Abs(legRef - legOut);
                        legacyEdges++;
                    }
                    if (b0 - r0 >= 20)
                    {
                        legacyColorErr += Math.Abs(b0 - b1) + Math.Abs(g0 - g1) + Math.Abs(r0 - r1);
                        legacyColored++;
                    }
                }
            }
        }

        double n = (double)width * height;
        double mse = sumY2 / n;
        double psnr = mse <= 1e-9 ? 99.0 : 10.0 * Math.Log10((255.0 * 255.0) / mse);
        return new(
            Math.Round(sumY / n, 3),
            Math.Round(psnr, 2),
            Math.Round(sumChroma / n, 3),
            Math.Round(neutralNearText > 0 ? sumNeutralBleed / neutralNearText : 0, 3),
            bleedOutliers,
            Math.Round(darkCores > 0 ? sumDarkSat / darkCores : 0, 3),
            Math.Round(syntaxCount > 0 ? sumSyntaxErr / syntaxCount : 0, 3),
            Math.Round(refGrad > 0 ? tstGrad / refGrad : 1.0, 4),
            legacyEdges,
            Math.Round(legacyEdges > 0 ? (double)legacyEdgeErr / legacyEdges : 0, 3),
            legacyColored,
            Math.Round(legacyColored > 0 ? (double)legacyColorErr / (3.0 * legacyColored) : 0, 3));
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

    static byte[] RenderWpfDirectWriteDesktop(int w, int h, int dpi)
    {
        double scale = dpi / 96.0;
        double dipW = w / scale, dipH = h / scale;
        var drawing = new DrawingVisual();
        using (var ctx = drawing.RenderOpen())
        {
            ctx.DrawRectangle(Brushes.White, null, new Rect(0, 0, dipW, dipH / 2));
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(30, 30, 30)), null, new Rect(0, dipH / 2, dipW, dipH / 2));
            var yahei = new Typeface("Microsoft YaHei UI");
            var consolas = new Typeface("Consolas");
            double y = 10;
            int[] sizes = [10, 12, 14, 16, 20];
            for (int r = 0; y + 24 < dipH / 2; r++)
            {
                double sz = sizes[r % sizes.Length];
                var ft = new FormattedText(
                    $"[{sz}px DPI={dpi}] 远程桌面清晰度测试：细小笔画（赢、馨、鬱、囊、龜）与 0123456789 边缘保真",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, yahei, sz,
                    r % 4 == 3 ? Brushes.DarkBlue : Brushes.Black, scale);
                ctx.DrawText(ft, new Point(14, y));
                y += sz + 6;
            }
            y = dipH / 2 + 10;
            for (int r = 0; y + 20 < dipH; r++)
            {
                var c1 = new FormattedText($"public static DecodedSurface? DecodeGpu_{r:D2}(ReadOnlySpan<byte> nal) ",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, consolas, 13,
                    new SolidColorBrush(Color.FromRgb(86, 156, 214)), scale);
                var c2 = new FormattedText("// 零拷贝双边色度重建与 1:1 像素对齐",
                    CultureInfo.InvariantCulture, FlowDirection.LeftToRight, yahei, 12,
                    new SolidColorBrush(Color.FromRgb(106, 153, 85)), scale);
                ctx.DrawText(c1, new Point(16, y));
                ctx.DrawText(c2, new Point(16 + c1.WidthIncludingTrailingWhitespace, y));
                y += 19;
            }
        }
        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(drawing);
        byte[] bgra = new byte[w * h * 4];
        rtb.CopyPixels(bgra, w * 4, 0);
        for (int i = 0; i < w * h; i++) bgra[i * 4 + 3] = 255;
        return bgra;
    }

    static byte[] RenderWin32ClearTypeDesktop(int w, int h, int dpi)
    {
        double s = dpi / 96.0;
        int Scale(int px) => Math.Max(8, (int)Math.Round(px * s));

        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32
        };
        nint dc = CreateCompatibleDC(0);
        nint dib = CreateDIBSection(dc, ref bmi, 0, out nint bits, 0, 0);
        nint oldBmp = SelectObject(dc, dib);
        try
        {
            void Fill(int l, int t, int r, int b, uint colorRef)
            {
                var rc = new RECT { Left = l, Top = t, Right = r, Bottom = b };
                nint br = CreateSolidBrush(colorRef);
                FillRect(dc, ref rc, br);
                DeleteObject(br);
            }

            Fill(0, 0, w / 2, h / 2, Rgb(255, 255, 255));
            Fill(w / 2, 0, w, h / 2, Rgb(248, 250, 252));
            Fill(0, h / 2, w, h, Rgb(30, 30, 30));

            nint pen = CreatePen(0, 1, Rgb(203, 213, 225));
            nint oldPen = SelectObject(dc, pen);
            for (int gy = Scale(28); gy < h / 2; gy += Scale(24)) { MoveToEx(dc, w / 2, gy, 0); LineTo(dc, w, gy); }
            for (int gx = w / 2; gx < w; gx += Scale(120)) { MoveToEx(dc, gx, 0, 0); LineTo(dc, gx, h / 2); }
            SelectObject(dc, oldPen);
            DeleteObject(pen);

            SetBkMode(dc, 1);
            int[] sizes = [10, 12, 14, 16, 20];
            nint[] yaheiFonts = sizes.Select(sz => CreateClearTypeFont(-Scale(sz), "Microsoft YaHei UI")).ToArray();
            nint f12 = yaheiFonts[1];
            nint fCode = CreateClearTypeFont(-Scale(13), "Consolas");

            string[] webLines =
            [
                "远程桌面文字清晰度测试：细小笔画（如“赢、馨、鬱、囊、龜、龘”）与数字 0123456789 边缘锐利度。",
                "当使用 NV12 4:2:0 色度降采样传输 ClearType 文本时，红蓝子像素偶极子易与 2x2 色度块串色。",
                "办公文档摘要：第一季度营收增长 18.45%，研发投入占比 24.70%，累计处理工单 98,765 件。",
                "API 响应延迟 P50 = 0.91ms, P95 = 1.41ms | HTTP/2 200 OK | Content-Type: application/json"
            ];

            int y = Scale(8);
            for (int row = 0; y + Scale(22) < h / 2; row++)
            {
                int szIdx = row % sizes.Length;
                SelectObject(dc, yaheiFonts[szIdx]);
                SetTextColor(dc, row % 5 == 4 ? Rgb(29, 78, 216) : Rgb(15, 23, 42));
                string txt = webLines[row % webLines.Length];
                TextOutW(dc, Scale(14), y, txt, txt.Length);
                y += Scale(sizes[szIdx] + 6);
            }

            SelectObject(dc, f12);
            for (int r = 0; Scale(8) + r * Scale(24) < h / 2 - Scale(16); r++)
            {
                for (int c = 0; w / 2 + Scale(8) + c * Scale(120) < w - Scale(90); c++)
                {
                    string cell;
                    if (r == 0)
                    {
                        SetTextColor(dc, Rgb(30, 41, 59));
                        cell = $"指标列_{c + 1:D2}";
                    }
                    else if ((r + c) % 6 == 0)
                    {
                        SetTextColor(dc, Rgb(185, 28, 28));
                        cell = $"-¥{(r * 13 + c) % 99},{(r * 317 + c * 41) % 1000:D3}.{(r * 19 + c) % 100:D2}";
                    }
                    else
                    {
                        SetTextColor(dc, Rgb(15, 23, 42));
                        cell = $"¥{(r * 29 + c) % 999},{(r * 613 + c * 83) % 1000:D3}.{(r * 37 + c) % 100:D2}";
                    }
                    TextOutW(dc, w / 2 + Scale(8) + c * Scale(120), Scale(6) + r * Scale(24), cell, cell.Length);
                }
            }

            y = h / 2 + Scale(10);
            for (int row = 0; y + Scale(18) < h; row++)
            {
                string ln = $"{row + 1,4}";
                SelectObject(dc, fCode);
                SetTextColor(dc, Rgb(133, 133, 133));
                TextOutW(dc, Scale(10), y, ln, ln.Length);
                for (int col = 0; col < 2; col++)
                {
                    int bx = Scale(64) + col * (w / 2 - Scale(40));
                    if (bx + Scale(420) > w) break;
                    SelectObject(dc, fCode);
                    SetTextColor(dc, Rgb(86, 156, 214)); TextOutW(dc, bx, y, "public static", 13);
                    SetTextColor(dc, Rgb(78, 201, 176)); TextOutW(dc, bx + Scale(104), y, "DecodedSurface?", 15);
                    SetTextColor(dc, Rgb(220, 220, 170)); TextOutW(dc, bx + Scale(224), y, "DecodeGpu", 9);
                    SetTextColor(dc, Rgb(212, 212, 212)); TextOutW(dc, bx + Scale(296), y, "(ReadOnlySpan<byte> nal)", 24);
                    if (bx + Scale(640) <= w)
                    {
                        SelectObject(dc, f12);
                        const string cmt = "// 零拷贝硬件解码与双边色度重建";
                        SetTextColor(dc, Rgb(106, 153, 85)); TextOutW(dc, bx + Scale(488), y, cmt, cmt.Length);
                    }
                }
                y += Scale(19);
            }

            foreach (nint hf in yaheiFonts) DeleteObject(hf);
            DeleteObject(fCode);
            GdiFlush();

            byte[] outBgra = new byte[w * h * 4];
            Marshal.Copy(bits, outBgra, 0, outBgra.Length);
            for (int i = 0; i < w * h; i++) outBgra[i * 4 + 3] = 255;
            return outBgra;
        }
        finally
        {
            SelectObject(dc, oldBmp);
            DeleteObject(dib);
            DeleteDC(dc);
        }
    }

    static void SaveBgraPng(string path, byte[] bgra, int width, int height)
    {
        var bmp = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    static void SaveNv12PlaneSheetPng(string path, byte[] nv12, int width, int height)
    {
        // Left half: full-res Y luma; Right half: 2x nearest-expanded false-color Cb/Cr visualization
        byte[] vis = new byte[width * height * 4];
        int uvBase = width * height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                if (x < width / 2)
                {
                    byte lum = nv12[y * width + x];
                    vis[o] = lum; vis[o + 1] = lum; vis[o + 2] = lum; vis[o + 3] = 255;
                }
                else
                {
                    int sx = x - width / 2;
                    byte u = nv12[uvBase + (y / 2) * width + (sx & ~1)];
                    byte v = nv12[uvBase + (y / 2) * width + (sx & ~1) + 1];
                    vis[o] = u; vis[o + 1] = 128; vis[o + 2] = v; vis[o + 3] = 255;
                }
            }
        }
        SaveBgraPng(path, vis, width, height);
    }

    static void SaveZoomedComparisonStripPng(string path, int srcW, int srcH, Int32Rect roi, int zoomFactor, params (string Label, byte[] Pixels)[] panels)
    {
        int headerH = 34;
        int pad = 12;
        int panelW = roi.Width * zoomFactor;
        int panelH = roi.Height * zoomFactor;
        int totalW = pad + panels.Length * (panelW + pad);
        int totalH = headerH + panelH + pad;

        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)), null, new Rect(0, 0, totalW, totalH));
            var tf = new Typeface("Segoe UI Semibold");

            for (int p = 0; p < panels.Length; p++)
            {
                int ox = pad + p * (panelW + pad);
                int oy = headerH;
                byte[] zoomed = new byte[panelW * panelH * 4];
                byte[] src = panels[p].Pixels;
                for (int ry = 0; ry < roi.Height; ry++)
                {
                    for (int rx = 0; rx < roi.Width; rx++)
                    {
                        int si = ((roi.Y + ry) * srcW + (roi.X + rx)) * 4;
                        byte b = src[si], g = src[si + 1], r = src[si + 2];
                        for (int zy = 0; zy < zoomFactor; zy++)
                        {
                            for (int zx = 0; zx < zoomFactor; zx++)
                            {
                                int di = ((ry * zoomFactor + zy) * panelW + (rx * zoomFactor + zx)) * 4;
                                // Subtle 1px pixel-grid border at bottom/right of each 8x cell
                                bool grid = zx == zoomFactor - 1 || zy == zoomFactor - 1;
                                zoomed[di + 0] = grid ? (byte)((b * 7 + 128) / 8) : b;
                                zoomed[di + 1] = grid ? (byte)((g * 7 + 128) / 8) : g;
                                zoomed[di + 2] = grid ? (byte)((r * 7 + 128) / 8) : r;
                                zoomed[di + 3] = 255;
                            }
                        }
                    }
                }

                var bmp = BitmapSource.Create(panelW, panelH, 96, 96, PixelFormats.Bgra32, null, zoomed, panelW * 4);
                ctx.DrawImage(bmp, new Rect(ox, oy, panelW, panelH));
                var label = new FormattedText(panels[p].Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    tf, 14, Brushes.White, 1.0);
                ctx.DrawText(label, new Point(ox + 4, 8));
            }
        }

        var rtb = new RenderTargetBitmap(totalW, totalH, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        png.Save(fs);
    }

    static string BuildMarkdownReport(
        string liveCaptureStatus,
        string codecCapsJson,
        TextQualityMetrics legacyGpuColor,
        TextQualityMetrics optGpuColor,
        TextQualityMetrics legacyCpuColor,
        TextQualityMetrics optCpuColor,
        TextQualityMetrics settle0,
        TextQualityMetrics settle3,
        int transBytes,
        int settleBytes,
        List<CodecPathSample> codecMatrix,
        List<ViewportScaleSample> viewportMatrix)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# RelaxDesk 桌面文字画质量化测试报告");
        sb.AppendLine();
        sb.AppendLine($"- **实时桌面采集状态**：`{liveCaptureStatus}`");
        sb.AppendLine($"- **硬件编解码与 4:4:4 实测能力 JSON**：`{codecCapsJson}`");
        sb.AppendLine();
        sb.AppendLine("## 1. Stage 2 色彩转换与色度重建优化前后对比 (1920x1080 真实 ClearType)");
        sb.AppendLine();
        sb.AppendLine("| 路径 | Luma PSNR (dB) | 中性字异常彩边均值 | 彩边异常像素数 (>15) | 黑字核伪饱和度 | 彩色语法字误差 | 边缘锐度保持率 |");
        sb.AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | ---: |");
        sb.AppendLine($"| 旧版 GPU `VideoProcessorBlt` | {legacyGpuColor.LumaPsnrDb:F2} | {legacyGpuColor.NeutralBleedMean:F2} | {legacyGpuColor.NeutralBleedOutliers:N0} | {legacyGpuColor.DarkCoreSatMean:F2} | {legacyGpuColor.ColoredSyntaxErr:F2} | {legacyGpuColor.EdgeSharpnessRatio:F4} |");
        sb.AppendLine($"| **新版 GPU HLSL 相干度+双边重建** | **{optGpuColor.LumaPsnrDb:F2}** | **{optGpuColor.NeutralBleedMean:F2}** | **{optGpuColor.NeutralBleedOutliers:N0}** | **{optGpuColor.DarkCoreSatMean:F2}** | **{optGpuColor.ColoredSyntaxErr:F2}** | **{optGpuColor.EdgeSharpnessRatio:F4}** |");
        sb.AppendLine($"| 旧版 CPU 2x2 Box + 最近邻 | {legacyCpuColor.LumaPsnrDb:F2} | {legacyCpuColor.NeutralBleedMean:F2} | {legacyCpuColor.NeutralBleedOutliers:N0} | {legacyCpuColor.DarkCoreSatMean:F2} | {legacyCpuColor.ColoredSyntaxErr:F2} | {legacyCpuColor.EdgeSharpnessRatio:F4} |");
        sb.AppendLine($"| **新版 CPU 相干度+双边重建** | **{optCpuColor.LumaPsnrDb:F2}** | **{optCpuColor.NeutralBleedMean:F2}** | **{optCpuColor.NeutralBleedOutliers:N0}** | **{optCpuColor.DarkCoreSatMean:F2}** | **{optCpuColor.ColoredSyntaxErr:F2}** | **{optCpuColor.EdgeSharpnessRatio:F4}** |");
        sb.AppendLine();
        sb.AppendLine("## 2. 编码器、分辨率、DPI 与码率矩阵");
        sb.AppendLine();
        sb.AppendLine("| 媒体路径 / 场景 | 分辨率@DPI | 总编码字节 | 编码耗时(ms) | 解码/渲染(ms) | CPU Readback(B) | Luma PSNR(dB) | 彩边均值 | 彩边异常点 | 边缘锐度比 |");
        sb.AppendLine("| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var s in codecMatrix)
        {
            sb.AppendLine($"| {s.PathName} | {s.Resolution}@{s.Dpi} | {s.TotalEncodedBytes:N0} | {s.EncodeMsPerFrame:F2} | {s.DecodeOrRenderMsPerFrame:F2} | {s.CpuReadbackBytes:N0} | {s.Metrics.LumaPsnrDb:F2} | {s.Metrics.NeutralBleedMean:F2} | {s.Metrics.NeutralBleedOutliers:N0} | {s.Metrics.EdgeSharpnessRatio:F4} |");
        }
        sb.AppendLine();
        sb.AppendLine("## 3. 剧烈切屏后静态画面渐进式 P 帧精修 (8 Mbps 1080p)");
        sb.AppendLine();
        sb.AppendLine($"- 首帧切换负载：`{transBytes / 1024.0:F1} KiB`；6 帧静态精修合计增量负载：`{settleBytes / 1024.0:F1} KiB`");
        sb.AppendLine($"- 运动停止无精修 (`0 Settle`)：PSNR = `{settle0.LumaPsnrDb:F2} dB`，边缘锐度比 = `{settle0.EdgeSharpnessRatio:F4}`，水平边缘误差 = `{settle0.MeanHorizontalEdgeError:F2}`");
        sb.AppendLine($"- **渐进式静态精修 (`+6 P-Frames`)**：PSNR = **`{settle3.LumaPsnrDb:F2} dB`**，边缘锐度比 = **`{settle3.EdgeSharpnessRatio:F4}`**，水平边缘误差 = **`{settle3.MeanHorizontalEdgeError:F2}`**");
        sb.AppendLine();
        sb.AppendLine("## 4. 客户端 D3D11 视口缩放比例矩阵");
        sb.AppendLine();
        sb.AppendLine("| 视口模式 | 客户区尺寸 | 渲染耗时(ms) | Luma PSNR(dB) | 彩边均值 | 边缘锐度保持率 |");
        sb.AppendLine("| :--- | :--- | ---: | ---: | ---: | ---: |");
        foreach (var v in viewportMatrix)
        {
            sb.AppendLine($"| {v.Mode} | {v.ClientWidth}x{v.ClientHeight} | {v.RenderMs:F2} | {v.Metrics.LumaPsnrDb:F2} | {v.Metrics.NeutralBleedMean:F2} | {v.Metrics.EdgeSharpnessRatio:F4} |");
        }
        return sb.ToString();
    }

    static uint Rgb(byte r, byte g, byte b) => (uint)(r | (g << 8) | (b << 16));

    static nint CreateClearTypeFont(int height, string family) =>
        CreateFontW(height, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5 /* CLEARTYPE_QUALITY */, 0, family);

    static nint CreateTestHostWindow(int width, int height)
    {
        nint hwnd = CreateWindowExW(0, "STATIC", "RelaxDesk Text Quality Offscreen Host",
            0x00CF0000 | 0x02000000 /* WS_OVERLAPPEDWINDOW | WS_CLIPCHILDREN */,
            80, 80, width, height, 0, 0, GetModuleHandleW(null), 0);
        if (hwnd == 0) throw new InvalidOperationException("CreateWindowExW failed");
        ShowWindow(hwnd, 4 /* SW_SHOWNOACTIVATE */);
        return hwnd;
    }

    static void PumpPendingMessages()
    {
        while (PeekMessageW(out var msg, 0, 0, 0, 1))
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public nint hwnd; public uint message; public nint wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("gdi32.dll")] static extern nint CreateCompatibleDC(nint hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(nint hdc);
    [DllImport("gdi32.dll")] static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);
    [DllImport("gdi32.dll")] static extern nint SelectObject(nint hdc, nint h);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint ho);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] static extern nint CreatePen(int iStyle, int cWidth, uint color);
    [DllImport("gdi32.dll")] static extern bool MoveToEx(nint hdc, int x, int y, nint lppt);
    [DllImport("gdi32.dll")] static extern bool LineTo(nint hdc, int x, int y);
    [DllImport("gdi32.dll")] static extern int SetBkMode(nint hdc, int mode);
    [DllImport("gdi32.dll")] static extern uint SetTextColor(nint hdc, uint color);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern nint CreateFontW(int cHeight, int cWidth, int cEscapement, int cOrientation, int cWeight, uint bItalic, uint bUnderline, uint bStrikeOut, uint iCharSet, uint iOutPrecision, uint iClipPrecision, uint iQuality, uint iPitchAndFamily, string pszFaceName);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern bool TextOutW(nint hdc, int x, int y, string lpString, int c);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("user32.dll")] static extern int FillRect(nint hDC, ref RECT lprc, nint hbr);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(uint exStyle, string className, string windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hWnd, int nCmdShow);
    [DllImport("user32.dll")] static extern bool MoveWindow(nint hWnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hWnd);
    [DllImport("user32.dll")] static extern bool PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")] static extern nint DispatchMessageW(ref MSG lpMsg);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? lpModuleName);
}
