using Android.App;
using Android.Content.PM;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;
using System.Security.Authentication;
using CoreRemoteInput = RelaxDesk.Core.RemoteInput;
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
    EditText hostInput = null!, portInput = null!, userInput = null!, passInput = null!;
    CheckBox rememberPassCheck = null!;
    string savedFingerprint = "";
    Button connectBtn = null!, backListBtn = null!, modeBtn = null!, collapseBtn = null!;
    Button keysToggleBtn = null!, audioToggleBtn = null!, hudToggleBtn = null!, disconnectBtn = null!;
    Button ctrlBtn = null!, altBtn = null!, shiftBtn = null!, winBtn = null!;

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
    bool isPinching;
    long touchDownTimeMs;
    float touchDownX, touchDownY;
    bool movedSinceDown;
    Handler? uiHandler;
    string pcName = "";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidStrings.Init(this);
        Window?.SetFlags(WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn,
                        WindowManagerFlags.Secure | WindowManagerFlags.KeepScreenOn);

        SetContentView(Resource.Layout.activity_remote_session);
        uiHandler = new Handler(Looper.MainLooper!);

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

        hudText = FindViewById<TextView>(Resource.Id.session_hud)!;
        sessionPill = FindViewById<LinearLayout>(Resource.Id.session_pill_bar)!;
        pillButtonsContainer = FindViewById<LinearLayout>(Resource.Id.session_pill_buttons)!;
        collapseBtn = FindViewById<Button>(Resource.Id.btn_pill_collapse)!;
        modeBtn = FindViewById<Button>(Resource.Id.btn_mode)!;
        keysToggleBtn = FindViewById<Button>(Resource.Id.btn_keys_toggle)!;
        audioToggleBtn = FindViewById<Button>(Resource.Id.btn_audio_toggle)!;
        hudToggleBtn = FindViewById<Button>(Resource.Id.btn_hud_toggle)!;
        disconnectBtn = FindViewById<Button>(Resource.Id.btn_disconnect)!;
        extraKeysBar = FindViewById<HorizontalScrollView>(Resource.Id.extra_keys_bar)!;
        connectOverlay = FindViewById<View>(Resource.Id.connect_overlay)!;
        statusText = FindViewById<TextView>(Resource.Id.connect_status)!;
        hostInput = FindViewById<EditText>(Resource.Id.input_host)!;
        portInput = FindViewById<EditText>(Resource.Id.input_port)!;
        userInput = FindViewById<EditText>(Resource.Id.input_user)!;
        passInput = FindViewById<EditText>(Resource.Id.input_password)!;
        rememberPassCheck = FindViewById<CheckBox>(Resource.Id.chk_remember_password)!;
        backListBtn = FindViewById<Button>(Resource.Id.btn_back_list)!;
        connectBtn = FindViewById<Button>(Resource.Id.btn_connect)!;

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
                        pillButtonsContainer.Visibility = pillExpanded ? ViewStates.Visible : ViewStates.Gone;
                        collapseBtn.Text = pillExpanded ? "‹" : "≡";
                        if (pillExpanded) sessionPill.TranslationX = 0f;
                    }
                    pillDragging = false;
                    e.Handled = true;
                    break;
            }
        };
        audioToggleBtn.Click += (_, _) =>
        {
            audioMuted = !audioMuted;
            audioToggleBtn.Text = audioMuted ? "🔇" : "🔊";
            session?.SetAudioMuted(audioMuted);
        };
        modeBtn.Click += (_, _) =>
        {
            touchpadMode = !touchpadMode;
            modeBtn.Text = AndroidStrings.Get(touchpadMode ? "mode_touchpad" : "mode_direct");
        };
        keysToggleBtn.Click += (_, _) =>
        {
            extraKeysBar.Visibility = extraKeysBar.Visibility == ViewStates.Visible ? ViewStates.Gone : ViewStates.Visible;
        };
        FindViewById<Button>(Resource.Id.btn_reset_zoom)!.Click += (_, _) =>
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
        PopulateFromIntentOrStore();
        EnterImmersiveMode();
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
        modeBtn.Text = AndroidStrings.Get(touchpadMode ? "mode_touchpad" : "mode_direct");
        keysToggleBtn.Text = AndroidStrings.Get("keys_bar");
        hudToggleBtn.Text = AndroidStrings.Get("metrics");
        disconnectBtn.Text = AndroidStrings.Get("disconnect");
        statusText.Text = AndroidStrings.Get("connect_prompt");
    }

    void PopulateExtraKeysBar()
    {
        var row = FindViewById<LinearLayout>(Resource.Id.extra_keys_row)!;
        row.AddView(MakeKeyButton("Esc", KeyBg, () => SendTapKey(0x1B, 0x01, false)));
        row.AddView(MakeKeyButton("Tab", KeyBg, () => SendTapKey(0x09, 0x0F, false)));

        ctrlBtn = MakeKeyButton("Ctrl", KeyBg, () => ToggleModifier(ref stickyCtrl, ctrlBtn, 0xA2, 0x1D, false));
        altBtn = MakeKeyButton("Alt", KeyBg, () => ToggleModifier(ref stickyAlt, altBtn, 0xA4, 0x38, false));
        shiftBtn = MakeKeyButton("Shift", KeyBg, () => ToggleModifier(ref stickyShift, shiftBtn, 0xA0, 0x2A, false));
        winBtn = MakeKeyButton("Win", KeyBg, () => ToggleModifier(ref stickyWin, winBtn, 0x5B, 0x5B, true));
        row.AddView(ctrlBtn);
        row.AddView(altBtn);
        row.AddView(shiftBtn);
        row.AddView(winBtn);

        row.AddView(MakeKeyButton("←", KeyBg, () => SendTapKey(0x25, 0x4B, true)));
        row.AddView(MakeKeyButton("↑", KeyBg, () => SendTapKey(0x26, 0x48, true)));
        row.AddView(MakeKeyButton("↓", KeyBg, () => SendTapKey(0x28, 0x50, true)));
        row.AddView(MakeKeyButton("→", KeyBg, () => SendTapKey(0x27, 0x4D, true)));
        row.AddView(MakeKeyButton("Del", KeyBg, () => SendTapKey(0x2E, 0x53, true)));
        row.AddView(MakeKeyButton("Win+D", KeyBg, () =>
        {
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x5B, ScanCode: 0x5B, Extended: true, Up: false));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x44, ScanCode: 0x20, Extended: false, Up: false));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x44, ScanCode: 0x20, Extended: false, Up: true));
            session?.SendInput(new CoreRemoteInput("key", VirtualKey: 0x5B, ScanCode: 0x5B, Extended: true, Up: true));
        }));
        row.AddView(MakeKeyButton(AndroidStrings.Get("send_text"), KeyActiveBg, ShowTextInputDialog));
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
                new AlertDialog.Builder(this)
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
            catch (HostConsentRejectedException) { endMessage = "Host rejected connection."; }
            catch (PasswordLoginRejectedException) { endMessage = "Invalid username or password."; }
            catch (HostAdmissionException ex) { endMessage = ex.Reason switch
            {
                "admission-pending-v5" => "被控端正在建立或清理另一个会话，请稍后重试。",
                "admission-busy-v5" => "被控端正在使用中；此设备须先在空闲时连接并获批准。",
                "admission-rate-limited-v5" => "连接尝试过于频繁，请稍后重试。",
                "takeover-cancel-v5" => "已取消接管。",
                _ => "会话状态已变化，请重新连接。"
            }; }
            catch (RemoteSessionEndedException ex) { endMessage = ex.Reason == SessionEndReason.ReplacedByViewer
                ? "会话已被另一台设备接管。" : "远程会话已结束。"; }
            catch (AuthenticationException) { endMessage = "TLS authentication failed."; }
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
        "接管连接", "继续将断开当前控制端。是否接管这台电脑？", "接管连接", ct);

    Task<bool> ConfirmServerCertificateAsync(string fingerprint, CancellationToken ct)
    {
        string formatted = string.Join(":", Enumerable.Range(0, fingerprint.Length / 2).Select(i => fingerprint.Substring(i * 2, 2)));
        return ConfirmConnectionAsync("核对主机 TLS 证书指纹",
            $"首次连接或主机证书已变更，请核对 Windows 被控端显示的 SHA-256 指纹：\n\n{formatted}", "信任并连接", ct);
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
            dialog = new AlertDialog.Builder(this)
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
        session?.ReleaseAllInputs();
        sessionCts?.Cancel();
    }

    void ScheduleHudTick()
    {
        uiHandler?.PostDelayed(() =>
        {
            if (session is null || IsDestroyed) return;
            if (showHud) hudText.Text = session.Telemetry.FormatHud() +
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
        var input = new EditText(this) { Hint = AndroidStrings.Get("text_hint") };
        new AlertDialog.Builder(this)
            .SetTitle(AndroidStrings.Get("send_text"))!
            .SetView(input)!
            .SetPositiveButton(AndroidStrings.Get("send"), (_, _) =>
            {
                string text = input.Text ?? "";
                if (InputProtocol.TextValid(text))
                    session?.SendInput(new CoreRemoteInput("text", Text: text));
            })!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { })!
            .Show();
    }

    Button MakeKeyButton(string title, Color bg, Action onClick)
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
        statusText.Text = HasLanPermission() ? AndroidStrings.Get("connect_prompt") : "LAN permission denied.";
    }

    public void SurfaceCreated(ISurfaceHolder holder) { activeSurface = holder.Surface; session?.AttachSurface(activeSurface); }
    public void SurfaceChanged(ISurfaceHolder holder, Format format, int width, int height) { }
    public void SurfaceDestroyed(ISurfaceHolder holder) { activeSurface = null; session?.AttachSurface(null); }
}
