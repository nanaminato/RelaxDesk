using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    sealed class MeasuredAudioSink : IAudioEndpoint
    {
        readonly WindowsAudio endpoint = new(false);
        public long Frames; public int Flushes;
        public bool IsDefaultDevice() => endpoint.IsDefaultDevice();
        public Core.AudioFrame? Read() => throw new InvalidOperationException();
        public int Write(ReadOnlySpan<byte> data)
        { int written = endpoint.Write(data); Interlocked.Add(ref Frames, written / Core.AudioProtocol.FrameBytes); return written; }
        public void Flush() { Interlocked.Increment(ref Flushes); endpoint.Flush(); }
        public void Dispose() => endpoint.Dispose();
    }

    // Explicit opt-in real-host/real-speaker probe, not part of unattended suites.
    // Uses existing opted-in credentials and verified pin; never records samples.
    internal static void VerifyRemoteAudio(Application app, string address, int number, string userName)
    {
        var endpoint = new IPEndPoint(IPAddress.Parse(address), number).ToString();
        var saved = new ViewerCredentialStore().Read(endpoint) ?? throw new IOException("No opted-in credential for endpoint");
        if (saved.UserName != userName || string.IsNullOrEmpty(PasswordLoginSettings.ReadServerPin(endpoint)))
            throw new IOException("Matching saved user and verified certificate pin required");
        var window = new ViewerWindow { ShowActivated = false };
        window.host.Text = address; window.port.Text = number.ToString(); window.loginName.Text = userName; window.token.Password = saved.Password;
        window.requestClipboard.IsChecked = window.requestClipboardImage.IsChecked = window.requestClipboardHtml.IsChecked = window.requestFileClipboard.IsChecked = false;
        window.requestAudio.IsChecked = true; window.gpuRendering.IsChecked = false; window.startFullScreen.IsChecked = false; window.networkMode.SelectedIndex = 1;
        MeasuredAudioSink? sink = null;
        window.AudioSinkForVerification = () => sink = new();
        window.Loaded += async (_, _) =>
        {
            Task? running = null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            try
            {
                running = window.ConnectVideo();
                while (window.audioSession?.Packets < 20 || window.audioSession is null || window.smokeFrames == 0)
                {
                    if (running.IsCompleted) throw new IOException("Connection ended before playback");
                    await Task.Delay(20, deadline.Token);
                }
                await Task.Delay(TimeSpan.FromSeconds(10), deadline.Token);
                var audio = window.audioSession ?? throw new IOException("Audio disabled during playback");
                long received = audio.PayloadBytes / Core.AudioProtocol.FrameBytes;
                long rendered = sink is null ? 0 : Interlocked.Read(ref sink.Frames);
                double ratio = rendered / (double)Math.Max(1, received);
                Console.WriteLine(JsonSerializer.Serialize(new { receivedFrames = received, renderedFrames = rendered, renderFraction = ratio,
                    flushes = sink?.Flushes, videoFrames = window.smokeFrames, failure = audio.Failure,
                    renderer = "WPF CPU", synchronizedToVideo = true, samplesSaved = false }));
                if (received < 400_000 || ratio < .95 || audio.Failure is not null)
                    throw new IOException("Real WPF audio playback did not sustain source frame delivery");
                Console.WriteLine("PASS authorized remote WPF/TLS/real-WASAPI video-synchronized audio throughput; no physical listening-quality claim");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                window.StopConnection();
                if (running is not null) try { await running; } catch { }
                window.Close(); app.Shutdown(Environment.ExitCode);
            }
        };
        app.Run(window);
    }
}
