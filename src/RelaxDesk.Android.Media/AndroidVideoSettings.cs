using RelaxDesk.Core;

namespace RelaxDesk.Mobile.Media;

public sealed record AndroidVideoPreferences(int FramesPerSecond = 30, int Bitrate = 8_000_000, bool Automatic = false)
{
    public AndroidVideoPreferences Validate()
    {
        VideoSettingsProtocol.Validate(new(FramesPerSecond, Bitrate, 1));
        return this;
    }
}

// Serializes UI changes, protocol acknowledgements and decoder presentation.
// A desired change waits for presentation + acknowledgement + protocol cooldown.
public sealed class AndroidVideoSettings(VideoSettings initial, AndroidVideoPreferences preferences,
    Action<Packet> send, TimeProvider? clock = null)
{
    readonly object sync = new();
    readonly ViewerVideoSettings model = new(initial, clock);
    AndroidVideoPreferences desired = preferences.Validate();
    AutomaticVideoQuality automatic = new();
    bool restore = true;

    public VideoSettings Current { get { lock (sync) return model.Current; } }
    public bool Pending { get { lock (sync) return restore || model.Pending; } }
    public void Configure(AndroidVideoPreferences value)
    {
        value.Validate();
        lock (sync) { desired = value; automatic = new(); restore = true; Pump(); }
    }
    public bool Accept(MediaFrame frame) { lock (sync) return model.Accept(frame); }
    public void Presented(MediaFrame frame) { lock (sync) { model.Presented(frame); } }
    public void Receive(Packet packet)
    {
        lock (sync)
        {
            if (packet.VideoSettings is null || packet != new Packet("video-settings-state-v2",
                VideoVersion: VideoSettingsProtocol.Version, VideoSettings: packet.VideoSettings))
                throw new InvalidDataException("Invalid video settings acknowledgement");
            model.Receive(packet.VideoSettings);
        }
    }
    public void Pump()
    {
        lock (sync)
        {
            if (!restore || !model.Ready) return;
            var target = desired.Automatic
                ? AutomaticVideoQuality.Initial(desired.FramesPerSecond, desired.Bitrate, model.Current.Generation)
                : new VideoSettings(desired.FramesPerSecond, desired.Bitrate, model.Current.Generation);
            Request(target);
            restore = false;
        }
    }
    public void Observe(ConnectionQualitySample sample)
    {
        lock (sync)
        {
            if (!desired.Automatic || restore || !model.Ready) return;
            var target = automatic.Observe(sample, model.Current, desired.FramesPerSecond, desired.Bitrate);
            if (target is not null) Request(target);
        }
    }
    void Request(VideoSettings target)
    {
        if (target.FramesPerSecond == model.Current.FramesPerSecond && target.Bitrate == model.Current.Bitrate) return;
        send(new Packet("video-settings-v2", VideoVersion: VideoSettingsProtocol.Version,
            VideoSettings: model.Request(target.FramesPerSecond, target.Bitrate)));
    }
}
