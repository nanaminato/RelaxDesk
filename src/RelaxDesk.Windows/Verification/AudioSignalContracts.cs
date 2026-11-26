using System.Buffers.Binary;
using System.Diagnostics;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class AudioSignalContracts
{
    // Opt-in quiet 1 kHz output/loopback probe. Only aggregate measurements leave
    // this function; captured device samples are never written to disk.
    public static Task Run()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var capture = new WindowsAudio(true);
                using var render = new WindowsAudio(false);
                byte[] tone = new byte[480 * AudioProtocol.FrameBytes];
                for (int frame = 0; frame < 480; frame++)
                {
                    short sample = (short)Math.Round(512 * Math.Sin(frame * 2 * Math.PI * 1000 / AudioProtocol.Rate));
                    BinaryPrimitives.WriteInt16LittleEndian(tone.AsSpan(frame * 4), sample);
                    BinaryPrimitives.WriteInt16LittleEndian(tone.AsSpan(frame * 4 + 2), sample);
                }
                var samples = new List<short>();
                var clock = Stopwatch.StartNew(); int offset = 0;
                while (clock.ElapsedMilliseconds < 650)
                {
                    if (clock.ElapsedMilliseconds < 500)
                    {
                        offset += render.Write(tone.AsSpan(offset));
                        if (offset == tone.Length) offset = 0;
                    }
                    while (capture.Read() is { } frame)
                        for (int i = 0; i < frame.Data.Length; i += 4)
                            samples.Add(BinaryPrimitives.ReadInt16LittleEndian(frame.Data.AsSpan(i)));
                    Thread.Sleep(2);
                }
                int start = samples.FindIndex(sample => Math.Abs((int)sample) > 8);
                int end = samples.FindLastIndex(sample => Math.Abs((int)sample) > 8);
                if (start < 0 || end - start < 4800)
                    throw new IOException("No measurable loopback test tone; check endpoint mute/volume/routing");
                int crossings = 0, peak = 0; double energy = 0;
                for (int i = start + 1; i <= end; i++)
                {
                    if (samples[i - 1] < 0 && samples[i] >= 0) crossings++;
                    peak = Math.Max(peak, Math.Abs((int)samples[i])); energy += (double)samples[i] * samples[i];
                }
                double frequency = crossings * (double)AudioProtocol.Rate / (end - start);
                double rms = Math.Sqrt(energy / (end - start));
                Console.WriteLine($"WASAPI signal measured_hz={frequency:F1} pcm_peak={peak} pcm_rms={rms:F1} measured_frames={end-start}; 500ms quiet generated tone, aggregate only");
                if (frequency is < 950 or > 1050 || peak > 2048)
                    throw new IOException("Loopback PCM format/pitch probe differs from the 1 kHz low-level source; other active system sound can interfere");
                Console.WriteLine("PASS actual WASAPI shared PCM16 render-to-loopback frequency/amplitude probe; local default endpoint only, no remote speaker/listening claim");
                done.SetResult();
            }
            catch (Exception error) { done.SetException(error); }
        }) { IsBackground = true, Name = "RelaxDesk quiet audio signal probe" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); return done.Task;
    }
}
