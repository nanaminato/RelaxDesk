using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace RelaxDesk.Mobile;

// Local media and key checks; the separate network probe is user-launched.
[Activity(Label = "RelaxDesk Media Probe", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity, ISurfaceHolderCallback
{
    TextView status = null!;
    Button play = null!;
    Surface? surface;
    CancellationTokenSource? playback;
    Task? worker;
    bool foreground;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        status = new TextView(this) { Text = "本地解码探针，不连接主机" };
        play = new Button(this) { Text = "播放合成测试画面", Enabled = false };
        var stage = new FrameLayout(this);
        var video = new SurfaceView(this);
        stage.AddView(video, new FrameLayout.LayoutParams(1, 1, GravityFlags.Center));
        stage.LayoutChange += (_, _) =>
        {
            int width = Math.Min(stage.Width, stage.Height * 16 / 9);
            if (width > 0)
                video.LayoutParameters = new FrameLayout.LayoutParams(width, width * 9 / 16, GravityFlags.Center);
        };
        video.Holder!.AddCallback(this);
        play.Click += (_, _) => StartPlayback();
        root.AddView(status);
        root.AddView(play);
        var keyProbe = new Button(this) { Text = "本地设备密钥自检" };
        var keyStatus = new TextView(this);
        keyProbe.Click += async (_, _) =>
        {
            keyProbe.Enabled = false;
            keyStatus.Text = "正在验证 Android Keystore…";
            string result;
            try
            {
                await Task.Run(DeviceKeyProbe.Run);
                result = "签名验证通过：同一密钥可重开，证书指纹变化时拒绝。";
            }
            catch (Exception e)
            {
#if DEBUG
                // Fixed local self-test only: no Host, credentials or payloads.
                global::Android.Util.Log.Error("RelaxDeskDeviceKey", e.ToString());
#endif
                result = $"设备密钥自检未通过：{e.GetType().Name}";
            }
            if (!IsDestroyed)
            {
                keyStatus.Text = result;
                keyProbe.Enabled = true;
            }
        };
        root.AddView(keyProbe);
        root.AddView(keyStatus);
        var liveSession = new Button(this) { Text = "连接 Windows 远程桌面（持续会话）" };
        liveSession.Click += (_, _) => StartActivity(typeof(RemoteSessionActivity));
        root.AddView(liveSession);
        var remote = new Button(this) { Text = "局域网首帧连接探针" };
        remote.Click += (_, _) => StartActivity(typeof(RemoteProbeActivity));
        root.AddView(remote);
        root.AddView(stage, new LinearLayout.LayoutParams(-1, 0, 1));
        SetContentView(root);
    }

    protected override void OnStart()
    {
        base.OnStart();
        foreground = true;
        UpdatePlay();
    }

    protected override void OnStop()
    {
        foreground = false;
        StopPlayback();
        base.OnStop();
    }

    public void SurfaceCreated(ISurfaceHolder holder) { surface = holder.Surface; UpdatePlay(); }
    public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) { }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        surface = null;
        StopPlayback();
    }

    void UpdatePlay() => play.Enabled = foreground && surface?.IsValid == true && playback is null;

    void StartPlayback()
    {
        if (!foreground || surface is not { IsValid: true } output || playback is not null) return;
        var run = new CancellationTokenSource();
        playback = run;
        UpdatePlay();
        status.Text = "正在解码测试画面…";
        worker = Task.Run(async () =>
        {
            string result;
            try
            {
                using var frames = Assets!.Open("probe.rdm2");
                result = await SurfaceProbe.Play(frames, output, run.Token);
            }
            catch (System.OperationCanceledException) when (run.IsCancellationRequested) { return; }
            catch (Exception e) { result = $"解码未通过：{e.GetType().Name} – {e.Message}"; }
            RunOnUiThread(() =>
            {
                if (!ReferenceEquals(playback, run)) return;
                playback = null;
                worker = null;
                run.Dispose();
                status.Text = result;
                UpdatePlay();
            });
        });
    }

    // SurfaceHolder requires the decoder to stop touching its Surface before
    // SurfaceDestroyed returns. Dequeue polls, presentation waits and EOS
    // deadlines in the worker are bounded and the UI is never awaited there.
    void StopPlayback()
    {
        var run = playback;
        playback = null;
        run?.Cancel();
        worker?.GetAwaiter().GetResult();
        worker = null;
        run?.Dispose();
        UpdatePlay();
    }
}
