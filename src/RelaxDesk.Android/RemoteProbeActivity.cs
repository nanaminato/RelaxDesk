using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using System.Security.Authentication;

namespace RelaxDesk.Mobile;

[Activity(Label = "RelaxDesk 首帧连接探针", Exported = false)]
public sealed class RemoteProbeActivity : Activity, ISurfaceHolderCallback
{
    const string LanPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    const int LanRequest = 31;
    TextView status = null!;
    EditText address = null!, port = null!, pin = null!, user = null!, password = null!;
    Button connect = null!, cancel = null!;
    View form = null!;
    Surface? surface;
    CancellationTokenSource? attempt;
    Task? worker;
    bool foreground, requesting;

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        status = new TextView(this) { Text = "仅实测首帧。请从被控端独立核对证书指纹。" };
        root.AddView(status);
        var entries = new LinearLayout(this) { Orientation = Orientation.Vertical };
        address = Field("局域网 IP 地址");
        port = Field("端口", InputTypes.ClassNumber);
        pin = Field("64 位 SHA-256 证书指纹");
        user = Field("被控端登录用户名");
        password = Field("被控端登录密码", InputTypes.ClassText | InputTypes.TextVariationPassword);
        connect = new Button(this) { Text = "连接并试解首帧", Enabled = false };
        connect.Click += (_, _) => Connect();
        entries.AddView(connect);
        var scroll = new ScrollView(this);
        scroll.AddView(entries);
        form = scroll;
        root.AddView(form, new LinearLayout.LayoutParams(-1, 0, 3));
        cancel = new Button(this) { Text = "断开", Visibility = ViewStates.Gone };
        cancel.Click += (_, _) => attempt?.Cancel();
        root.AddView(cancel);
        var stage = new FrameLayout(this);
        var video = new SurfaceView(this);
        stage.AddView(video, new FrameLayout.LayoutParams(-1, -1));
        video.Holder!.AddCallback(this);
        root.AddView(stage, new LinearLayout.LayoutParams(-1, 0, 2));
        SetContentView(root);

        EditText Field(string label, InputTypes type = InputTypes.ClassText)
        {
            var field = new EditText(this) { Hint = label, InputType = type, SaveEnabled = false, ImportantForAutofill = ImportantForAutofill.No };
            field.SetSingleLine(true);
            entries.AddView(field);
            return field;
        }
    }

    protected override void OnStart()
    {
        base.OnStart();
        foreground = true;
        UpdateActions();
    }

    protected override void OnStop()
    {
        foreground = false;
        requesting = false;
        password.Text = "";
        attempt?.Cancel();
        UpdateActions();
        base.OnStop();
    }

    bool HasLanPermission() =>
        (int)Build.VERSION.SdkInt < 37 || CheckSelfPermission(LanPermission) == Permission.Granted;

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != LanRequest || !requesting) return;
        requesting = false;
        if (!foreground) return;
        // Never resume an old request from a permission callback.
        status.Text = HasLanPermission() ? "局域网权限已授权，请重新输入密码并点击连接。" : "未获局域网权限，未连接。";
        UpdateActions();
    }

    void Connect()
    {
        if (!foreground || requesting || attempt is not null || surface is not { IsValid: true } output)
            return;
        if (!FirstFrameSession.TryEndpoint(address.Text ?? "", port.Text ?? "", pin.Text ?? "", out var ip,
            out var endpointPort, out var endpointPin))
        {
            status.Text = "请输入局域网 IP、端口和已核对的 SHA-256 证书指纹。";
            return;
        }
        if (!HasLanPermission())
        {
            password.Text = "";
            requesting = true;
            UpdateActions();
            RequestPermissions([LanPermission], LanRequest);
            return;
        }
        string username = user.Text ?? "";
        string credential = password.Text ?? "";
        password.Text = "";
        if (!PasswordVerifier.ValidUserName(username) || credential.Length == 0)
        {
            status.Text = "请输入被控端用户名和密码。";
            return;
        }
        var run = new CancellationTokenSource();
        attempt = run;
        UpdateActions();
        status.Text = "正在连接，若被控端提示请在 Windows 上批准。";
        worker = Task.Run(async () =>
        {
            string result;
            try
            {
                run.Token.ThrowIfCancellationRequested();
                if (!HasLanPermission()) throw new UnauthorizedAccessException();
                result = await FirstFrameSession.Run(ip, endpointPort, endpointPin, username, credential,
                    AndroidDeviceKey.OpenProbeKey, (frame, ct) => SurfaceProbe.PlayFirst(frame, output, ct), run.Token);
                result += "。探针已断开。";
            }
            catch (System.OperationCanceledException) when (run.IsCancellationRequested) { result = "已断开。"; }
            catch (System.OperationCanceledException) { result = "连接或首帧超时，已断开。"; }
            catch (HostConsentRejectedException) { result = "被控端未批准。"; }
            catch (PasswordLoginRejectedException) { result = "被控端未接受登录。"; }
            catch (AuthenticationException) { result = "认证失败，请检查被控端状态及证书指纹。"; }
            catch (UnauthorizedAccessException) { result = "局域网权限不可用，未连接。"; }
            catch (global::Android.Media.MediaCodec.CodecException e)
            {
#if DEBUG
                // Codec diagnostics only; never log credentials or frame content.
                global::Android.Util.Log.Error("RelaxDeskCodec", e.ToString());
#endif
                result = $"首帧探针未通过：{e.GetType().Name}";
            }
            catch (Exception e) { result = $"首帧探针未通过：{e.GetType().Name}"; }
            finally { credential = ""; }
            RunOnUiThread(() =>
            {
                if (!ReferenceEquals(attempt, run)) return;
                attempt = null;
                worker = null;
                run.Dispose();
                status.Text = foreground ? result : "已断开。";
                UpdateActions();
            });
        });
    }

    void UpdateActions()
    {
        form.Visibility = attempt is null ? ViewStates.Visible : ViewStates.Gone;
        cancel.Visibility = attempt is null ? ViewStates.Gone : ViewStates.Visible;
        connect.Enabled = foreground && surface?.IsValid == true && attempt is null && !requesting;
    }

    public void SurfaceCreated(ISurfaceHolder holder) { surface = holder.Surface; UpdateActions(); }
    public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) { }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        surface = null;
        attempt?.Cancel();
        // The owner must stop touching this Surface before the callback
        // returns; network reads share cancellation and no worker awaits UI.
        worker?.GetAwaiter().GetResult();
        UpdateActions();
    }
}
