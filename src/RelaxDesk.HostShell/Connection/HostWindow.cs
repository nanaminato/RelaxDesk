using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Core.Localization;
using RelaxDesk.Session;
using RelaxDesk.Windows;
namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow:Window
{
    internal readonly HostProcess Host=new();
    internal Button SharingAction => start;
    readonly HostConsentView consent = new();
    Button allow => consent.Allow;
    Button deny => consent.Deny;
    TextBlock peer => consent.Peer;
    TextBlock consentAccess => consent.Access;
    CheckBox clipboardPermission => consent.ClipboardPermission;
    HostPanelWindow? requestWindow;
    bool fullControlRequest;
    bool nativeApproval, stoppingSharing;
    bool activeInput;
    readonly TextBox logs;
    readonly string application;
    bool closing;
    string? desktopNotice;
    bool hostTokenDenied;
    bool adminDeploymentUnavailable;
    bool checkingInputDeployment;
    internal Func<AdminWorkerDeployment>? InputDeploymentForVerification { get; set; }
    string? adminStartupNotice;
    string[] fullControlFeatures = [];
    string[] requestedClipboardFormats = [];
    const string AdminDeploymentNotice = "完整远程控制不可用。要修复代理，请结束共享，在「服务设置」中选择「安装／更新／修复（管理员）」。";
    const string DesktopUnavailableNotice="不支持当前会话或安全桌面控制。请回到原用户的普通桌面后重新连接并授权；Host 继续等待连接。";
    string? displayedSessionId;
    public HostWindow(string? hostApplication=null, DeviceTrustStore? trustStore=null)
    {
        deviceTrust = trustStore ?? DeviceTrustStore.CurrentUser();
        application=hostApplication??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Host","RelaxDesk.Host.exe"));
        InitializeComponent();
        serviceSettings = new ServiceSettingsView();
        logs = (TextBox)Resources["HostLogPanel"];
        BuildVersion.Text = BuildIdentity.Display;
        var addresses = Addresses();
        ip.Text = addresses.FirstOrDefault() ?? "127.0.0.1";
        if (addresses.Length > 1)
        {
            foreach (string address in addresses)
            {
                var choice = new Button { Content = address, Padding = new(10, 5, 10, 5), FontSize = 12 };
                choice.Click += (_, _) => { if (!Host.Running && !RestartPending) ip.Text = address; };
                addressChoices.Children.Add(choice);
            }
        }

        ConfigureDisplaySettings();
        ConfigureSettingsWindow(openDisplay, "Host.SharingSettings", displaySettings);
        ConfigureLoginSettings();
        ConfigureSettingsWindow(openLogin, "Host.LoginSettings", loginSettings);
        ConfigureServiceSettings();
        ConfigureSettingsWindow(openService, "Host.SystemService", serviceSettings);
        ConfigureDeviceTrust();
        ConfigureSettingsWindow(openDevices, "Host.TrustedDevices", trustedDeviceSettings);
        ConfigureSettingsWindow(openLogs, "Host.DiagnosticsAndLogs", logs);
        ConfigureLanguageSelector();

        connectionNavigation.Click += (_, _) => ShowPage(false);
        settingsNavigation.Click += (_, _) => ShowPage(true);
        loginLink.Click += (_, _) => ShowPage(true, loginSection);
        serviceLink.Click += (_, _) => ShowPage(true, serviceSection);
        ShowPage(false);
        start.Style = (Style)Resources["PrimaryButton"];
        allow.Style = (Style)Resources["PrimaryButton"];
        deny.Style = (Style)Resources["DangerButton"];
        Closed += (_, _) => CloseRequest();
        start.Click += async (_, _) =>
        {
            if (Host.Running || RestartPending) await StopSharing();
            else await StartSharing();
        };
        allow.Click+=async(_,_)=>await Decide(true);deny.Click+=async(_,_)=>await Decide(false);
        Host.Changed+=change=>Dispatcher.BeginInvoke(()=>Changed(change));Host.Diagnostic+=line=>Dispatcher.BeginInvoke(()=>{logs.Text=string.Join(Environment.NewLine,Host.Snapshot());if(line.StartsWith("producer_error")||line.StartsWith("Session ended:")||line.StartsWith("clipboard_")||line.StartsWith("file_transfer_disabled"))detail.Text=line;if(line.StartsWith("audio_disabled")){detail.Text="声音传输已关闭；画面与输入仍可使用。";}});
        Closing+=async(_,e)=>{if(ServiceSetupBusy){e.Cancel=true;serviceSetupNotice.Text="正在处理系统服务，请等待完成后再关闭；如出现 Windows 授权，可在那里取消。";return;}CancelRecovery();if(!Host.Running&&serviceAgent is null&&serviceAttempt is null&&serviceWatch is null)return;e.Cancel=true;if(closing)return;closing=true;await DisconnectService();await StopSharing();Close();};
    }
    bool showingSettings;
    void ConfigureLanguageSelector()
    {
        var manager = LocalizationManager.Current;
        languageSelector.ItemsSource = manager.AvailableLanguages;
        languageSelector.SelectedValue = manager.CurrentLanguageCode;
        languageSelector.SelectionChanged += (_, _) =>
        {
            if (languageSelector.SelectedItem is LanguageOption option)
                manager.SetLanguage(option.Code);
        };
        manager.LanguageChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                PageTitle.Text = Strings.Get(showingSettings ? "Host.PageTitleSettings" : "Host.PageTitleSharing");
                if (!string.Equals(languageSelector.SelectedValue as string, manager.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                    languageSelector.SelectedValue = manager.CurrentLanguageCode;
            });
        };
    }

    internal void ShowPage(bool settings, FrameworkElement? section = null)
    {
        showingSettings = settings;
        PageTitle.Text = Strings.Get(settings ? "Host.PageTitleSettings" : "Host.PageTitleSharing");
        (settings ? connectionNavigation : settingsNavigation).ClearValue(StyleProperty);
        (settings ? settingsNavigation : connectionNavigation).Style = (Style)Resources["PrimaryButton"];
        SharingContent.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsContent.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        MainScroll.ScrollToTop();
        if (settings && section is not null)
            Dispatcher.InvokeAsync(() =>
            {
                if (section.IsVisible)
                    MainScroll.ScrollToVerticalOffset(MainScroll.VerticalOffset + section.TranslatePoint(new Point(), MainScroll).Y);
            }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    void ConfigureSettingsWindow(Button open, string titleKey, UIElement body)
    {
        HostPanelWindow? dialog = null;
        open.Click += (_, _) =>
        {
            if (dialog is not null)
            {
                if (dialog.WindowState == WindowState.Minimized) dialog.WindowState = WindowState.Normal;
                dialog.Activate();
                return;
            }
            var window = new HostPanelWindow(this, Strings.Get(titleKey), body);
            window.Closed += (_, _) => dialog = null;
            dialog = window;
            window.Show();
        };
    }

    void OpenRequest(string? id)
    {
        if (id is null || closing) return;
        CloseRequest();
        var window = new HostPanelWindow(this, "RelaxDesk 连接请求", consent)
        {
            SizeToContent = SizeToContent.Height,
            MaxHeight = SystemParameters.WorkArea.Height * 0.85
        };
        requestWindow = window;
        window.Closing += (_, args) =>
        {
            if (ReferenceEquals(requestWindow, window) && !closing && Host.Pending && !deny.IsEnabled)
                args.Cancel = true;
        };
        window.Closed += async (_, _) =>
        {
            if (!ReferenceEquals(requestWindow, window)) return;
            requestWindow = null;
            if (!closing && Host.Pending && displayedSessionId == id) await Decide(false);
        };
        window.Show();
        window.Activate();
    }

    void CloseRequest()
    {
        var window = requestWindow;
        requestWindow = null;
        window?.Close();
    }
    static string[] Addresses()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).OrderByDescending(n=>n.GetIPProperties().GatewayAddresses.Count>0).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address.ToString()).Distinct().ToArray();
    internal bool RequestVisible => requestWindow?.IsVisible == true;
    internal bool ClipboardChoiceVisible => clipboardPermission.Visibility == Visibility.Visible;
    internal bool ClipboardAllowed { get => clipboardPermission.IsChecked == true; set => clipboardPermission.IsChecked = value; }
    internal string ClipboardRequestDescription=>clipboardPermission.Content is TextBlock label?label.Text:clipboardPermission.Content.ToString()??"";
    internal async Task StopClipboard(){string? id=displayedSessionId;if(id is null)return;await Host.RevokeClipboard(id);if(displayedSessionId!=id)return;UpdateSavedGrant(grant=>grant with{ClipboardFormats=[]});detail.Text="已撤销此设备的剪贴板权限。";}
    internal string Status=>state.Text;
    internal string? DesktopStatusNotice => detail.Text == desktopNotice ? desktopNotice : null;
    internal bool AdminDeploymentNoticeVisible => detail.Text == AdminDeploymentNotice;
    string ListeningDetail => desktopNotice ?? (adminDeploymentUnavailable ? AdminDeploymentNotice : adminStartupNotice ?? $"Viewer 可连接 {ip.Text}:{port.Text}，收到请求后由你确认。");
    internal async Task StartSharing(string? address=null,int? number=null,string backend="dxgi",bool recovering=false)
    {
        if(ServiceSetupBusy || savingLogin || checkingInputDeployment || Host.Running)return;
        if(!recovering){if(RestartPending)return;desktopNotice=null;hostTokenDenied=false;}
        if(ServiceRequired&&!ServiceReady){state.Text="服务协调不可用";detail.Text="请先连接已授权的系统服务；没有开始共享。";UpdateServiceControls();return;}
        if (InputDeploymentForVerification is not null && !await CheckInputDeployment()) return;
        if (closing || ServiceSetupBusy || ServiceRequired && !ServiceReady) return;
        automaticSessionCapabilities=!backend.StartsWith("test-",StringComparison.Ordinal);
        try{if(automaticSessionCapabilities){
                if(PasswordLoginSettings.Read() is null) throw new IOException("请先在「登录设置」保存用户名和密码。");
                EnsureDefaultFileRoot();
            }
            if (!recovering)
            {
                restartOrigin = ReadRecoveryDesktop();
                restartOrigin.EnsureAllowed();
                restartBudget = new();
                restartBackend = backend;
            }
            if(address is not null)ip.Text=address;if(number.HasValue)port.Text=number.Value.ToString();if(!int.TryParse(port.Text,out int selected))throw new ArgumentException("端口应为 1–65535");ip.IsEnabled=port.IsEnabled=start.IsEnabled=false;SetDisplayConfigurationEnabled(false);state.Text="正在启动";await Host.Start(application,ip.Text.Trim(),selected,backend,selectedFileRoot,SelectedDisplay,loginVerifierForTests);}
        catch(Exception e){state.Text="无法开始共享";detail.Text=e.Message;ip.IsEnabled=port.IsEnabled=start.IsEnabled=true;SetDisplayConfigurationEnabled(true);}
        finally{UpdateServiceControls();}
    }

    async Task<bool> CheckInputDeployment()
    {
        checkingInputDeployment = true;
        state.Text = "正在检查完整远程控制组件";
        UpdateServiceControls();
        try
        {
            var deployment = await Task.Run(InputDeploymentForVerification ?? InspectInputDeployment);
            if (closing || !IsLoaded) return false;
            adminDeploymentUnavailable = deployment != AdminWorkerDeployment.Verified;
            if (!adminDeploymentUnavailable) return true;
            state.Text = "完整远程控制不可用";
            detail.Text = AdminDeploymentNotice;
            return false;
        }
        finally
        {
            checkingInputDeployment = false;
            UpdateServiceControls();
        }
    }

    static AdminWorkerDeployment InspectInputDeployment()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } owner) return AdminWorkerDeployment.RepairRequired;
        try
        {
            AdminInputLauncher.CheckScheduledDeployment(owner.Value);
            return AdminWorkerDeployment.Verified;
        }
        catch (UnauthorizedAccessException) { return AdminWorkerDeployment.AccessDenied; }
        catch (Exception error) when (error is IOException or System.Security.SecurityException or System.Runtime.InteropServices.COMException or InvalidDataException)
        { return AdminWorkerDeployment.RepairRequired; }
    }
    internal async Task Decide(bool accepted)
    {
        string? id = displayedSessionId;
        if (id is null) return;
        allow.IsEnabled = deny.IsEnabled = false;
        bool clipboard = accepted && !automaticSessionCapabilities && !fullControlRequest && clipboardPermission.IsChecked == true;
        bool files = accepted && !automaticSessionCapabilities && !fullControlRequest && filesPermission.IsChecked == true && selectedFileRoot is not null;
        bool audio = accepted && !automaticSessionCapabilities && !fullControlRequest && audioPermission.IsChecked == true;
        if (accepted && passwordLoginRequest && activeDeviceFingerprint is { } fingerprint && !KnownDevice(fingerprint))
        {
            try
            {
                if (!automaticSessionCapabilities && loginVerifierForTests is null)
                    PasswordLoginSettings.SaveDeviceGrant(fingerprint, new(clipboard ? requestedClipboardFormats : [], audio, files ? selectedFileRoot : null));
                await SaveDeviceTrust(fingerprint);
            }
            catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
            { trustNotice.Text = "本次可连接，设备授权保存失败。"; }
            if (displayedSessionId != id || revokedDuringRun.Contains(fingerprint)) return;
        }
        activeInput = accepted;
        await Host.Decide(accepted, clipboard, files, id, audio);
        if (displayedSessionId != id) return;
        CloseRequest();
        if (CaptureDeviceTrust(accepted) is { } remember) await SaveDeviceTrust(remember);
    }
    internal async Task StopSharing()
    {
        if (stoppingSharing) return;
        stoppingSharing = true;
        CloseRequest();
        CancelRecovery();
        UpdateServiceControls();
        try { await Host.Stop(); }
        catch (Exception error) { detail.Text = error.Message; }
        finally { stoppingSharing = false; UpdateServiceControls(); }
    }
    internal Task DisconnectSession()=>displayedSessionId is {} id?Host.Disconnect(id):Task.CompletedTask;
    void Changed(HostEvent change)
    {
        if(change.Generation!=0&&change.Generation!=Host.Generation)return;
        string kind=change.Kind;string? value=change.Address;
        if ((kind is "admin-starting" or "admin-ready" or "admin-deployment-unavailable" or "admin-start-failed" or "admin-worker-lost" or
                "ordinary-input-blocked" or "ordinary-input-restored") &&
            (displayedSessionId is null || change.SessionId != displayedSessionId)) return;
        switch(kind){
            case "listening":CloseRequest();displayedSessionId=null;state.Text="等待连接";detail.Text=ListeningDetail;if(change.ServerFingerprint is {} serverPin)loginFingerprint.Text=serverPin;break;
            case "desktop-unavailable":
                if (change.DesktopReason == "host-input-token-denied") hostTokenDenied = true;
                detail.Text = desktopNotice = change.DesktopReason switch
                {
                    "host-input-token-denied" => "请使用普通 Windows 用户权限启动被控端，再开始共享。",
                    "session-locked" => "Windows 已锁屏。请在本机解锁后重新连接并授权。",
                    "physical-console-session-changed" => "Windows 已切换用户会话。请回到原用户桌面后重新连接并授权。",
                    "input-desktop-unavailable" => "Host 无法读取当前输入桌面。请回到普通桌面后重新连接并授权。",
                    "input-desktop-unsupported" or "thread-desktop-unsupported" => "Host 无法控制当前非默认桌面。请回到普通桌面后重新连接并授权。",
                    "expected-desktop-transition-expired" => "等待 Windows 返回普通桌面已超时。请在本机完成或取消授权后重新连接。",
                    _ => DesktopUnavailableNotice
                };
                break;
            case "native-approval-request":
                desktopNotice = null;
                displayedSessionId = change.SessionId;
                nativeApproval = fullControlRequest = true;
                activeInput = false;
                fullControlFeatures = change.Features ?? [];
                peer.Text = $"请求来源  {value}";
                ApplyDeviceTrust(change);
                CloseRequest();
                state.Text = "等待你的授权";
                detail.Text = "请在本机安全输入窗口核对设备指纹并确认。";
                break;
            case "approval-request":desktopNotice=null;displayedSessionId=change.SessionId;fullControlRequest = change.FullControl;passwordLoginRequest=change.PasswordLogin; activeInput = nativeApproval = false;
                requestedClipboardFormats = (change.Features ?? []).Where(SessionCapabilities.IsClipboard).ToArray();
                peer.Text = $"请求来源  {value}";
                peer.TextWrapping = TextWrapping.Wrap;
                if (change.DeviceFingerprint is { Length: 64 } device && device.All(char.IsAsciiHexDigit))
                    peer.Text += $"\n已验证设备公钥 SHA-256  {device}";
                consentAccess.Text = fullControlRequest ? "允许后，对方可控制此桌面并操作管理员权限窗口。请核对设备指纹后再批准。" : "允许后共享画面，并允许普通桌面的键鼠操作。";
                allow.IsEnabled=deny.IsEnabled=true;clipboardPermission.IsChecked=false;clipboardPermission.Content=new TextBlock{Text="允许双向剪贴板："+SessionCapabilities.ClipboardDescription(requestedClipboardFormats)+"（读取/替换两端新复制的内容）",TextWrapping=TextWrapping.Wrap};clipboardPermission.Visibility=!automaticSessionCapabilities&&requestedClipboardFormats.Length>0?Visibility.Visible:Visibility.Collapsed;ShowFileRequest((change.Features??[]).Contains(SessionCapabilities.FileTransfer));ShowAudioRequest(automaticSessionCapabilities ? [] : change.Features);ApplyDeviceTrust(change);
                if (automaticSessionCapabilities)
                {
                    var offered = SessionCapabilities.ForFullControl(change.Features, selectedFileRoot is not null);
                    string clipboard = SessionCapabilities.ClipboardDescription(offered.Where(feature => feature != SessionCapabilities.ClipboardFiles));
                    if (clipboard.Length > 0)
                        consentAccess.Text += "\n对方请求双向剪贴板：" + clipboard;
                    if (offered.Contains(SessionCapabilities.ClipboardFiles))
                        consentAccess.Text += "\n对方请求双向复制和粘贴文件。";
                    if (offered.Contains(SessionCapabilities.SystemAudio))
                        consentAccess.Text += "\n对方请求播放此电脑的系统声音。";
                }
                if (fullControlRequest)
                {
                    fullControlFeatures = SessionCapabilities.ForFullControl(change.Features, selectedFileRoot is not null);
                    clipboardPermission.Visibility = filesPermission.Visibility = audioPermission.Visibility = Visibility.Collapsed;
                    filesScopeLabel.Visibility = Visibility.Collapsed;
                    if (!automaticSessionCapabilities && requestedClipboardFormats.Length > 0)
                        consentAccess.Text += "\n双向剪贴板：" + SessionCapabilities.ClipboardDescription(requestedClipboardFormats);
                    if (!automaticSessionCapabilities && fullControlFeatures.Contains(SessionCapabilities.FileTransfer))
                        consentAccess.Text += "\n文件传输目录：" + selectedFileRoot;
                    if (!automaticSessionCapabilities && fullControlFeatures.Contains(SessionCapabilities.SystemAudio))
                        consentAccess.Text += "\n共享系统声音。";
                }
                if (passwordLoginRequest && activeDeviceFingerprint is { } approvedDevice && KnownDevice(approvedDevice))
                {
                    CloseRequest();
                    if (!automaticSessionCapabilities)
                    {
                        var grant = ReadDeviceGrant(approvedDevice);
                        clipboardPermission.IsChecked = requestedClipboardFormats.Length > 0 &&
                            grant.ClipboardFormats is { } formats && requestedClipboardFormats.All(formats.Contains);
                        filesPermission.IsChecked = selectedFileRoot is not null && grant.FileRoot == selectedFileRoot && Host.FilesRequested;
                        audioPermission.IsChecked = grant.Audio && Host.AudioRequested;
                    }
                    _ = Decide(true);
                }
                else if (change.ReconnectOnly)
                {
                    CloseRequest();
                    _ = Decide(false);
                }
                else
                {
                    if (passwordLoginRequest) consentAccess.Text += "\n允许后，此设备以后可凭正确密码无人值守连接。可在「受信任的设备」撤销。";
                    state.Text="等待你的授权";detail.Text="还未共享桌面或开放输入。";
                    OpenRequest(displayedSessionId);
                }
                break;
            case "admin-starting":
                adminStartupNotice = null;
                state.Text = "等待本机批准";
                detail.Text = "请在本机安全输入窗口核对设备指纹并确认。";
                break;
            case "admin-ready":adminDeploymentUnavailable=false;state.Text="正在共享";detail.Text="管理员输入代理已连接。断开会话会撤销输入。";break;
            case "admin-deployment-unavailable":adminDeploymentUnavailable=true;detail.Text=AdminDeploymentNotice;break;
            case "admin-start-failed":
                state.Text = "完整远程控制不可用";
                detail.Text = adminStartupNotice = "管理员输入代理未能就绪。请检查本机授权和系统服务，然后重新连接。";
                break;
            case "admin-worker-lost":
                state.Text = "管理员输入组件已断开";
                detail.Text = adminStartupNotice = "本次远程控制已结束。请检查输入组件状态后重新连接。";
                break;
            case "ordinary-input-blocked" when activeInput && !fullControlRequest:
                detail.Text = "Windows 暂时拒绝键鼠操作，画面仍在共享。请将焦点切回普通权限窗口。";
                break;
            case "ordinary-input-restored" when activeInput && !fullControlRequest:
                detail.Text = "普通桌面的键鼠操作已恢复。";
                break;
            case "approved":
                if (nativeApproval && displayedSessionId == change.SessionId)
                {
                    activeInput = true;
                }
                state.Text="正在建立共享";detail.Text="已允许本次连接，正在准备屏幕。";CloseRequest();break;
            case "denied":CloseRequest();state.Text="已拒绝连接";detail.Text="没有共享桌面。";break;
            case "streaming":state.Text="正在共享";detail.Text=fullControlRequest?"管理员输入代理已连接。断开会话会撤销输入。":activeInput?"对方可查看并操作普通桌面。":"键鼠输入未授权。";break;
            case "color":
                try
                {
                    using var d=JsonDocument.Parse(value!); var info=d.RootElement;
                    string transform=info.GetProperty("transform").GetString()??"SDR";
                    color.Text=transform=="SDR"&&info.GetProperty("hdr_active").GetBoolean()
                        ? "HDR 已开启，但当前是系统转换的 8 位图像，亮部精度可能受限"
                        : (transform=="SDR"?"SDR 桌面":"已启用 HDR → SDR · "+transform)+"  ·  SDR 白位 "+info.GetProperty("sdr_white_nits").GetSingle().ToString("F0")+" nits";
                    if(!info.GetProperty("white_level_known").GetBoolean()) color.Text+="（系统未提供白位，使用默认值）";
                }
                catch(JsonException){}break;
            case "session-ended":CloseRequest();displayedSessionId=null;state.Text="会话已结束";detail.Text="正在清理资源，随后继续等待新的连接。";break;
            case "ended":case "stopped":displayedSessionId=null;if(state.Text!="已拒绝连接")state.Text=kind=="stopped"?"已停止共享":"会话已结束";CloseRequest();ip.IsEnabled=port.IsEnabled=start.IsEnabled=true;SetDisplayConfigurationEnabled(true);break;
        }
        UpdateConnectedDeviceTrust();
        UpdateServiceControls();
        RecoverWorker(change);
    }
}
