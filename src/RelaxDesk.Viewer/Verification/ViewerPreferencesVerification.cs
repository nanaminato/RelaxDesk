using System.IO;
using System.Windows;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void VerifyPreferences(Application app, string fixtures)
    {
        string root = Path.Combine(Path.GetFullPath(fixtures), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new ViewerPreferencesStore(Path.Combine(root, "viewer.json"));
        var history = new ConnectionHistoryStore(Path.Combine(root, "connections.json"));
        history.Remember("192.0.2.1", 48200).GetAwaiter().GetResult();
        history.Remember("192.0.2.2", 48201).GetAwaiter().GetResult();
        var window = new ViewerWindow { historyStore = history, ShowActivated = false };
        ViewerWindow? restored = null;
        int checks = 0;
        void Check(bool value) { if (!value) throw new IOException($"Viewer preferences check {checks + 1}"); checks++; }
        void Reject(Action action)
        {
            bool failed = false;
            try { action(); } catch (Exception error) when (error is InvalidDataException or System.Text.Json.JsonException) { failed = true; }
            Check(failed);
        }
        MediaFrame Frame(MediaCodec codec) => new(codec, 1, 0, 1, 1, true, false, 0, 0, [], [1], VideoGeneration: 1);
        window.Loaded += async (_, _) =>
        {
            try
            {
                await window.HistoryOperation;
                Check(window.host.Text == "192.0.2.2" && window.port.Text == "48201" && window.recentConnections.SelectedIndex == 0);
                Check(window.connectionRun is null && window.token.Password == "");
                Check(store.Load() == new ViewerPreferences());
                window.preferencesStore = store;
                window.startFullScreen.IsChecked = false; window.gpuRendering.IsChecked = false;
                window.requestTopology.IsChecked = false; window.requestClipboardImage.IsChecked = false;
                window.requestAudio.IsChecked = false; window.networkMode.SelectedIndex = 1; window.fileRateLimit.SelectedIndex = 2;
                window.SetViewportZoom(137);
                using var sessionLifetime = new CancellationTokenSource();
                window.session = sessionLifetime;
                window.videoSettingsSession = new(VideoSettingsProtocol.Default);
                window.settingsCodec = MediaCodec.H264;
                window.videoSettingsSession.Presented(Frame(MediaCodec.H264));
                window.ChangeVideoSettings(60, 20_000_000);
                Check(window.outgoing.TryRead(out var command) && command?.VideoSettings is { FramesPerSecond:60, Bitrate:20_000_000, Generation:1 });
                await window.preferencesOperation;
                var saved = store.Load();
                Check(saved is { FramesPerSecond:60, Bitrate:20_000_000, Zoom:137, FullScreen:false, Gpu:false,
                    Topology:false, ClipboardImage:false, Audio:false, Network:1, FileRate:2 });
                Check(!File.ReadAllText(Path.Combine(root, "viewer.json")).Contains("Password"));
                restored = new ViewerWindow { preferences = store.Load(), historyStore = history, ShowActivated = false };
                restored.ApplyPreferences(); restored.Show(); await restored.HistoryOperation;
                Check(restored.CurrentViewport.Zoom == 137 && restored.startFullScreen.IsChecked == false && restored.networkMode.SelectedIndex == 1);
                Check(restored.host.Text == "192.0.2.2" && restored.port.Text == "48201");
                restored.session = sessionLifetime;
                restored.videoSettingsSession = new(VideoSettingsProtocol.Default); restored.restoreVideoPreferences = true;
                Check(restored.outgoing.Depth == 0); // Wait for the first authorized frame.
                restored.VideoSettingsPresented(Frame(MediaCodec.H264));
                Check(restored.outgoing.TryRead(out command) && command?.VideoSettings is { FramesPerSecond:60, Bitrate:20_000_000, Generation:1 });
                restored.VideoSettingsPresented(Frame(MediaCodec.H264)); Check(restored.outgoing.Depth == 0);
                restored.videoSettingsSession = new(VideoSettingsProtocol.Default); restored.restoreVideoPreferences = true;
                restored.VideoSettingsPresented(Frame(MediaCodec.DeflateBgra));
                Check(restored.outgoing.TryRead(out command) && command?.VideoSettings is { FramesPerSecond:60, Bitrate:8_000_000 });
                Check(restored.preferences.Bitrate == 20_000_000);
                restored.videoSettingsSession = null; restored.restoreVideoPreferences = true;
                restored.VideoSettingsPresented(Frame(MediaCodec.H264)); Check(restored.outgoing.Depth == 0);
                Reject(() => store.Save(saved with { FramesPerSecond = 61 }));
                Reject(() => store.Save(saved with { Network = 3 }));
                Reject(() => store.Save(saved with { Zoom = 401 }));
                File.WriteAllText(Path.Combine(root, "viewer.json"), "{"); Reject(() => store.Load());
                File.WriteAllText(Path.Combine(root, "viewer.json"), new string(' ', 16_385)); Reject(() => store.Load());
                File.WriteAllText(Path.Combine(root, "viewer.json"), "null"); Reject(() => store.Load());
                store.Save(saved); Check(store.Load() == saved);
                Console.WriteLine($"PASS {checks} Viewer preference/history/recreation/video-restore/legacy/codec/validation checks; isolated local files and synthetic frames, no Host grant or physical capture claim");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                if (restored is not null) { restored.session = null; restored.Close(); }
                window.session = null; window.Close(); app.Shutdown(Environment.ExitCode);
            }
        };
        app.Run(window);
    }
}
