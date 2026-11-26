using System.IO;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    sealed class QualityClock : TimeProvider
    {
        long value;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => value;
        public void Advance() => value += 1100;
    }
    internal static void VerifyAutomaticQuality(Application app, string fixtures)
    {
        var window = new ViewerWindow { ShowActivated = false };
        int checks = 0;
        void Check(bool value) { if (!value) throw new IOException($"Automatic quality UI check {checks + 1}"); checks++; }
        window.Loaded += async (_, _) =>
        {
            using var lifetime = new CancellationTokenSource();
            try
            {
                var store = new ViewerPreferencesStore(Path.Combine(Path.GetFullPath(fixtures), Guid.NewGuid().ToString("N"), "viewer.json"));
                window.preferencesStore = store;
                window.SetOptionsExpanded(true); window.connectionOptions.SelectedIndex = 2;
                window.videoFrameRate.SelectedValue = 144; window.videoBitrate.SelectedValue = 100_000_000;
                window.autoConnectionQuality.IsChecked = true;
                await window.preferencesOperation;
                Check(window.videoFrameRate.IsVisible && window.autoConnectionQuality.IsVisible);
                Check(store.Load() is { FramesPerSecond:144, Bitrate:100_000_000, AutomaticVideoQuality:true });
                var clock = new QualityClock(); var host = new HostVideoSettings(clock);
                window.session = lifetime; window.settingsCodec = MediaCodec.H264;
                window.videoSettingsSession = new(VideoSettingsProtocol.Default, clock);
                MediaFrame Frame(int generation) => new(MediaCodec.H264, 1, 0, 1, 1, true, false, 0, 0, [], [1], VideoGeneration:generation);
                window.VideoSettingsPresented(Frame(1));
                Check(!window.restoreVideoPreferences && window.outgoing.Depth == 0); // Initial 30/8, within saved caps.
                window.ShowConnectedView(true); window.RefreshVideoSettingsMenu();
                Check(window.sessionToolbar!.VideoSettingsAction.Items.Count == 3);
                ConnectionQualitySample Sample(int i, double rtt) => new(i * 5, rtt, i * 100, i * 1_000_000, 0, 0);
                window.ObserveAutomaticVideoQuality(Sample(0,20)); window.ObserveAutomaticVideoQuality(Sample(1,150));
                Check(window.outgoing.Depth == 0);
                window.ObserveAutomaticVideoQuality(Sample(2,150));
                Check(window.outgoing.TryRead(out var command) && command?.VideoSettings is { FramesPerSecond:22, Bitrate:6_000_000, Generation:1 });
                host.Request(command!.VideoSettings!);
                window.ObserveAutomaticVideoQuality(Sample(3,150)); Check(window.outgoing.Depth == 0); // Pending barrier.
                host.Commit(host.Pending!);
                window.ReceiveVideoSettings(new("video-settings-state-v2", VideoVersion:1, VideoSettings:host.Current), lifetime.Token);
                window.VideoSettingsPresented(Frame(2)); clock.Advance();
                Check(window.videoSettingsSession.Ready && window.videoSettingsSession.Current == host.Current);
                Check(window.preferences is { FramesPerSecond:144, Bitrate:100_000_000, AutomaticVideoQuality:true });
                Check(Equals(window.videoFrameRate.SelectedValue,144) && Equals(window.videoBitrate.SelectedValue,100_000_000));
                window.RefreshVideoSettingsMenu();
                var fps = (MenuItem)window.sessionToolbar.VideoSettingsAction.Items[0];
                fps.Items.OfType<MenuItem>().Single(item => Equals(item.Header,"60 FPS")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(window.outgoing.TryRead(out command) && command?.VideoSettings is { FramesPerSecond:60, Bitrate:6_000_000, Generation:2 });
                await window.preferencesOperation;
                Check(store.Load() is { FramesPerSecond:60, Bitrate:6_000_000, AutomaticVideoQuality:false });
                Check(window.autoConnectionQuality.IsChecked == false && Equals(window.videoFrameRate.SelectedValue,60));
                window.videoSettingsSession = null;
                window.autoConnectionQuality.IsChecked = true;
                window.ObserveAutomaticVideoQuality(Sample(20,2000)); Check(window.outgoing.Depth == 0); // Older Host.
                window.EndVideoSettings(); Check(window.videoSettingsSession is null);
                Console.WriteLine($"PASS {checks} automatic quality WPF/state checks: Advanced controls/save, bounded auto request, ACK/keyframe/pending barrier, unchanged saved caps, toolbar manual override, old Host and teardown; synthetic metrics, no bandwidth claim");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { lifetime.Cancel(); window.session = null; window.ShowConnectedView(false); window.Close(); }
        };
        app.Run(window);
    }
}
