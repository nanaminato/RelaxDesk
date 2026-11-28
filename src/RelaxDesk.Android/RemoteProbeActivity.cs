using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using System.Security.Authentication;
using Orientation = Android.Widget.Orientation;

namespace RelaxDesk.Mobile;

[Activity(
    Label = "RelaxDesk",
    Exported = false,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode)]
public sealed class RemoteProbeActivity : Activity, ISurfaceHolderCallback
{
    const string LanPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    const int LanRequest = 31;

    LinearLayout root = null!;
    LinearLayout formCard = null!;
    TextView status = null!;
    EditText address = null!, port = null!, pin = null!, user = null!, password = null!;
    Button connect = null!, cancel = null!;
    View form = null!;
    Surface? surface;
    CancellationTokenSource? attempt;
    Task? worker;
    bool foreground, requesting;
    int insetTop, insetBottom, insetLeft, insetRight;

    protected override void OnCreate(Bundle? state)
    {
        base.OnCreate(state);
        AndroidStrings.Init(this);
        AndroidTheme.Init(this);
        Title = AndroidStrings.Get("probe_title");
        Window?.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);

        root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        status = new TextView(this)
        {
            Text = AndroidStrings.Get("probe_intro"),
            TextSize = 14f
        };
        status.SetPadding(Dp(16), Dp(12), Dp(16), Dp(8));
        root.AddView(status);

