using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;
using System.Security.Authentication;
using CoreRemoteInput = RelaxDesk.Core.RemoteInput;

namespace RelaxDesk.Mobile;

/// <summary>
/// Microsoft Remote Desktop (Windows App) inspired immersive full-screen session activity
/// with a collapsible top-center session pill bar and sticky-modifier extra keys bar.
/// </summary>
[Activity(
    Label = "RelaxDesk 远程桌面",
    Exported = false,
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class RemoteSessionActivity : Activity, ISurfaceHolderCallback
{
    public const string ExtraName = "rd_name";
    public const string ExtraHost = "rd_host";
    public const string ExtraPort = "rd_port";
    public const string ExtraPin = "rd_pin";
    public const string ExtraUser = "rd_user";

    const string LanPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    const int LanRequest = 32;

    static readonly Color AccentBlue = Color.Rgb(0, 120, 212);
    static readonly Color PillBg = Color.Argb(205, 24, 28, 36);
    static readonly Color KeyBg = Color.Rgb(46, 52, 64);
    static readonly Color KeyActiveBg = Color.Rgb(0, 120, 212);

    readonly ViewportMapper viewport = new();
    RemoteDesktopSession? session;
    CancellationTokenSource? sessionCts;
    Task? sessionWorker;

    View connectOverlay = null!;
    FrameLayout stage = null!;
    SurfaceView videoSurface = null!;
    RemoteCursorView cursorView = null!;
    TextView statusText = null!;
    TextView hudText = null!;
    LinearLayout sessionPill = null!;
    LinearLayout pillButtonsContainer = null!;
    HorizontalScrollView extraKeysBar = null!;
    EditText hostInput = null!, portInput = null!, pinInput = null!, userInput = null!, passInput = null!;
    Button connectBtn = null!, modeBtn = null!, collapseBtn = null!;
    Button ctrlBtn = null!, altBtn = null!, shiftBtn = null!, winBtn = null!;

    Surface? activeSurface;
    bool foreground;
    bool requestingPermission;
    bool touchpadMode = true;
    bool showHud;
    bool pillExpanded = true;
    bool stickyCtrl, stickyAlt, stickyShift, stickyWin;
    float lastTouchX, lastTouchY;
    float prevPinchDist;
    bool isPinching;
    long touchDownTimeMs;
    float touchDownX, touchDownY;
    bool movedSinceDown;
    Handler? uiHandler;
    string pcName = "";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetFlags(WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn,
                        WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn);

        uiHandler = new Handler(Looper.MainLooper!);

        var root = new FrameLayout(this) { Background = new ColorDrawable(Color.Black) };

        // 1. Video stage + independent cursor overlay
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

        // 2. Telemetry HUD (top-left)
        hudText = new TextView(this) { TextSize = 11f, Visibility = ViewStates.Gone };
        hudText.SetTextColor(Color.Argb(235, 130, 255, 170));
        hudText.Background = MakeRounded(Color.Argb(165, 12, 16, 22), Dp(8));
        hudText.SetPadding(Dp(10), Dp(6), Dp(10), Dp(6));
        var hudLp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.Start);
        hudLp.SetMargins(Dp(12), Dp(12), 0, 0);
        root.AddView(hudText, hudLp);

        // 3. Microsoft RD Client style top-center collapsible pill bar
        sessionPill = BuildSessionPillBar();
        var pillLp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Top | GravityFlags.CenterHorizontal);
        pillLp.TopMargin = Dp(8);
        root.AddView(sessionPill, pillLp);

        // 4. Bottom collapsible extra keys bar (Esc, Tab, Ctrl, Alt, Win, Arrows, Del, Text)
        extraKeysBar = BuildExtraKeysBar();
        var keysLp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom);
        root.AddView(extraKeysBar, keysLp);

        // 5. Connection / password prompt overlay
        connectOverlay = BuildConnectionOverlay();
        root.AddView(connectOverlay, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        SetContentView(root);
        PopulateFromIntentOrStore();
        EnterImmersiveMode();
    }

    LinearLayout BuildSessionPillBar()
    {
        var pill = new LinearLayout(this)
        {
            Orientation = Orientation.Horizontal,
            Visibility = ViewStates.Gone
        };
        pill.SetGravity(GravityFlags.CenterVertical);
        pill.Background = MakeRounded(PillBg, Dp(22));
        pill.SetPadding(Dp(6), Dp(4), Dp(6), Dp(4));

        collapseBtn = MakeBarButton("‹", Color.Transparent, () =>
        {
            pillExpanded = !pillExpanded;
            pillButtonsContainer.Visibility = pillExpanded ? ViewStates.Visible : ViewStates.Gone;
            collapseBtn.Text = pillExpanded ? "‹" : "≡";
        });
        pill.AddView(collapseBtn);

        pillButtonsContainer = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        pillButtonsContainer.SetGravity(GravityFlags.CenterVertical);

        modeBtn = MakeBarButton("触控板", KeyBg, () =>
        {
            touchpadMode = !touchpadMode;
            modeBtn.Text = touchpadMode ? "触控板" : "直触";
        });
        pillButtonsContainer.AddView(modeBtn);

        pillButtonsContainer.AddView(MakeBarButton("键盘条", KeyBg, () =>
        {
            extraKeysBar.Visibility = extraKeysBar.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
        }));
        pillButtonsContainer.AddView(MakeBarButton("1:1", KeyBg, () =>
        {
            viewport.ResetTransform();
            UpdateVideoSurfaceLayout();
        }));
        pillButtonsContainer.AddView(MakeBarButton("指标", KeyBg, () =>
        {
            showHud = !showHud;
            hudText.Visibility = showHud && session is not null ? ViewStates.Visible : ViewStates.Gone;
        }));
        pillButtonsContainer.AddView(MakeBarButton("断开", Color.Rgb(180, 45, 45), Disconnect));

        pill.AddView(pillButtonsContainer);
        return pill;
    }

    HorizontalScrollView BuildExtraKeysBar()
    {
        var scroll = new HorizontalScrollView(this)
        {
            Visibility = ViewStates.Gone,
            HorizontalScrollBarEnabled = false
        };
        scroll.SetBackgroundColor(Color.Argb(220, 20, 24, 32));

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetPadding(Dp(8), Dp(6), Dp(8), Dp(6));

        row.AddView(MakeBarButton("Esc", KeyBg, () => SendTapKey(0x1B, 0x01, false)));
        row.AddView(MakeBarButton("Tab", KeyBg, () => SendTapKey(0x09, 0x0F, false)));

        ctrlBtn = MakeBarButton("Ctrl", KeyBg, () => ToggleModifier(ref stickyCtrl, ctrlBtn, 0xA2, 0x1D, false));
        altBtn = MakeBarButton("Alt", KeyBg, () => ToggleModifier(ref stickyAlt, altBtn, 0xA4, 0x38, false));
        shiftBtn = MakeBarButton("Shift", KeyBg, () => ToggleModifier(ref stickyShift, shiftBtn, 0xA0, 0x2A, false));
        winBtn = MakeBarButton("Win", KeyBg, () => ToggleModifier(ref stickyWin, winBtn, 0x5B, 0x5B, true));
        row.AddView(ctrlBtn);
        row.AddView(altBtn);
        row.AddView(shiftBtn);
        row.AddView(winBtn);

        row.AddView(MakeBarButton("←", KeyBg, () => SendTapKey(0x25, 0x4B, true)));
        row.AddView(MakeBarButton("↑", KeyBg, () => SendTapKey(0x26, 0x48, true)));
        row.AddView(MakeBarButton("↓", KeyBg, () => SendTapKey(0x28, 0x50, true)));
        row.AddView(MakeBarButton("→", KeyBg, () => SendTapKey(0x27, 0x4D, true)));
        row.AddView(MakeBarButton("Del", KeyBg, () => SendTapKey(0x2E, 0x53, true)));
        row.AddView(MakeBarButton("Win+D", KeyBg, () =>
        {
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x5B, ScanCode: 0x5B, Extended: true, Up: false));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x44, ScanCode: 0x20, Extended: false, Up: false));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x44, ScanCode: 0x20, Extended: false, Up: true));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x5B, ScanCode: 0x5B, Extended: true, Up: true));
        }));
        row.AddView(MakeBarButton("输入文字", AccentBlue, ShowTextInputDialog));

        scroll.AddView(row);
        return scroll;
    }

    View BuildConnectionOverlay()
    {
        var backdrop = new FrameLayout(this);
        backdrop.SetBackgroundColor(Color.Argb(245, 16, 19, 24));

        var scroll = new ScrollView(this);
        var card = new LinearLayout(this) { Orientation = Orientation.Vertical };
        card.Background = MakeRounded(Color.Rgb(28, 32, 40), Dp(14));
        card.SetPadding(Dp(24), Dp(20), Dp(24), Dp(20));

        statusText = new TextView(this) { Text = "输入 Windows 密码以连接（不保存密码）", TextSize = 15f };
        statusText.SetTextColor(Color.White);
        statusText.SetPadding(0, 0, 0, Dp(8));
        card.AddView(statusText);

        hostInput = MakeField(card, "局域网 IP");
        portInput = MakeField(card, "端口（默认 48200）", InputTypes.ClassNumber);
        pinInput = MakeField(card, "64 位 SHA-256 证书指纹");
        userInput = MakeField(card, "Windows 登录用户名");
        passInput = MakeField(card, "Windows 登录密码", InputTypes.ClassText | InputTypes.TextVariationPassword);

        var actions = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        actions.SetPadding(0, Dp(12), 0, 0);

        var backBtn = MakeBarButton("返回列表", KeyBg, Finish);
        actions.AddView(backBtn, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        connectBtn = MakeBarButton("连接桌面", AccentBlue, StartConnect);
        var connLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 2f);
        connLp.LeftMargin = Dp(10);
        actions.AddView(connectBtn, connLp);

        card.AddView(actions);
        scroll.AddView(card);

        var scrollLp = new FrameLayout.LayoutParams(Dp(460), ViewGroup.LayoutParams.WrapContent, GravityFlags.Center);
        backdrop.AddView(scroll, scrollLp);
        return backdrop;
    }

    void PopulateFromIntentOrStore()
    {
        string? intentHost = Intent?.GetStringExtra(ExtraHost);
        if (!string.IsNullOrWhiteSpace(intentHost))
        {
            pcName = Intent?.GetStringExtra(ExtraName) ?? intentHost;
            hostInput.Text = intentHost;
            portInput.Text = (Intent?.GetIntExtra(ExtraPort, 48200) ?? 48200).ToString();
            pinInput.Text = Intent?.GetStringExtra(ExtraPin) ?? "";
            userInput.Text = Intent?.GetStringExtra(ExtraUser) ?? "";
            statusText.Text = $"连接到 {pcName}（请输入密码）";
            passInput.RequestFocus();
            return;
        }

        var saved = ConnectionStore.Load(this);
        if (saved.Count > 0)
        {
            var latest = saved[0];
            pcName = latest.Name;
            hostInput.Text = latest.Host;
            portInput.Text = latest.Port.ToString();
            pinInput.Text = latest.Fingerprint;
            userInput.Text = latest.Username;
        }
        else
        {
            portInput.Text = "48200";
        }
    }

    void EnterImmersiveMode()
    {
        if (Window is null) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var controller = Window.InsetsController;
            if (controller is not null)
            {
                controller.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
            }
        }
    }

    void ToggleModifier(ref bool state, Button btn, int vk, int scan, bool ext)
    {
        if (session is null) return;
        state = !state;
        btn.Background = MakeRounded(state ? KeyActiveBg : KeyBg, Dp(14));
        session.SendInput(new CoreRemoteInput("key", VirtualKey: vk, ScanCode: scan, Extended: ext, Up: !state));
    }

    void SendTapKey(int vk, int scan, bool ext)
    {
        if (session is null) return;
        session.SendInput(new CoreRemoteInput("key", VirtualKey: vk, ScanCode: scan, Extended: ext, Up: false));
        session.SendInput(new CoreRemoteInput("key", VirtualKey: vk, ScanCode: scan, Extended: ext, Up: true));
        ReleaseStickyModifiers();
    }

    void ReleaseStickyModifiers()
    {
        if (stickyCtrl) ToggleModifier(ref stickyCtrl, ctrlBtn, 0xA2, 0x1D, false);
        if (stickyAlt) ToggleModifier(ref stickyAlt, altBtn, 0xA4, 0x38, false);
        if (stickyShift) ToggleModifier(ref stickyShift, shiftBtn, 0xA0, 0x2A, false);
        if (stickyWin) ToggleModifier(ref stickyWin, winBtn, 0x5B, 0x5B, true);
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

        string displayName = string.IsNullOrWhiteSpace(pcName) ? ip : pcName;
        ConnectionStore.SaveOrUpdate(this, new SavedHostEntry(displayName, ip, port, pin, username, DateTime.UtcNow.Ticks));

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

        connectOverlay.Visibility = ViewStates.Gone;
        sessionPill.Visibility = ViewStates.Visible;
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
                sessionPill.Visibility = ViewStates.Gone;
                extraKeysBar.Visibility = ViewStates.Gone;
                hudText.Visibility = ViewStates.Gone;
                connectOverlay.Visibility = ViewStates.Visible;
                statusText.Text = endMessage;
            });
        });
    }

    void Disconnect()
    {
        ReleaseStickyModifiers();
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
                    session.SendInput(new CoreRemoteInput("move", X: nx, Y: ny));
                    session.SendInput(new CoreRemoteInput("button", Button: 1, Up: false));
                }
                break;

            case MotionEventActions.Move:
                float mdx = x - lastTouchX;
                float mdy = y - lastTouchY;
                float moveDx = x - touchDownX, moveDy = y - touchDownY;
                if (!movedSinceDown && MathF.Sqrt(moveDx * moveDx + moveDy * moveDy) > 10f)
                    movedSinceDown = true;
                lastTouchX = x;
                lastTouchY = y;

                if (touchpadMode)
                {
                    if (viewport.TryApplyRelativeDelta(mdx, mdy, 1.35f, out int rx, out int ry))
                    {
                        if (viewport.TryNormalizedToView(rx, ry, out float vx, out float vy))
                            cursorView.UpdateLocalPosition(vx, vy);
                        session.SendInput(new CoreRemoteInput("move", X: rx, Y: ry));
                    }
                }
                else if (viewport.TryViewToNormalized(x, y, true, out int tx, out int ty))
                {
                    cursorView.UpdateLocalPosition(x, y);
                    session.SendInput(new CoreRemoteInput("move", X: tx, Y: ty));
                }
                break;

            case MotionEventActions.Up:
                if (touchpadMode)
                {
                    long heldMs = Java.Lang.JavaSystem.CurrentTimeMillis() - touchDownTimeMs;
                    if (!movedSinceDown)
                    {
                        int button = heldMs >= 450 ? 2 : 1;
                        session.SendInput(new CoreRemoteInput("button", Button: button, Up: false));
                        session.SendInput(new CoreRemoteInput("button", Button: button, Up: true));
                    }
                }
                else
                {
                    session.SendInput(new CoreRemoteInput("button", Button: 1, Up: true));
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
                session.SendInput(new CoreRemoteInput("move", X: nx, Y: ny));
                e.Handled = true;
            }
        }
        else if (ev.Action == MotionEventActions.Scroll)
        {
            int wheelY = (int)Math.Round(ev.GetAxisValue(Axis.Vscroll) * 120f);
            int wheelX = (int)Math.Round(ev.GetAxisValue(Axis.Hscroll) * 120f);
            if (wheelX != 0 || wheelY != 0)
            {
                session.SendInput(new CoreRemoteInput("wheel", WheelX: wheelX, WheelY: wheelY));
                e.Handled = true;
            }
        }
    }

    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (session is not null && AndroidKeyMapper.TryMap(keyCode, out int vk, out int scan, out bool ext))
        {
            session.SendInput(new CoreRemoteInput("key", VirtualKey: vk, ScanCode: scan, Extended: ext, Up: false, Repeat: (e?.RepeatCount ?? 0) > 0));
            return true;
        }
        return base.OnKeyDown(keyCode, e);
    }

    public override bool OnKeyUp(Keycode keyCode, KeyEvent? e)
    {
        if (session is not null && AndroidKeyMapper.TryMap(keyCode, out int vk, out int scan, out bool ext))
        {
            session.SendInput(new CoreRemoteInput("key", VirtualKey: vk, ScanCode: scan, Extended: ext, Up: true));
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
                    session?.SendInput(new CoreRemoteInput("text", Text: text));
            })!
            .SetNegativeButton("取消", (_, _) => { })!
            .Show();
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
        edit.SetHintTextColor(Color.Argb(160, 190, 200, 215));
        parent.AddView(edit);
        return edit;
    }

    Button MakeBarButton(string title, Color bg, Action onClick)
    {
        var btn = new Button(this) { Text = title, TextSize = 12f };
        btn.SetTextColor(Color.White);
        btn.SetAllCaps(false);
        btn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        btn.SetMinWidth(Dp(40));
        btn.SetMinHeight(Dp(34));
        btn.Background = MakeRounded(bg, Dp(14));
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        lp.LeftMargin = Dp(4);
        lp.RightMargin = Dp(4);
        btn.LayoutParameters = lp;
        btn.Click += (_, _) => onClick();
        return btn;
    }

    static GradientDrawable MakeRounded(Color color, float radius)
    {
        var d = new GradientDrawable();
        d.SetColor(color);
        d.SetCornerRadius(radius);
        return d;
    }

    int Dp(int dp) => (int)Math.Round(dp * Resources!.DisplayMetrics!.Density);

    protected override void OnStart() { base.OnStart(); foreground = true; EnterImmersiveMode(); }

    protected override void OnStop()
    {
        foreground = false;
        requestingPermission = false;
        passInput.Text = "";
        ReleaseStickyModifiers();
        session?.ReleaseAllInputs();
        session?.AttachSurface(null);
        base.OnStop();
    }

    protected override void OnDestroy() { Disconnect(); base.OnDestroy(); }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != LanRequest || !requestingPermission) return;
        requestingPermission = false;
        statusText.Text = HasLanPermission() ? "局域网权限已授予，请重新输入密码并点击连接。" : "未获得局域网权限，无法连接。";
    }

    public void SurfaceCreated(ISurfaceHolder holder) { activeSurface = holder.Surface; session?.AttachSurface(activeSurface); }
    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
    public void SurfaceDestroyed(ISurfaceHolder holder) { activeSurface = null; session?.AttachSurface(null); }
}
