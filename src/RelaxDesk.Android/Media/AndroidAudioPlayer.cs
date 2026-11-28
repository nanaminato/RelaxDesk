using Android.Media;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile;

/// <summary>
/// Low-latency native Android AudioTrack player for RelaxDesk 48 kHz stereo PCM16 audio stream.
/// Isolated from the video and control pipelines so audio errors never disrupt desktop control.
/// </summary>
internal sealed class AndroidAudioPlayer : IDisposable
{
    AudioTrack? track;
    volatile bool muted;
    volatile bool disposed;

    public bool Muted
    {
        get => muted;
        set
        {
            muted = value;
            if (value)
            {
                try { track?.Flush(); } catch { }
            }
        }
    }

    public async Task RunAsync(System.IO.Stream audioStream, CancellationToken ct)
    {
        try
        {
            int minBuf = AudioTrack.GetMinBufferSize(AudioProtocol.Rate, ChannelOut.Stereo, Encoding.Pcm16bit);
            if (minBuf <= 0) minBuf = AudioProtocol.Rate * AudioProtocol.FrameBytes / 20; // ~50ms fallback
            int bufferBytes = Math.Max(minBuf * 2, 4800 * AudioProtocol.FrameBytes);

            using var attrs = new AudioAttributes.Builder()
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Movie)!
                .Build();
            using var format = new AudioFormat.Builder()
                .SetSampleRate(AudioProtocol.Rate)!
                .SetChannelMask(ChannelOut.Stereo)!
                .SetEncoding(Encoding.Pcm16bit)!
                .Build();

            var builder = new AudioTrack.Builder()
                .SetAudioAttributes(attrs!)
                .SetAudioFormat(format!)
                .SetBufferSizeInBytes(bufferBytes)
                .SetTransferMode(AudioTrackMode.Stream);
            builder.SetPerformanceMode(AudioTrackPerformanceMode.LowLatency);

            track = builder.Build();
            track.Play();

            while (!ct.IsCancellationRequested && !disposed)
            {
                var frame = await AudioProtocol.Read(audioStream, ct).ConfigureAwait(false);
                if (muted) continue;
                if (frame.Discontinuity)
                {
                    try { track.Flush(); } catch { }
                }
                track.Write(frame.Data, 0, frame.Data.Length, WriteMode.Blocking);
            }
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("RelaxDeskAudio", $"Audio playback stopped: {ex.GetType().Name} - {ex.Message}");
        }
        finally
        {
            ReleaseTrack();
        }
    }

    void ReleaseTrack()
    {
        var t = Interlocked.Exchange(ref track, null);
        if (t is null) return;
        try { t.Stop(); } catch { }
        try { t.Release(); } catch { }
        try { t.Dispose(); } catch { }
    }

    public void Dispose()
    {
        disposed = true;
        ReleaseTrack();
    }
}
