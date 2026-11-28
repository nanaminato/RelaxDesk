using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;
using System.Security.Authentication;

namespace RelaxDesk.Mobile;

[Activity(
    Label = "RelaxDesk 远程桌面",
    Exported = false,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class RemoteSessionActivity : Activity, ISurfaceHolderCallback
{
    const string LanPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    const int LanRequest = 32;

    readonly ViewportMapper viewport = new();
    RemoteDesktopSession? session;
    CancellationTokenSource? sessionCts;
    Task? sessionWorker;

    View formPanel = null!;
    FrameLayout stage = null!;
    SurfaceView videoSurface = null!;
    RemoteCursorView cursorView = null!;
    TextView statusText = null!;
    TextView hudText = null!;
    LinearLayout toolbar = null!;
    EditText hostInput = null!, portInput = null!, pinInput = null!, userInput = null!, passInput = null!;
    Button connectBtn = null!, modeBtn = null!;

    Surface? activeSurface;
    bool foreground;
    bool requestingPermission;
    bool touchpadMode = true;
    bool showHud = true;
    float lastTouchX, lastTouchY;
    float prevPinchDist;
    bool isPinching;
    long touchDownTimeMs;
    float touchDownX, touchDownY;
    bool movedSinceDown;
    Handler? uiHandler;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn,
                        WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn);

        uiHandler = new Handler(Looper.MainLooper!);

        var root = new FrameLayout(this) { Background = new global::Android.Graphics.Drawables.ColorDrawable(Color.Black) };

        // Video stage + independent cursor overlay
        stage = new FrameLayout(this);
        videoSurface = new SurfaceView(this);
        videoSurface.Holder!.AddCallback(this);
        stage.AddView(videoSurface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        cursorView = new RemoteCursorView(this);
        stage.AddView(cursorView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        stage.LayoutChange += (_, _) =>
        {
            viewport.SetViewSize(stage.Width, stage.Height);
            UpdateVideoSurfaceLayout();
        };
        stage.Touch += OnStageTouch;
        stage.GenericMotion += OnStageGenericMotion;
        root.AddView(stage, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        // Telemetry HUD
        hudText = new TextView(this)
        {
            TextSize = 11f,
            Visibility = ViewStates.Gone
        };
        hudText.SetTextColor(Color.Argb(230, 120, 255, 160));
        hudText.SetBackgroundColor(Color.Argb(150, 0, 0, 0));
        hudText.SetPadding(12, 6, 12, 6);
        root.AddView(hudText, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Start));

        // Floating session toolbar
        toolbar = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            Visibility = ViewStates.Gone
        };
        toolbar.SetBackgroundColor(Color.Argb(170, 20, 24, 30));
        modeBtn = MakeToolbarButton("触控板", () =>
        {
            touchpadMode = !touchpadMode;
            modeBtn.Text = touchpadMode ? "触控板" : "直触";
        });
        toolbar.AddView(modeBtn);
        toolbar.AddView(MakeToolbarButton("文字", ShowTextInputDialog));
        toolbar.AddView(MakeToolbarButton("重置缩放", () => { viewport.ResetTransform(); UpdateVideoSurfaceLayout(); }));
        toolbar.AddView(MakeToolbarButton("指标", () =>
        {
            showHud = !showHud;
            hudText.Visibility = showHud && session is not null ? ViewStates.Visible : ViewStates.Gone;
        }));
        toolbar.AddView(MakeToolbarButton("断开", Disconnect));
        root.AddView(toolbar, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.End));

        // Connection form overlay
        formPanel = BuildConnectionForm();
        root.AddView(formPanel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        SetContentView(root);
        LoadLatestHost();
    }

    View BuildConnectionForm()
    {
        var scroll = new ScrollView(this);
        scroll.SetBackgroundColor(Color.Argb(245, 18, 22, 28));
        var box = new LinearLayout(this) { Orientation = Orientation.Vertical };
        box.SetPadding(36, 28, 36, 28);

        statusText = new TextView(this) { Text = "连接到 Windows 被控端（TCP+TLS 加密）", TextSize = 15f };
        statusText.SetTextColor(Color.White);
        box.AddView(statusText);

        hostInput = MakeField(box, "局域网 IP（如 192.168.1.10）");
        portInput = MakeField(box, "端口（默认 48200）", InputTypes.ClassNumber);
        portInput.Text = "48200";
        pinInput = MakeField(box, "64 位 SHA-256 证书指纹");
        userInput = MakeField(box, "Windows 端登录用户名");
        passInput = MakeField(box, "Windows 端登录密码（不保存）", InputTypes.ClassText | InputTypes.TextVariationPassword);

        connectBtn = new Button(this) { Text = "连接远程桌面" };
        connectBtn.Click += (_, _) => StartConnect();
        box.AddView(connectBtn);

        scroll.AddView(box);
        return scroll;
    }

    EditText MakeField(LinearLayout parent, string hint, InputTypes type = InputTypes.ClassText)
    {
        var edit = new EditText(this)
        {
            Hint = hint,
            InputType = type,
            SaveEnabled = false,
            ImportantForAutofill = ImportantForAutofill.No
        };
        edit.SetSingleLine(true);
        edit.SetTextColor(Color.White);
        edit.SetHintTextColor(Color.Argb(160, 200, 210, 220));
        parent.AddView(edit);
        return edit;
    }

    Button MakeToolbarButton(string title, Action onClick)
    {
        var btn = new Button(this) { Text = title, TextSize = 12f };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    void LoadLatestHost()
    {
        var saved = ConnectionStore.Load(this);
        if (saved.Count == 0) return;
        var latest = saved[0];
        hostInput.Text = latest.Host;
        portInput.Text = latest.Port.ToString();
        pinInput.Text = latest.Fingerprint;
        userInput.Text = latest.Username;
    }

    bool HasLanPermission() =>
        (int)Build.VERSION.SdkInt < 37 || CheckSelfPermission(LanPermission) == Permission.Granted;

    void StartConnect()
    {
        if (!foreground || requestingPermission || session is not null) return;
        if (!FirstFrameSession.TryEndpoint(hostInput.Text ?? "", portInput.Text ?? "", pinInput.Text ?? "",
                out var ip, out var port, out var pin))
        {
            statusText.Text = "请输入有效的局域网 IP、端口和 64 位 SHA-256 证书指纹。";
            return;
        }
        if (!HasLanPermission())
        {
            passInput.Text = "";
            requestingPermission = true;
            RequestPermissions([LanPermission], LanRequest);
            return;
        }

        string username = userInput.Text ?? "";
        string password = passInput.Text ?? "";
        passInput.Text = "";
        if (!PasswordVerifier.ValidUserName(username) || password.Length == 0)
        {
            statusText.Text = "请输入被控端用户名和密码。";
            return;
        }

        ConnectionStore.SaveOrUpdate(this, new SavedHostEntry(ip, ip, port, pin, username, DateTime.UtcNow.Ticks));

        var cts = new CancellationTokenSource();
        var activeSession = new RemoteDesktopSession();
        sessionCts = cts;
        session = activeSession;

        activeSession.StatusChanged += msg => RunOnUiThread(() => statusText.Text = msg);
        activeSession.VideoSizeChanged += (w, h) => RunOnUiThread(() =>
        {
            viewport.SetVideoSize(w, h);
            UpdateVideoSurfaceLayout();
        });
        activeSession.PointerUpdated += ptr => RunOnUiThread(() =>
        {
            cursorView.ApplyRemoteState(ptr);
            if (!movedSinceDown)
            {
                viewport.SyncRemotePointer(ptr.X, ptr.Y);
                if (viewport.TryNormalizedToView(viewport.CursorNormalizedX, viewport.CursorNormalizedY, out float vx, out float vy))
                    cursorView.UpdateLocalPosition(vx, vy);
            }
        });

        formPanel.Visibility = ViewStates.Gone;
        toolbar.Visibility = ViewStates.Visible;
        hudText.Visibility = showHud ? ViewStates.Visible : ViewStates.Gone;
        ScheduleHudTick();

        sessionWorker = Task.Run(async () =>
        {
            string endMessage = "已断开连接。";
            try
            {
                await activeSession.RunAsync(ip, port, pin, username, password,
                    AndroidDeviceKey.OpenProbeKey, activeSurface, cts.Token);
            }
            catch (System.OperationCanceledException) when (cts.IsCancellationRequested) { endMessage = "已断开连接。"; }
            catch (HostConsentRejectedException) { endMessage = "被控端拒绝了连接请求。"; }
            catch (PasswordLoginRejectedException) { endMessage = "用户名或密码未通过验证。"; }
            catch (AuthenticationException) { endMessage = "TLS 认证失败，请核对证书指纹。"; }
            catch (Exception ex) { endMessage = $"会话结束：{ex.GetType().Name} - {ex.Message}"; }
            finally { password = ""; }

            RunOnUiThread(() =>
            {
                if (!ReferenceEquals(session, activeSession)) return;
                activeSession.Dispose();
                session = null;
                sessionCts?.Dispose();
                sessionCts = null;
                sessionWorker = null;
                toolbar.Visibility = ViewStates.Gone;
                hudText.Visibility = ViewStates.Gone;
                formPanel.Visibility = ViewStates.Visible;
                statusText.Text = endMessage;
            });
        });
    }

    void Disconnect()
    {
        session?.ReleaseAllInputs();
        sessionCts?.Cancel();
    }

    void ScheduleHudTick()
    {
        uiHandler?.PostDelayed(() =>
        {
            if (session is null || IsDestroyed) return;
            if (showHud) hudText.Text = session.Telemetry.FormatHud();
            ScheduleHudTick();
        }, 500);
    }

    void UpdateVideoSurfaceLayout()
    {
        if (!viewport.TryGetFittedRect(out float left, out float top, out float width, out float height))
            return;
        var lp = new FrameLayout.LayoutParams(Math.Max(1, (int)Math.Round(width)), Math.Max(1, (int)Math.Round(height)));
        lp.LeftMargin = (int)Math.Round(left);
        lp.TopMargin = (int)Math.Round(top);
        videoSurface.LayoutParameters = lp;
    }

    void OnStageTouch(object? sender, View.TouchEventArgs e)
    {
        var ev = e.Event;
        if (ev is null || session is null) return;
        e.Handled = true;

        int pointers = ev.PointerCount;
        var action = ev.ActionMasked;

        if (pointers >= 2)
        {
            float dx = ev.GetX(0) - ev.GetX(1);
            float dy = ev.GetY(0) - ev.GetY(1);
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float midX = (ev.GetX(0) + ev.GetX(1)) * 0.5f;
            float midY = (ev.GetY(0) + ev.GetY(1)) * 0.5f;

            if (!isPinching)
            {
                isPinching = true;
                prevPinchDist = Math.Max(1f, dist);
                lastTouchX = midX;
                lastTouchY = midY;
            }
            else if (action == MotionEventActions.Move)
            {
                float scale = dist / Math.Max(1f, prevPinchDist);
                viewport.ApplyScaleAndPan(scale, midX, midY, midX - lastTouchX, midY - lastTouchY);
                prevPinchDist = Math.Max(1f, dist);
                lastTouchX = midX;
                lastTouchY = midY;
                UpdateVideoSurfaceLayout();
            }
            return;
        }

        if (isPinching)
        {
            if (action is MotionEventActions.Up or MotionEventActions.Cancel) isPinching = false;
            return;
        }

        float x = ev.GetX();
        float y = ev.GetY();

        switch (action)
        {
            case MotionEventActions.Down:
                touchDownTimeMs = Java.Lang.JavaSystem.CurrentTimeMillis();
                touchDownX = lastTouchX = x;
                touchDownY = lastTouchY = y;
                movedSinceDown = false;
                if (!touchpadMode && viewport.TryViewToNormalized(x, y, true, out int nx, out int ny))
                {
                    cursorView.UpdateLocalPosition(x, y);
                    session.SendInput(new RemoteInput("move", X: nx, Y: ny));
                    session.SendInput(new RemoteInput("button", Button: 1, Up: false));
                }
                break;

            case MotionEventActions.Move:
                float mdx = x - lastTouchX;
                float mdy = y - lastTouchY;
                if (!movedSinceDown && MathF.Hypot(x - touchDownX, y - touchDownY) > 10f)
                    movedSinceDown = true;
                lastTouchX = x;
                lastTouchY = y;

                if (touchpadMode)
                {
                    if (viewport.TryApplyRelativeDelta(mdx, mdy, 1.35f, out int nx, out int ny))
                    {
                        if (viewport.TryNormalizedToView(nx, ny, out float vx, out float vy))
                            cursorView.UpdateLocalPosition(vx, vy);
                        session.SendInput(new RemoteInput("move", X: nx, Y: ny));
                    }
                }
                else if (viewport.TryViewToNormalized(x, y, true, out int nx, out int ny))
                {
                    cursorView.UpdateLocalPosition(x, y);
                    session.SendInput(new RemoteInput("move", X: nx, Y: ny));
                }
                break;

            case MotionEventActions.Up:
                if (touchpadMode)
                {
                    long heldMs = Java.Lang.JavaSystem.CurrentTimeMillis() - touchDownTimeMs;
                    if (!movedSinceDown)
                    {
                        int button = heldMs >= 450 ? 2 : 1; // Long-press = right click, tap = left click
                        session.SendInput(new RemoteInput("button", Button: button, Up: false));
                        session.SendInput(new RemoteInput("button", Button: button, Up: true));
                    }
                }
                else
                {
                    session.SendInput(new RemoteInput("button", Button: 1, Up: true));
                }
                movedSinceDown = false;
                break;

            case MotionEventActions.Cancel:
                session.ReleaseAllInputs();
                movedSinceDown = false;
                break;
        }
    }

    void OnStageGenericMotion(object? sender, View.GenericMotionEventArgs e)
    {
        var ev = e.Event;
        if (ev is null || session is null) return;

        if (ev.Action == MotionEventActions.HoverMove)
        {
            if (viewport.TryViewToNormalized(ev.GetX(), ev.GetY(), false, out int nx, out int ny))
            {
                cursorView.UpdateLocalPosition(ev.GetX(), ev.GetY());
                session.SendInput(new RemoteInput("move", X: nx, Y: ny));
                e.Handled = true;
            }
        }
        else if (ev.Action == MotionEventActions.Scroll)
        {
            int wheelY = (int)Math.Round(ev.GetAxisValue(Axis.Vscroll) * 120f);
            int wheelX = (int)Math.Round(ev.GetAxisValue(Axis.Hscroll) * 120f);
            if (wheelX != 0 || wheelY != 0)
            {
                session.SendInput(new RemoteInput("wheel", WheelX: wheelX, WheelY: wheelY));
                e.Handled = true;
            }
        }
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (session is not null && AndroidKeyMapper.TryMap(keyCode, out int vk, out int scan, out bool ext))
        {
            session.SendInput(new RemoteInput("key", VirtualKey: vk, ScanCode: scan, Extended: ext, Up: false, Repeat: (e?.RepeatCount ?? 0) > 0));
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (session is not null && AndroidKeyMapper.TryMap(keyCode, out int vk, out int scan, out bool ext))
        {
            session.SendInput(new RemoteInput("key", VirtualKey: vk, ScanCode: scan, Extended: ext, Up: true));
            return true;
        }
        return base.OnKeyUp(keyCode, e);
    }

    void ShowTextInputDialog()
    {
        if (session is null) return;
        var input = new EditText(this) { Hint = "输入要发送到 Windows 的文字" };
        new AlertDialog.Builder(this)
            .SetTitle("发送文字")!
            .SetView(input)!
            .SetPositiveButton("发送", (_, _) =>
            {
                string text = input.Text ?? "";
                if (InputProtocol.TextValid(text))
                    session?.SendInput(new RemoteInput("text", Text: text));
            })!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();
    }

    protected override void OnStart()
    {
        base.OnStart();
        foreground = true;
    }

    protected override void OnStop()
    {
        foreground = false;
        requestingPermission = false;
        passInput.Text = "";
        session?.ReleaseAllInputs();
        session?.AttachSurface(null);
        base.OnStop();
    }

    protected override void OnDestroy()
    {
        Disconnect();
        base.OnDestroy();
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != LanRequest || !requestingPermission) return;
        requestingPermission = false;
        statusText.Text = HasLanPermission() ? "局域网权限已授予，请重新输入密码并点击连接。" : "未获得局域网权限，无法连接。";
    }

    public void SurfaceCreated(ISurfaceHolder holder)
    {
        activeSurface = holder.Surface;
        session?.AttachSurface(activeSurface);
    }

    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }

    public void SurfaceDestroyed(ISurfaceHolder holder)
    {
        activeSurface = null;
        session?.AttachSurface(null);
    }
}