        var entriesShell = new LinearLayout(this) { Orientation = Orientation.Vertical };
        entriesShell.SetGravity(GravityFlags.CenterHorizontal);
        formCard = new LinearLayout(this) { Orientation = Orientation.Vertical };
        formCard.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));

        address = Field(AndroidStrings.Get("hint_host"));
        port = Field(AndroidStrings.Get("hint_port"), InputTypes.ClassNumber);
        pin = Field(AndroidStrings.Get("probe_hint_pin"));
        user = Field(AndroidStrings.Get("hint_user"));
        password = Field(AndroidStrings.Get("hint_password"), InputTypes.ClassText | InputTypes.TextVariationPassword);

        connect = new Button(this)
        {
            Text = AndroidStrings.Get("probe_connect_btn"),
            Enabled = false,
            TextSize = 14f
        };
        connect.SetAllCaps(false);
        var connectLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(42));
        connectLp.TopMargin = Dp(10);
        connect.LayoutParameters = connectLp;
        connect.Click += (_, _) => Connect();
        formCard.AddView(connect);

        entriesShell.AddView(formCard, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));
        var scroll = new ScrollView(this);
        scroll.AddView(entriesShell);
        form = scroll;
        root.AddView(form, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 3));

        cancel = new Button(this)
        {
            Text = AndroidStrings.Get("disconnect"),
            Visibility = ViewStates.Gone,
            TextSize = 14f
        };
        cancel.SetAllCaps(false);
        var cancelLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(42));
        cancelLp.SetMargins(Dp(16), Dp(6), Dp(16), Dp(6));
        cancel.LayoutParameters = cancelLp;
        cancel.Click += (_, _) => attempt?.Cancel();
        root.AddView(cancel);

        var stage = new FrameLayout(this);
        stage.SetBackgroundColor(Color.Black);
        var video = new SurfaceView(this);
        stage.AddView(video, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        video.Holder!.AddCallback(this);
        root.AddView(stage, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 2));

        root.SetOnApplyWindowInsetsListener(new InsetsListener((top, bottom, left, right) =>
        {
            insetTop = top;
            insetBottom = bottom;
            insetLeft = left;
            insetRight = right;
            ApplyResponsiveLayout();
        }));

        SetContentView(root);
        ApplyTheme();
        ApplyResponsiveLayout();

        EditText Field(string label, InputTypes type = InputTypes.ClassText)
        {
            var field = new EditText(this)
            {
                Hint = label,
                InputType = type,
                SaveEnabled = false,
                ImportantForAutofill = ImportantForAutofill.No
            };
            field.SetSingleLine(true);
            formCard.AddView(field);
            return field;
        }
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyTheme();
        ApplyResponsiveLayout();
    }

    void ApplyTheme()
    {
        var p = AndroidTheme.GetPalette(this);
        AndroidTheme.ApplyWindowChrome(this);

        root.SetBackgroundColor(p.PageBackground);
        status.SetTextColor(p.PrimaryText);
        formCard.Background = AndroidTheme.MakeRounded(p.CardBackground, Dp(12), p.CardStroke, Dp(1));

        foreach (var edit in new[] { address, port, pin, user, password })
        {
            edit.SetTextColor(p.PrimaryText);
            edit.SetHintTextColor(p.HintText);
        }

        connect.SetTextColor(p.AccentText);
        connect.Background = AndroidTheme.MakeRounded(p.AccentBg, Dp(18));

        cancel.SetTextColor(p.DangerText);
        cancel.Background = AndroidTheme.MakeRounded(p.DangerBg, Dp(18));
    }

    void ApplyResponsiveLayout()
    {
        var dm = Resources!.DisplayMetrics!;
        int safeWidth = Math.Max(1, dm.WidthPixels - insetLeft - insetRight);
        int maxCardWidth = Dp(520);
        int cardWidth = safeWidth - Dp(32) > maxCardWidth ? maxCardWidth : ViewGroup.LayoutParams.MatchParent;

        root.SetPadding(insetLeft, insetTop, insetRight, insetBottom);
        var lp = (LinearLayout.LayoutParams)formCard.LayoutParameters!;
        lp.Width = cardWidth;
        lp.SetMargins(Dp(16), Dp(4), Dp(16), Dp(12));
        formCard.LayoutParameters = lp;
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
        status.Text = HasLanPermission()
            ? AndroidStrings.Get("probe_perm_granted")
            : AndroidStrings.Get("probe_perm_denied");
        UpdateActions();
    }

    void Connect()
    {
        if (!foreground || requesting || attempt is not null || surface is not { IsValid: true } output)
            return;
        if (!FirstFrameSession.TryEndpoint(address.Text ?? "", port.Text ?? "", pin.Text ?? "", out var ip,
            out var endpointPort, out var endpointPin))
        {
            status.Text = AndroidStrings.Get("probe_err_endpoint");
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
            status.Text = AndroidStrings.Get("probe_err_user");
            return;
        }
        var run = new CancellationTokenSource();
        attempt = run;
        UpdateActions();
        status.Text = AndroidStrings.Get("probe_connecting");
        worker = Task.Run(async () =>
        {
            string result;
            try
            {
                run.Token.ThrowIfCancellationRequested();
                if (!HasLanPermission()) throw new UnauthorizedAccessException();
                result = await FirstFrameSession.Run(ip, endpointPort, endpointPin, username, credential,
                    AndroidDeviceKey.OpenProbeKey, (frame, ct) => SurfaceProbe.PlayFirst(frame, output, ct), run.Token);
                result += AndroidStrings.Get("probe_done_suffix");
            }
            catch (System.OperationCanceledException) when (run.IsCancellationRequested) { result = AndroidStrings.Get("disconnected"); }
            catch (System.OperationCanceledException) { result = AndroidStrings.Get("probe_timeout"); }
            catch (HostConsentRejectedException) { result = AndroidStrings.Get("err_host_rejected"); }
            catch (PasswordLoginRejectedException) { result = AndroidStrings.Get("err_invalid_credentials"); }
            catch (AuthenticationException) { result = AndroidStrings.Get("probe_auth_failed"); }
            catch (UnauthorizedAccessException) { result = AndroidStrings.Get("probe_lan_unavailable"); }
            catch (global::Android.Media.MediaCodec.CodecException e)
            {
#if DEBUG
                // Codec diagnostics only; never log credentials or frame content.
                global::Android.Util.Log.Error("RelaxDeskCodec", $"{e.DiagnosticInfo} (code {e.ErrorCode}): {e}");
#endif
                result = AndroidStrings.Format("probe_failed", e.GetType().Name);
            }
            catch (Exception e) { result = AndroidStrings.Format("probe_failed", e.GetType().Name); }
            finally { credential = ""; }
            RunOnUiThread(() =>
            {
                if (!ReferenceEquals(attempt, run)) return;
                attempt = null;
                worker = null;
                run.Dispose();
                status.Text = foreground ? result : AndroidStrings.Get("disconnected");
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

    int Dp(int dp) => (int)Math.Round(dp * Resources!.DisplayMetrics!.Density);

    sealed class InsetsListener(Action<int, int, int, int> onInsets) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                onInsets(bars.Top, bars.Bottom, bars.Left, bars.Right);
            }
            return insets;
        }
    }
}
