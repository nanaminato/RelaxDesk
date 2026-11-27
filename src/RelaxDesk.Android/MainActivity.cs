using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;

namespace RelaxDesk.Mobile;

// A local test tool, not a network client. No permissions or background work.
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
