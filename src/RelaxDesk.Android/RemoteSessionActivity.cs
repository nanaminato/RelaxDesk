using Android.App;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;
using System.Security.Authentication;
using CoreRemoteInput = RelaxDesk.Core.RemoteInput;
using Orientation = Android.Widget.Orientation;
using Resource = RelaxDesk.Android.Resource;

namespace RelaxDesk.Mobile;

/// <summary>
/// Immersive full-screen remote desktop session backed by Resources/layout/activity_remote_session.xml.
/// </summary>
[Activity(
    Label = "RelaxDesk",
    Exported = false,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ScreenOrientation = ScreenOrientation.SensorLandscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public sealed class RemoteSessionActivity : Activity, ISurfaceHolderCallback
{
    public const string ExtraName = "rd_name";
    public const string ExtraHost = "rd_host";
    public const string ExtraPort = "rd_port";
    public const string ExtraPin = "rd_pin";
    public const string ExtraUser = "rd_user";

    const string LanPermission = "android.permission.ACCESS_LOCAL_NETWORK";
    const int LanRequest = 32;

    readonly ViewportMapper viewport = new();
    readonly TouchGestureState gesture = new();
    float touchpadSensitivity = 1.35f;
    Button displayBtn = null!;
    TextView zoomText = null!;
    int zoomHintVersion;
    RemoteDesktopSession? session;
    CancellationTokenSource? sessionCts;
    Task? sessionWorker;

    View sessionRoot = null!;
    View connectOverlay = null!;
    LinearLayout connectCard = null!;
    FrameLayout stage = null!;
    SurfaceView videoSurface = null!;
    RemoteCursorView cursorView = null!;
    TextView statusText = null!;
    TextView hudText = null!;
    LinearLayout sessionPill = null!;
    HorizontalScrollView pillScroll = null!;
    LinearLayout pillButtonsContainer = null!;
    HorizontalScrollView extraKeysBar = null!;
    EditText hostInput = null!, portInput = null!, userInput = null!, passInput = null!;
    CheckBox rememberPassCheck = null!;
    string savedFingerprint = "";
    Button connectBtn = null!, backListBtn = null!, videoBtn = null!, modeBtn = null!, collapseBtn = null!;
    Button keysToggleBtn = null!, resetZoomBtn = null!, audioToggleBtn = null!, hudToggleBtn = null!, disconnectBtn = null!;
    Button ctrlBtn = null!, altBtn = null!, shiftBtn = null!, winBtn = null!, sendTextBtn = null!;
    readonly List<Button> extraKeyButtons = [];
    int insetTop, insetBottom, insetLeft, insetRight;

    AndroidVideoPreferences videoPreferences = new();
    Surface? activeSurface;
    bool foreground;
    bool requestingPermission;
    bool touchpadMode = true;
    bool showHud = true;
    bool audioMuted;
    bool pillExpanded = true;
    float pillDragStartRawX;
    float pillStartTranslationX;
    bool pillDragging;
    bool stickyCtrl, stickyAlt, stickyShift, stickyWin;
    float lastTouchX, lastTouchY;
    float prevPinchDist;
    int secondPointerId;
    long? touchDownTimeMs;
    float touchDownX, touchDownY;
    bool movedSinceDown;
    int touchSlop;
    Handler? uiHandler;
    string pcName = "";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        touchSlop = ViewConfiguration.Get(this)!.ScaledTouchSlop;
        AndroidStrings.Init(this);
        AndroidTheme.Init(this);
        using (var prefs = GetSharedPreferences("relaxdesk_video_v1", global::Android.Content.FileCreationMode.Private))
        {
            try { videoPreferences = new AndroidVideoPreferences(prefs!.GetInt("fps", 30), prefs.GetInt("bitrate", 8_000_000), prefs.GetBoolean("automatic", false)).Validate(); }
            catch (InvalidDataException) { videoPreferences = new(); }
        }
        using (var prefs = GetSharedPreferences("relaxdesk_input_v1", global::Android.Content.FileCreationMode.Private))
        {
            float saved = prefs!.GetFloat("sensitivity", 1.35f);
            touchpadSensitivity = float.IsFinite(saved) ? Math.Clamp(saved, 0.5f, 3f) : 1.35f;
        }
        Window?.SetFlags(WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn,
                        WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn);

        SetContentView(Resource.Layout.activity_remote_session);
        uiHandler = new Handler(Looper.MainLooper!);

        sessionRoot = FindViewById<View>(Resource.Id.session_root)!;
        stage = FindViewById<FrameLayout>(Resource.Id.session_stage)!;
        videoSurface = FindViewById<SurfaceView>(Resource.Id.session_video_surface)!;
        videoSurface.Holder!.AddCallback(this);

        cursorView = new RemoteCursorView(this);
        stage.AddView(cursorView, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        stage.LayoutChange += (_, _) =>
        {
            viewport.SetViewSize(stage.Width, stage.Height);
            UpdateVideoSurfaceLayout();
        };
        stage.Touch += OnStageTouch;
        stage.GenericMotion += OnStageGenericMotion;

        zoomText = new TextView(this) { TextSize = 22f, Visibility = ViewStates.Gone, Clickable = false };
        zoomText.SetTextColor(Color.White);
        zoomText.SetPadding(Dp(16), Dp(8), Dp(16), Dp(8));
        zoomText.Background = AndroidTheme.MakeRounded(Color.Argb(210, 20, 24, 32), Dp(12));
        ((FrameLayout)sessionRoot).AddView(zoomText, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Center));

        hudText = FindViewById<TextView>(Resource.Id.session_hud)!;
        sessionPill = FindViewById<LinearLayout>(Resource.Id.session_pill_bar)!;
        pillScroll = FindViewById<HorizontalScrollView>(Resource.Id.session_pill_scroll)!;
        pillButtonsContainer = FindViewById<LinearLayout>(Resource.Id.session_pill_buttons)!;
        collapseBtn = FindViewById<Button>(Resource.Id.btn_pill_collapse)!;
        videoBtn = FindViewById<Button>(Resource.Id.btn_video)!;
        modeBtn = FindViewById<Button>(Resource.Id.btn_mode)!;
        keysToggleBtn = FindViewById<Button>(Resource.Id.btn_keys_toggle)!;
        resetZoomBtn = FindViewById<Button>(Resource.Id.btn_reset_zoom)!;
        audioToggleBtn = FindViewById<Button>(Resource.Id.btn_audio_toggle)!;
        hudToggleBtn = FindViewById<Button>(Resource.Id.btn_hud_toggle)!;
        disconnectBtn = FindViewById<Button>(Resource.Id.btn_disconnect)!;
        extraKeysBar = FindViewById<HorizontalScrollView>(Resource.Id.extra_keys_bar)!;
        connectOverlay = FindViewById<View>(Resource.Id.connect_overlay)!;
        connectCard = FindViewById<LinearLayout>(Resource.Id.connect_card)!;
        statusText = FindViewById<TextView>(Resource.Id.connect_status)!;
        hostInput = FindViewById<EditText>(Resource.Id.input_host)!;
        portInput = FindViewById<EditText>(Resource.Id.input_port)!;
        userInput = FindViewById<EditText>(Resource.Id.input_user)!;
        passInput = FindViewById<EditText>(Resource.Id.input_password)!;
        rememberPassCheck = FindViewById<CheckBox>(Resource.Id.chk_remember_password)!;
        backListBtn = FindViewById<Button>(Resource.Id.btn_back_list)!;
        connectBtn = FindViewById<Button>(Resource.Id.btn_connect)!;

        sessionRoot.SetOnApplyWindowInsetsListener(new InsetsListener((top, bottom, left, right) =>
        {
            insetTop = top;
            insetBottom = bottom;
            insetLeft = left;
            insetRight = right;
            ApplyResponsiveLayout();
        }));

        // Tap to toggle collapse/expand; when collapsed, drag horizontally to reposition.
        collapseBtn.Touch += (_, e) =>
        {
            var ev = e.Event;
            if (ev is null) return;
            switch (ev.ActionMasked)
            {
                case MotionEventActions.Down:
                    pillDragStartRawX = ev.RawX;
                    pillStartTranslationX = sessionPill.TranslationX;
                    pillDragging = false;
                    e.Handled = true;
                    break;

                case MotionEventActions.Move:
                    float dx = ev.RawX - pillDragStartRawX;
                    if (!pillExpanded && (pillDragging || MathF.Abs(dx) > Dp(8)))
                    {
                        pillDragging = true;
                        float maxOffset = Math.Max(0f, (stage.Width - sessionPill.Width) * 0.5f - Dp(8));
                        sessionPill.TranslationX = Math.Clamp(pillStartTranslationX + dx, -maxOffset, maxOffset);
                    }
                    e.Handled = true;
                    break;

                case MotionEventActions.Up:
                case MotionEventActions.Cancel:
                    if (!pillDragging)
                    {
                        pillExpanded = !pillExpanded;
                        pillScroll.Visibility = pillExpanded ? ViewStates.Visible : ViewStates.Gone;
                        collapseBtn.Text = pillExpanded ? "‹" : "≡";
                        if (pillExpanded) sessionPill.TranslationX = 0f;
                    }
                    pillDragging = false;
                    e.Handled = true;
                    break;
            }
        };
        videoBtn.Click += (_, _) => ShowVideoSettings();
        audioToggleBtn.Click += (_, _) =>
        {
            audioMuted = !audioMuted;
            audioToggleBtn.Text = audioMuted ? "🔇" : "🔊";
            session?.SetAudioMuted(audioMuted);
        };
        displayBtn = new Button(this) { TextSize = 12f };
        displayBtn.SetAllCaps(false);
        pillButtonsContainer.AddView(displayBtn, 3, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, Dp(34)));
        displayBtn.Click += (_, _) => ShowDisplaySettings();
        modeBtn.Click += (_, _) =>
        {
            ResetTouchInput();
            touchpadMode = !touchpadMode;
            modeBtn.Text = AndroidStrings.Get(touchpadMode ? "mode_touchpad" : "mode_direct");
        };
        keysToggleBtn.Click += (_, _) =>
        {
            extraKeysBar.Visibility = extraKeysBar.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
        };
        resetZoomBtn.Click += (_, _) =>
        {
            viewport.ResetTransform();
            UpdateVideoSurfaceLayout();
        };
        hudToggleBtn.Click += (_, _) =>
        {
            showHud = !showHud;
            hudText.Visibility = showHud && session is not null ? ViewStates.Visible : ViewStates.Gone;
        };
        disconnectBtn.Click += (_, _) => Disconnect();
        backListBtn.Click += (_, _) => { if (session is not null) Disconnect(); else Finish(); };
        connectBtn.Click += (_, _) => StartConnect();

        PopulateExtraKeysBar();
        ApplyLocalization();
        ApplyTheme();
        ApplyResponsiveLayout();
        PopulateFromIntentOrStore();
        EnterImmersiveMode();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyTheme();
        ApplyResponsiveLayout();
    }

    void ApplyLocalization()
    {
        hostInput.Hint = AndroidStrings.Get("hint_host");
        portInput.Hint = AndroidStrings.Get("hint_port");
        userInput.Hint = AndroidStrings.Get("hint_user");
        passInput.Hint = AndroidStrings.Get("hint_password");
        rememberPassCheck.Text = AndroidStrings.Get("remember_password");
        backListBtn.Text = AndroidStrings.Get("back_list");
        connectBtn.Text = AndroidStrings.Get("connect_btn");
        videoBtn.Text = AndroidStrings.Get("video_settings");
        modeBtn.Text = AndroidStrings.Get(touchpadMode ? "mode_touchpad" : "mode_direct");
        keysToggleBtn.Text = AndroidStrings.Get("keys_bar");
        displayBtn.Text = AndroidStrings.Get("display_settings");
        resetZoomBtn.Text = AndroidStrings.Get("display_reset");
        hudToggleBtn.Text = AndroidStrings.Get("metrics");
        disconnectBtn.Text = AndroidStrings.Get("disconnect");
        sendTextBtn.Text = AndroidStrings.Get("send_text");
        statusText.Text = AndroidStrings.Get("connect_prompt");
    }

    void ApplyTheme()
    {
        var p = AndroidTheme.GetPalette(this);
        AndroidTheme.ApplyWindowChrome(this, immersiveDark: connectOverlay.Visibility != ViewStates.Visible);

        connectOverlay.SetBackgroundColor(p.OverlayScrim);
        connectCard.Background = AndroidTheme.MakeRounded(p.CardBackground, Dp(14), p.CardStroke, Dp(1));
        statusText.SetTextColor(p.PrimaryText);

        foreach (var edit in new[] { hostInput, portInput, userInput, passInput })
        {
            edit.SetTextColor(p.PrimaryText);
            edit.SetHintTextColor(p.HintText);
            edit.BackgroundTintList = ColorStateList.ValueOf(p.CardStroke);
        }
        rememberPassCheck.SetTextColor(p.SecondaryText);
        rememberPassCheck.ButtonTintList = ColorStateList.ValueOf(p.AccentBg);

        backListBtn.SetTextColor(p.ButtonNeutralText);
        backListBtn.Background = AndroidTheme.MakeRounded(p.ButtonNeutralBg, Dp(18));
        connectBtn.SetTextColor(p.AccentText);
        connectBtn.Background = AndroidTheme.MakeRounded(p.AccentBg, Dp(18));

        sessionPill.Background = AndroidTheme.MakeRounded(p.SessionPillBg, Dp(22), p.SessionPillStroke, Dp(1));
        collapseBtn.SetTextColor(p.PrimaryText);
        foreach (var btn in new[] { videoBtn, modeBtn, keysToggleBtn, displayBtn, resetZoomBtn, audioToggleBtn, hudToggleBtn })
        {
            btn.SetTextColor(p.ButtonNeutralText);
            btn.Background = AndroidTheme.MakeRounded(p.ButtonNeutralBg, Dp(18));
        }
        disconnectBtn.SetTextColor(p.DangerText);
        disconnectBtn.Background = AndroidTheme.MakeRounded(p.DangerBg, Dp(18));

        hudText.SetTextColor(p.HudText);
        hudText.Background = AndroidTheme.MakeRounded(p.HudBg, Dp(10), p.CardStroke, Dp(1));

        extraKeysBar.SetBackgroundColor(p.ExtraKeysBarBg);
        foreach (var btn in extraKeyButtons)
        {
            bool active = (ReferenceEquals(btn, ctrlBtn) && stickyCtrl)
                || (ReferenceEquals(btn, altBtn) && stickyAlt)
                || (ReferenceEquals(btn, shiftBtn) && stickyShift)
                || (ReferenceEquals(btn, winBtn) && stickyWin)
                || ReferenceEquals(btn, sendTextBtn);
            btn.SetTextColor(active ? p.AccentText : p.KeyText);
            btn.Background = AndroidTheme.MakeRounded(active ? p.AccentBg : p.KeyBg, Dp(14));
        }
    }

    void ApplyResponsiveLayout()
    {
        var dm = Resources!.DisplayMetrics!;
        float density = Math.Max(1f, dm.Density);
        int safeWidth = Math.Max(1, dm.WidthPixels - insetLeft - insetRight);
        int safeHeight = Math.Max(1, dm.HeightPixels - insetTop - insetBottom);
        float heightDp = safeHeight / density;

        int overlayPad = Dp(heightDp < 420f ? 10 : 16);
        connectOverlay.SetPadding(overlayPad + insetLeft, overlayPad + insetTop, overlayPad + insetRight, overlayPad + insetBottom);

        int cardPad = Dp(heightDp < 420f ? 14 : 20);
        connectCard.SetPadding(cardPad, cardPad, cardPad, cardPad);
        int maxCardWidth = Dp(480);
        int targetCardWidth = safeWidth - overlayPad * 2 > maxCardWidth ? maxCardWidth : ViewGroup.LayoutParams.MatchParent;
        var cardLp = connectCard.LayoutParameters ?? new LinearLayout.LayoutParams(targetCardWidth, ViewGroup.LayoutParams.WrapContent);
        if (cardLp.Width != targetCardWidth)
        {
            cardLp.Width = targetCardWidth;
            connectCard.LayoutParameters = cardLp;
        }

        if (sessionPill.LayoutParameters is FrameLayout.LayoutParams pillLp)
        {
            pillLp.TopMargin = Dp(8) + insetTop;
            pillLp.LeftMargin = Dp(8) + insetLeft;
            pillLp.RightMargin = Dp(8) + insetRight;
            sessionPill.LayoutParameters = pillLp;
        }

        if (hudText.LayoutParameters is FrameLayout.LayoutParams hudLp)
        {
            hudLp.TopMargin = Dp(12) + insetTop;
            hudLp.LeftMargin = Dp(12) + insetLeft;
            hudText.LayoutParameters = hudLp;
        }

        extraKeysBar.SetPadding(insetLeft, 0, insetRight, insetBottom);
    }

    void PopulateExtraKeysBar()
    {
        var p = AndroidTheme.GetPalette(this);
        var row = FindViewById<LinearLayout>(Resource.Id.extra_keys_row)!;
        row.AddView(MakeKeyButton("Esc", p.KeyBg, p.KeyText, () => SendTapKey(0x1B, 0x01, false)));
        row.AddView(MakeKeyButton("Tab", p.KeyBg, p.KeyText, () => SendTapKey(0x09, 0x0F, false)));

        ctrlBtn = MakeKeyButton("Ctrl", p.KeyBg, p.KeyText, () => ToggleModifier(ref stickyCtrl, ctrlBtn, 0xA2, 0x1D, false));
        altBtn = MakeKeyButton("Alt", p.KeyBg, p.KeyText, () => ToggleModifier(ref stickyAlt, altBtn, 0xA4, 0x38, false));
        shiftBtn = MakeKeyButton("Shift", p.KeyBg, p.KeyText, () => ToggleModifier(ref stickyShift, shiftBtn, 0xA0, 0x2A, false));
        winBtn = MakeKeyButton("Win", p.KeyBg, p.KeyText, () => ToggleModifier(ref stickyWin, winBtn, 0x5B, 0x5B, true));
        row.AddView(ctrlBtn);
        row.AddView(altBtn);
        row.AddView(shiftBtn);
        row.AddView(winBtn);

        row.AddView(MakeKeyButton("←", p.KeyBg, p.KeyText, () => SendTapKey(0x25, 0x4B, true)));
        row.AddView(MakeKeyButton("↑", p.KeyBg, p.KeyText, () => SendTapKey(0x26, 0x48, true)));
        row.AddView(MakeKeyButton("↓", p.KeyBg, p.KeyText, () => SendTapKey(0x28, 0x50, true)));
        row.AddView(MakeKeyButton("→", p.KeyBg, p.KeyText, () => SendTapKey(0x27, 0x4D, true)));
        row.AddView(MakeKeyButton("Del", p.KeyBg, p.KeyText, () => SendTapKey(0x2E, 0x53, true)));
        row.AddView(MakeKeyButton("Win+D", p.KeyBg, p.KeyText, () =>
        {
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x5B, ScanCode: 0x5B, Extended: true, Up: false));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x44, ScanCode: 0x20, Extended: false, Up: false));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x44, ScanCode: 0x20, Extended: false, Up: true));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x5B, ScanCode: 0x5B, Extended: true, Up: true));
        }));
        sendTextBtn = MakeKeyButton(AndroidStrings.Get("send_text"), p.AccentBg, p.AccentText, ShowTextInputDialog);
        row.AddView(sendTextBtn);
    }

    void PopulateFromIntentOrStore()
    {
        string? intentHost = Intent?.GetStringExtra(ExtraHost);
        if (!string.IsNullOrWhiteSpace(intentHost))
        {
            pcName = Intent?.GetStringExtra(ExtraName) ?? intentHost;
            hostInput.Text = intentHost;
            portInput.Text = (Intent?.GetIntExtra(ExtraPort, 48200) ?? 48200).ToString();
            savedFingerprint = Intent?.GetStringExtra(ExtraPin) ?? "";
            userInput.Text = Intent?.GetStringExtra(ExtraUser) ?? "";
            LoadSavedPasswordIfAny();
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
            savedFingerprint = latest.Fingerprint;
            userInput.Text = latest.Username;
            LoadSavedPasswordIfAny();
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
        var p = AndroidTheme.GetPalette(this);
        btn.SetTextColor(state ? p.AccentText : p.KeyText);
        btn.Background = AndroidTheme.MakeRounded(state ? p.AccentBg : p.KeyBg, Dp(14));
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
        string ip = (hostInput.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(ip) || !int.TryParse(portInput.Text, out int port) || port is < 1 or > 65535)
        {
            statusText.Text = AndroidStrings.Get("err_invalid_endpoint");
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
        bool shouldRemember = rememberPassCheck.Checked;
        passInput.Text = "";
        if (!PasswordVerifier.ValidUserName(username) || password.Length == 0)
        {
            statusText.Text = AndroidStrings.Get("err_invalid_user");
            return;
        }

        if (shouldRemember)
        {
            if (!PasswordStore.TrySaveEncrypted(this, ip, port, username, password))
            {
                // Keystore encryption unavailable: ask user whether to save in plaintext or not save.
                AndroidTheme.CreateDialogBuilder(this)
                    .SetTitle(AndroidStrings.Get("enc_unavail_title"))!
                    .SetMessage(AndroidStrings.Get("enc_unavail_msg"))!
                    .SetCancelable(false)!
                    .SetPositiveButton(AndroidStrings.Get("save_plain"), (_, _) =>
                    {
                        PasswordStore.SavePlaintext(this, ip, port, username, password);
                        BeginSessionConnect(ip, port, username, password);
                    })!
                    .SetNegativeButton(AndroidStrings.Get("dont_save"), (_, _) =>
                    {
                        PasswordStore.Clear(this, ip, port, username);
                        BeginSessionConnect(ip, port, username, password);
                    })!
                    .Show();
                return;
            }
        }
        else
        {
            PasswordStore.Clear(this, ip, port, username);
        }

        BeginSessionConnect(ip, port, username, password);
    }

    void LoadSavedPasswordIfAny()
    {
        string ip = (hostInput.Text ?? "").Trim();
        string user = (userInput.Text ?? "").Trim();
        if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(user) || !int.TryParse(portInput.Text, out int port))
            return;
        string? saved = PasswordStore.Load(this, ip, port, user);
        if (!string.IsNullOrEmpty(saved))
        {
            passInput.Text = saved;
            rememberPassCheck.Checked = true;
        }
    }

    void BeginSessionConnect(string ip, int port, string username, string password)
    {

        string displayName = string.IsNullOrWhiteSpace(pcName) ? ip : pcName;
        // An Intent/list row may predate a completed certificate confirmation.
        // Resolve trust for the actual endpoint on every attempt.
        string pinToUse = ConnectionStore.Load(this).FirstOrDefault(e =>
            string.Equals(e.Host, ip, StringComparison.OrdinalIgnoreCase) && e.Port == port)?.Fingerprint ?? "";
        ConnectionStore.SaveOrUpdate(this, new SavedHostEntry(displayName, ip, port, pinToUse, username, DateTime.UtcNow.Ticks));

        var cts = new CancellationTokenSource();
        var activeSession = new RemoteDesktopSession();
        activeSession.ConfigureVideo(videoPreferences);
        sessionCts = cts;
        session = activeSession;
        viewport.ResetPointer();
        activeSession.AttachSurface(activeSurface);

        activeSession.StatusChanged += msg => RunOnUiThread(() => statusText.Text = msg);
        activeSession.FirstFramePresented += () => RunOnUiThread(() =>
        {
            if (!ReferenceEquals(session, activeSession)) return;
            connectOverlay.Visibility = ViewStates.Gone;
            sessionPill.Visibility = ViewStates.Visible;
        });
        activeSession.VideoSizeChanged += (w, h) => RunOnUiThread(() =>
        {
            viewport.SetVideoSize(w, h);
            UpdateVideoSurfaceLayout();
        });
        activeSession.PointerUpdated += ptr => RunOnUiThread(() =>
        {
            if (!ReferenceEquals(session, activeSession)) return;
            cursorView.ApplyRemoteState(ptr);
            if (viewport.TrySyncRemotePointer(ptr))
            {
                if (viewport.TryNormalizedToView(viewport.CursorNormalizedX, viewport.CursorNormalizedY, out float vx, out float vy))
                    cursorView.UpdateLocalPosition(vx, vy);
            }
        });

        connectOverlay.Visibility = ViewStates.Visible;
        connectBtn.Enabled = false;
        hostInput.Enabled = portInput.Enabled = userInput.Enabled = passInput.Enabled = false;
        sessionPill.Visibility = ViewStates.Gone;
        hudText.Visibility = showHud ? ViewStates.Visible : ViewStates.Gone;
        ScheduleHudTick();

        sessionWorker = Task.Run(async () =>
        {
            string endMessage = AndroidStrings.Get("disconnected");
            try
            {
                await activeSession.RunAsync(ip, port, pinToUse, username, password,
                    AndroidDeviceKey.OpenProbeKey,
                    ConfirmServerCertificateAsync,
                    verifiedPin =>
                    {
                        savedFingerprint = verifiedPin;
                        ConnectionStore.SaveOrUpdate(this, new SavedHostEntry(displayName, ip, port, verifiedPin, username, DateTime.UtcNow.Ticks));
                    },
                    cts.Token, ConfirmSessionTakeoverAsync);
            }
            catch (System.OperationCanceledException) when (cts.IsCancellationRequested) { endMessage = AndroidStrings.Get("disconnected"); }
            catch (HostConsentRejectedException) { endMessage = AndroidStrings.Get("err_host_rejected"); }
            catch (PasswordLoginRejectedException) { endMessage = AndroidStrings.Get("err_invalid_credentials"); }
            catch (HostAdmissionException ex) { endMessage = ex.Reason switch
            {
                "admission-pending-v5" => AndroidStrings.Get("err_admission_pending"),
                "admission-busy-v5" => AndroidStrings.Get("err_admission_busy"),
                "admission-rate-limited-v5" => AndroidStrings.Get("err_admission_rate_limited"),
                "takeover-cancel-v5" => AndroidStrings.Get("err_takeover_cancelled"),
                _ => AndroidStrings.Get("err_session_state_changed")
            }; }
            catch (RemoteSessionEndedException ex) { endMessage = ex.Reason == SessionEndReason.ReplacedByViewer
                ? AndroidStrings.Get("err_session_replaced") : AndroidStrings.Get("err_session_ended"); }
            catch (AuthenticationException) { endMessage = AndroidStrings.Get("err_tls_auth_failed"); }
            catch (Exception ex) { endMessage = $"{ex.GetType().Name}: {ex.Message}"; }
            finally
            {
                password = "";
                global::Android.Util.Log.Info("RelaxDesk", "session_end=" + endMessage);
            }

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
                connectBtn.Enabled = true;
                hostInput.Enabled = portInput.Enabled = userInput.Enabled = passInput.Enabled = true;
                LoadSavedPasswordIfAny();
                statusText.Text = endMessage;
            });
        });
    }

    Task<bool> ConfirmSessionTakeoverAsync(CancellationToken ct) => ConfirmConnectionAsync(
        AndroidStrings.Get("takeover_title"),
        AndroidStrings.Get("takeover_msg"),
        AndroidStrings.Get("takeover_accept"), ct);

    Task<bool> ConfirmServerCertificateAsync(string fingerprint, CancellationToken ct)
    {
        string formatted = string.Join(":", Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));
        return ConfirmConnectionAsync(
            AndroidStrings.Get("cert_title"),
            $"{AndroidStrings.Get("cert_msg")}\n\n{formatted}",
            AndroidStrings.Get("cert_trust"), ct);
    }

    async Task<bool> ConfirmConnectionAsync(string title, string message, string accept, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        AlertDialog? dialog = null;
        using var reg = ct.Register(() =>
        {
            tcs.TrySetCanceled(ct);
            RunOnUiThread(() => dialog?.Dismiss());
        });
        RunOnUiThread(() =>
        {
            if (ct.IsCancellationRequested || IsDestroyed || IsFinishing)
            {
                tcs.TrySetResult(false);
                return;
            }
            dialog = AndroidTheme.CreateDialogBuilder(this)
                .SetTitle(title)!
                .SetMessage(message)!
                .SetCancelable(false)!
                .SetPositiveButton(accept, (_, _) => tcs.TrySetResult(true))!
                .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => tcs.TrySetResult(false))!
                .Show();
        });
        return await tcs.Task;
    }

    void Disconnect()
    {
        ReleaseStickyModifiers();
        ResetTouchInput();
        sessionCts?.Cancel();
    }

    void ResetTouchInput()
    {
        ReleaseStickyModifiers();
        session?.ReleaseAllInputs();
        gesture.Reset();
        touchDownTimeMs = null;
        movedSinceDown = false;
    }

    void ShowZoomHint()
    {
        int version = ++zoomHintVersion;
        zoomText.Animate()?.Cancel();
        zoomText.Alpha = 1f;
        zoomText.Text = $"{viewport.Zoom:0.0}×";
        zoomText.Visibility = ViewStates.Visible;
        uiHandler?.PostDelayed(() =>
        {
            if (IsDestroyed || version != zoomHintVersion) return;
            zoomText.Animate()?.Alpha(0f)?.SetDuration(250)?.Start();
        }, 800);
    }

    void ShowDisplaySettings()
    {
        ResetTouchInput();
        var layout = new LinearLayout(this) { Orientation = Orientation.Vertical };
        layout.SetPadding(Dp(20), Dp(8), Dp(20), Dp(8));
        var modes = new Spinner(this);
        modes.Adapter = new ArrayAdapter<string>(this, global::Android.Resource.Layout.SimpleSpinnerDropDownItem,
            new[] { "display_fit", "display_fill", "display_pixels" }.Select(AndroidStrings.Get).ToArray());
        modes.SetSelection((int)viewport.DisplayMode);
        layout.AddView(modes);
        layout.AddView(new TextView(this) { Text = AndroidStrings.Get("display_pixels_tip") });
        var label = new TextView(this);
        var sensitivity = new SeekBar(this) { Max = 250, Progress = (int)Math.Round(touchpadSensitivity * 100) - 50 };
        void UpdateLabel() => label.Text = $"{AndroidStrings.Get("touchpad_sensitivity")}: {(sensitivity.Progress + 50) / 100f:0.00}";
        sensitivity.ProgressChanged += (_, _) => UpdateLabel();
        UpdateLabel();
        layout.AddView(label);
        layout.AddView(sensitivity);
        AndroidTheme.CreateDialogBuilder(this).SetTitle(AndroidStrings.Get("display_settings"))!
            .SetView(layout)!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { })!
            .SetPositiveButton(AndroidStrings.Get("video_apply"), (_, _) =>
            {
                if ((int)viewport.DisplayMode != modes.SelectedItemPosition)
                    viewport.SetDisplayMode((ViewportDisplayMode)modes.SelectedItemPosition);
                touchpadSensitivity = (sensitivity.Progress + 50) / 100f;
                using var prefs = GetSharedPreferences("relaxdesk_input_v1", global::Android.Content.FileCreationMode.Private);
                using var editor = prefs!.Edit();
                editor!.PutFloat("sensitivity", touchpadSensitivity);
                editor.Apply();
                UpdateVideoSurfaceLayout();
            })!.Show();
    }

    void ShowVideoSettings()
    {
        var builder = AndroidTheme.CreateDialogBuilder(this);
        var dialogCtx = builder.Context!;
        var p = AndroidTheme.GetPalette(this);

        var layout = new LinearLayout(dialogCtx) { Orientation = Orientation.Vertical };
        layout.SetPadding(Dp(20), Dp(10), Dp(20), Dp(10));

        var currentText = new TextView(dialogCtx) { Text = AndroidStrings.Get("video_current") + " " + session?.VideoSettingsStatus };
        currentText.SetTextColor(p.SecondaryText);
        currentText.SetPadding(0, 0, 0, Dp(8));
        layout.AddView(currentText);

        var fpsLabel = new TextView(dialogCtx) { Text = AndroidStrings.Get("video_fps") };
        fpsLabel.SetTextColor(p.PrimaryText);
        layout.AddView(fpsLabel);

        int[] rates = [5, 10, 15, 24, 30, 45, 60, 90, 120, 144];
        var fps = new Spinner(dialogCtx);
        fps.Adapter = new ArrayAdapter<string>(dialogCtx, global::Android.Resource.Layout.SimpleSpinnerDropDownItem,
            rates.Select(value => $"{value} FPS").ToArray());
        fps.SetSelection(Math.Max(0, Array.IndexOf(rates, videoPreferences.FramesPerSecond)));
        fps.SetPopupBackgroundDrawable(AndroidTheme.MakeRounded(p.CardBackground, Dp(8), p.CardStroke, Dp(1)));
        layout.AddView(fps);

        var bitrateLabel = new TextView(dialogCtx) { Text = AndroidStrings.Get("video_bitrate") };
        bitrateLabel.SetTextColor(p.PrimaryText);
        bitrateLabel.SetPadding(0, Dp(8), 0, 0);
        layout.AddView(bitrateLabel);

        int[] bitrates = [100_000, 250_000, 500_000, 1_000_000, 2_000_000, 4_000_000, 6_000_000, 8_000_000, 12_000_000, 16_000_000, 20_000_000, 30_000_000, 50_000_000, 100_000_000];
        var bitrate = new Spinner(dialogCtx);
        bitrate.Adapter = new ArrayAdapter<string>(dialogCtx, global::Android.Resource.Layout.SimpleSpinnerDropDownItem,
            bitrates.Select(value => $"{value / 1_000_000d:0.##} Mbps").ToArray());
        bitrate.SetSelection(Math.Max(0, Array.IndexOf(bitrates, videoPreferences.Bitrate)));
        bitrate.SetPopupBackgroundDrawable(AndroidTheme.MakeRounded(p.CardBackground, Dp(8), p.CardStroke, Dp(1)));
        layout.AddView(bitrate);

        var automatic = new CheckBox(dialogCtx) { Text = AndroidStrings.Get("video_auto"), Checked = videoPreferences.Automatic };
        automatic.SetTextColor(p.PrimaryText);
        automatic.ButtonTintList = ColorStateList.ValueOf(p.AccentBg);
        layout.AddView(automatic);

        var tipText = new TextView(dialogCtx) { Text = AndroidStrings.Get("video_tip"), TextSize = 12f };
        tipText.SetTextColor(p.SecondaryText);
        tipText.SetPadding(0, Dp(6), 0, 0);
        layout.AddView(tipText);

        var scroll = new ScrollView(dialogCtx);
        scroll.AddView(layout);

        builder.SetTitle(AndroidStrings.Get("video_settings"))!
            .SetView(scroll)!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { })!
            .SetPositiveButton(AndroidStrings.Get("video_apply"), (_, _) =>
            {
                videoPreferences = new(rates[fps.SelectedItemPosition], bitrates[bitrate.SelectedItemPosition], automatic.Checked);
                using var prefs = GetSharedPreferences("relaxdesk_video_v1", global::Android.Content.FileCreationMode.Private);
                using var editor = prefs!.Edit();
                editor!.PutInt("fps", videoPreferences.FramesPerSecond);
                editor.PutInt("bitrate", videoPreferences.Bitrate);
                editor.PutBoolean("automatic", videoPreferences.Automatic);
                editor.Apply();
                session?.ConfigureVideo(videoPreferences);
            })!.Show();
    }

    void ScheduleHudTick()
    {
        uiHandler?.PostDelayed(() =>
        {
            if (session is null || IsDestroyed) return;
            if (showHud) hudText.Text = session.VideoSettingsStatus + "\n" + session.Telemetry.FormatHud() +
                $"\nPointer {viewport.CursorNormalizedX},{viewport.CursorNormalizedY}";
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
        if (viewport.TryNormalizedToView(viewport.CursorNormalizedX, viewport.CursorNormalizedY, out float x, out float y))
            cursorView.UpdateLocalPosition(x, y);
    }

    void OnStageTouch(object? sender, View.TouchEventArgs e)
    {
        var ev = e.Event;
        if (ev is null || session is null) return;
        e.Handled = true;

        if (ev.ActionMasked == MotionEventActions.Cancel)
        {
            ResetTouchInput();
            return;
        }
        int pointers = ev.PointerCount;
        var action = ev.ActionMasked;

        if (pointers >= 2)
        {
            bool firstPair = !gesture.MultiplePointers && action == MotionEventActions.PointerDown;
            if (gesture.EnterMultiplePointers())
                session.SendInput(new CoreRemoteInput("button", Button: 1, Up: true));
            if (pointers > 2)
            {
                gesture.CancelPair();
                return;
            }

            var first = (X: ev.GetX(0), Y: ev.GetY(0));
            var second = (X: ev.GetX(1), Y: ev.GetY(1));
            if (action == MotionEventActions.PointerUp)
            {
                if (gesture.EndPair(ev.GetPointerId(ev.ActionIndex) == secondPointerId,
                    ev.EventTime, first, second, touchSlop))
                    SendTouchClick(2);
                return;
            }

            float dx = first.X - second.X;
            float dy = first.Y - second.Y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            float midX = (first.X + second.X) * 0.5f;
            float midY = (first.Y + second.Y) * 0.5f;

            if (firstPair)
            {
                secondPointerId = ev.GetPointerId(ev.ActionIndex);
                var anchor = ev.ActionIndex == 0 ? second : first;
                bool eligible = touchpadMode && !movedSinceDown && touchDownTimeMs is long start &&
                    ev.EventTime - start >= TouchGestureState.LongPressMilliseconds &&
                    !TouchGestureState.Moved((touchDownX, touchDownY), anchor, touchSlop);
                gesture.BeginPair(eligible, ev.EventTime, first, second);
                prevPinchDist = Math.Max(1f, dist);
                lastTouchX = midX;
                lastTouchY = midY;
            }
            else if (action == MotionEventActions.Move && gesture.MovePair(first, second, touchSlop))
            {
                float scale = dist / Math.Max(1f, prevPinchDist);
                viewport.ApplyScaleAndPan(scale, midX, midY, midX - lastTouchX, midY - lastTouchY);
                prevPinchDist = Math.Max(1f, dist);
                lastTouchX = midX;
                lastTouchY = midY;
                UpdateVideoSurfaceLayout();
                ShowZoomHint();
            }
            return;
        }

        if (gesture.MultiplePointers)
        {
            if (action == MotionEventActions.Up)
            {
                gesture.Reset();
                touchDownTimeMs = null;
            }
            return;
        }

        float x = ev.GetX();
        float y = ev.GetY();

        switch (action)
        {
            case MotionEventActions.Down:
                touchDownTimeMs = ev.EventTime;
                touchDownX = lastTouchX = x;
                touchDownY = lastTouchY = y;
                movedSinceDown = false;
                if (!touchpadMode && viewport.TryViewToNormalized(x, y, true, out int nx, out int ny))
                {
                    cursorView.UpdateLocalPosition(x, y);
                    session.SendInput(new CoreRemoteInput("move", X: nx, Y: ny));
                    gesture.BeginDirect();
                    session.SendInput(new CoreRemoteInput("button", Button: 1, Up: false));
                }
                break;

            case MotionEventActions.Move:
                float mdx = x - lastTouchX;
                float mdy = y - lastTouchY;
                if (!movedSinceDown && TouchGestureState.Moved((touchDownX, touchDownY), (x, y), touchSlop))
                    movedSinceDown = true;
                lastTouchX = x;
                lastTouchY = y;

                if (touchpadMode)
                {
                    if (viewport.TryApplyRelativeDelta(mdx, mdy, touchpadSensitivity, out int rx, out int ry))
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
                    if (touchDownTimeMs is long downTime && !movedSinceDown)
                        SendTouchClick(ev.EventTime - downTime >= TouchGestureState.LongPressMilliseconds ? 2 : 1);
                }
                else if (gesture.ReleaseDirect())
                {
                    session.SendInput(new CoreRemoteInput("button", Button: 1, Up: true));
                }
                movedSinceDown = false;
                touchDownTimeMs = null;
                break;

        }
    }

    void SendTouchClick(int button)
    {
        session?.SendInput(new CoreRemoteInput("button", Button: button, Up: false));
        session?.SendInput(new CoreRemoteInput("button", Button: button, Up: true));
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
        var builder = AndroidTheme.CreateDialogBuilder(this);
        var dialogCtx = builder.Context!;
        var p = AndroidTheme.GetPalette(this);

        var container = new LinearLayout(dialogCtx) { Orientation = Orientation.Vertical };
        container.SetPadding(Dp(20), Dp(12), Dp(20), Dp(4));

        var input = new EditText(dialogCtx) { Hint = AndroidStrings.Get("text_hint") };
        input.SetTextColor(p.PrimaryText);
        input.SetHintTextColor(p.HintText);
        input.BackgroundTintList = ColorStateList.ValueOf(p.CardStroke);
        container.AddView(input);

        builder
            .SetTitle(AndroidStrings.Get("send_text"))!
            .SetView(container)!
            .SetPositiveButton(AndroidStrings.Get("send"), (_, _) =>
            {
                string text = input.Text ?? "";
                if (InputProtocol.TextValid(text))
                    session?.SendInput(new CoreRemoteInput("text", Text: text));
            })!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { })!
            .Show();
    }

    Button MakeKeyButton(string title, Color bg, Color fg, Action onClick)
    {
        var btn = new Button(this) { Text = title, TextSize = 12f };
        btn.SetTextColor(fg);
        btn.SetAllCaps(false);
        btn.SetPadding(Dp(12), Dp(6), Dp(12), Dp(6));
        btn.SetMinWidth(Dp(40));
        btn.SetMinHeight(Dp(34));
        btn.Background = AndroidTheme.MakeRounded(bg, Dp(14));
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        lp.LeftMargin = Dp(4);
        lp.RightMargin = Dp(4);
        btn.LayoutParameters = lp;
        btn.Click += (_, _) => onClick();
        extraKeyButtons.Add(btn);
        return btn;
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

    protected override void OnStart() { base.OnStart(); foreground = true; EnterImmersiveMode(); }

    protected override void OnStop()
    {
        foreground = false;
        requestingPermission = false;
        passInput.Text = "";
        ReleaseStickyModifiers();
        ResetTouchInput();
        session?.AttachSurface(null);
        base.OnStop();
    }

    protected override void OnDestroy() { Disconnect(); base.OnDestroy(); }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != LanRequest || !requestingPermission) return;
        requestingPermission = false;
        statusText.Text = HasLanPermission() ? AndroidStrings.Get("connect_prompt") : AndroidStrings.Get("err_lan_permission_denied");
    }

    public void SurfaceCreated(ISurfaceHolder holder) { activeSurface = holder.Surface; session?.AttachSurface(activeSurface); }
    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
    public void SurfaceDestroyed(ISurfaceHolder holder) { activeSurface = null; session?.AttachSurface(null); }
}
