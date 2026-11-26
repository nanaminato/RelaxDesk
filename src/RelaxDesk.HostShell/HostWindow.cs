using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;
namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow:Window
{
    internal readonly HostProcess Host=new();
    readonly TextBox ip=new(),port=new(){Text="48200"};
    readonly TextBlock state=new(){Text="尚未共享"},detail=new(){Text="选择本机地址，开始等待连接请求。",TextWrapping=TextWrapping.Wrap},peer=new(),color=new(){Text="HDR 自动转为 SDR · 全程加密传输",Foreground=Brushes.SlateGray};
    readonly Button start=new(){Content="开始共享"},allow=new(){Content="允许本次连接"},deny=new(){Content="拒绝"};
    internal Button SharingAction => start;
    readonly Border request=new(){Visibility=Visibility.Collapsed};
    readonly CheckBox clipboardPermission=new(){Content="允许双向文本剪贴板（可读取两端新复制的文本，最大64KiB）",IsChecked=false,Visibility=Visibility.Collapsed,Margin=new(0,10,0,0)};
    readonly TextBlock consentAccess = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray };
    bool fullControlRequest;
    bool nativeApproval, stoppingSharing;
    bool activeInput;
    readonly TextBox logs=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=180,FontFamily=new("Consolas"),FontSize=11};
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
        serviceSettings = new ServiceSettingsView(serviceSetupNotice);
        RelaxDesk.Ui.ModernTheme.Apply(this);
        BuildVersion.Text = BuildIdentity.Display;
        var content = SharingContent;
        var statusContent=new StackPanel();state.FontSize=19;state.FontWeight=FontWeights.SemiBold;state.Foreground=Brush("#17243A");statusContent.Children.Add(state);detail.Margin=new(0,8,0,0);detail.Foreground=Brushes.SlateGray;statusContent.Children.Add(detail);content.Children.Add(Card(statusContent));
        var settings=new StackPanel();settings.Children.Add(Text("连接信息",17,true));settings.Children.Add(Text("Viewer 只需填写下面的 IP 和端口",12,false,Brushes.SlateGray));
        var fields=new Grid{Margin=new(0,12,0,0)};fields.ColumnDefinitions.Add(new());fields.ColumnDefinitions.Add(new(){Width=new(135)});
        var ipField=new StackPanel{Margin=new(0,0,16,0)};ipField.Children.Add(Text("本机局域网 IP",12));ipField.Children.Add(ip);fields.Children.Add(ipField);
        var portField=new StackPanel();portField.Children.Add(Text("端口",12));portField.Children.Add(port);Grid.SetColumn(portField,1);fields.Children.Add(portField);settings.Children.Add(fields);
        var addresses=Addresses();ip.Text=addresses.FirstOrDefault()??"127.0.0.1";
        if(addresses.Length>1){var options=new WrapPanel();foreach(string address in addresses){var pick=new Button{Content=address,Padding=new(10,5,10,5),FontSize=12};pick.Click+=(_,_)=>{if(!Host.Running&&!RestartPending)ip.Text=address;};options.Children.Add(pick);}settings.Children.Add(options);}
        var sharingSettings = new StackPanel();
        AddDisplaySettings(sharingSettings);
        AddRecoverySettings(sharingSettings);
        AddWindowButton(Navigation, "共享设置", sharingSettings);
        AddLoginSettings(Navigation);
        var devices = new StackPanel();
        BuildDeviceTrust(devices);
        AddWindowButton(Navigation, "受信任的设备", devices);
        AddWindowButton(Navigation, "诊断与日志", logs);
        var actions=new WrapPanel();start.Style=(Style)Resources["PrimaryButton"];
        actions.Children.Add(start);settings.Children.Add(actions);color.Margin=new(4,12,0,0);color.TextWrapping=TextWrapping.Wrap;settings.Children.Add(color);var settingsCard=Card(settings);
        var consent=new StackPanel();consent.Children.Add(Text("有人请求连接",19,true));peer.FontSize=16;peer.Margin=new(0,10,0,8);consent.Children.Add(peer);consent.Children.Add(consentAccess);var decision=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,14,0,0)};allow.Style=(Style)Resources["PrimaryButton"];deny.Style=(Style)Resources["DangerButton"];decision.Children.Add(allow);decision.Children.Add(deny);consent.Children.Add(decision);request.Child=consent;request.Background=Brush("#EAF2FF");request.CornerRadius=new(12);request.Padding=new(22);request.Margin=new(0,0,0,16);content.Children.Add(request);content.Children.Add(settingsCard);
        consent.Children.Insert(3,clipboardPermission);consent.Children.Insert(4,filesPermission);consent.Children.Insert(5,filesScopeLabel);consent.Children.Insert(6,audioPermission);consent.Children.Insert(7,rememberDevice);
        var servicePanel = new StackPanel();
        AddServiceSettings(servicePanel);
        content.Children.Add(Card(servicePanel));
        start.Click += async (_, _) =>
        {
            if (Host.Running || RestartPending) await StopSharing();
            else await StartSharing();
        };
        allow.Click+=async(_,_)=>await Decide(true);deny.Click+=async(_,_)=>await Decide(false);
        Host.Changed+=change=>Dispatcher.BeginInvoke(()=>Changed(change));Host.Diagnostic+=line=>Dispatcher.BeginInvoke(()=>{logs.Text=string.Join(Environment.NewLine,Host.Snapshot());if(line.StartsWith("producer_error")||line.StartsWith("Session ended:")||line.StartsWith("clipboard_")||line.StartsWith("file_transfer_disabled"))detail.Text=line;if(line.StartsWith("audio_disabled")){detail.Text="声音传输已关闭；画面与输入仍可使用。";}});
        Closing+=async(_,e)=>{if(ServiceSetupBusy){e.Cancel=true;serviceSetupNotice.Text="正在处理系统服务，请等待完成后再关闭；如出现 Windows 授权，可在那里取消。";return;}CancelRecovery();if(!Host.Running&&serviceAgent is null&&serviceAttempt is null&&serviceWatch is null)return;e.Cancel=true;if(closing)return;closing=true;await DisconnectService();await StopSharing();Close();};
    }
    void AddWindowButton(Panel parent, string title, UIElement body)
    {
        var button = new Button { Content = title, Margin = new(0, 8, 0, 0) };
        Window? dialog = null;
        button.Click += (_, _) =>
        {
            if (dialog is not null) { dialog.Activate(); return; }
            var window = new HostPanelWindow(this, title, body);
            window.Closed += (_, _) => dialog = null;
            dialog = window;
            window.Show();
        };
        parent.Children.Add(button);
    }
    static SolidColorBrush Brush(string value)=>new((Color)ColorConverter.ConvertFromString(value));
    static TextBlock Text(string value,double size=14,bool bold=false,Brush? foreground=null)=>new(){Text=value,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=foreground??Brush("#17243A"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,4)};
    static Border Card(UIElement child)=>new(){Child=child,Background=Brushes.White,CornerRadius=new(12),Padding=new(22),Margin=new(0,0,0,16)};
    static string[] Addresses()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).OrderByDescending(n=>n.GetIPProperties().GatewayAddresses.Count>0).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address.ToString()).Distinct().ToArray();
    internal bool RequestVisible=>request.Visibility==Visibility.Visible;
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
        if(!recovering){if(RestartPending)return;restartBudget=new();restartBackend=backend;desktopNotice=null;hostTokenDenied=false;}
        if(ServiceRequired&&!ServiceReady){state.Text="服务协调不可用";detail.Text="请先连接已授权的系统服务；没有开始共享。";UpdateServiceControls();return;}
        if (InputDeploymentForVerification is not null && !await CheckInputDeployment()) return;
        if (closing || ServiceSetupBusy || ServiceRequired && !ServiceReady) return;
        automaticSessionCapabilities=!backend.StartsWith("test-",StringComparison.Ordinal);
        try{if(automaticSessionCapabilities){
                if(PasswordLoginSettings.Read() is null) throw new IOException("请先在「登录设置」保存用户名和密码。");
                EnsureDefaultFileRoot();
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
        request.Visibility = Visibility.Collapsed;
        if (CaptureDeviceTrust(accepted) is { } remember) await SaveDeviceTrust(remember);
    }
    internal async Task StopSharing()
    {
        if (stoppingSharing) return;
        stoppingSharing = true;
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
            case "listening":request.Visibility=Visibility.Collapsed;displayedSessionId=null;state.Text="等待连接";detail.Text=ListeningDetail;if(change.ServerFingerprint is {} serverPin)loginFingerprint.Text=serverPin;break;
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
                request.Visibility = Visibility.Collapsed;
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
                request.Visibility=Visibility.Visible;allow.IsEnabled=deny.IsEnabled=true;clipboardPermission.IsChecked=false;clipboardPermission.Content=new TextBlock{Text="允许双向剪贴板："+SessionCapabilities.ClipboardDescription(requestedClipboardFormats)+"（读取/替换两端新复制的内容）",TextWrapping=TextWrapping.Wrap};clipboardPermission.Visibility=!automaticSessionCapabilities&&requestedClipboardFormats.Length>0?Visibility.Visible:Visibility.Collapsed;ShowFileRequest((change.Features??[]).Contains(SessionCapabilities.FileTransfer));ShowAudioRequest(automaticSessionCapabilities ? [] : change.Features);ApplyDeviceTrust(change);
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
                    request.Visibility = Visibility.Collapsed;
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
                    request.Visibility = Visibility.Collapsed;
                    _ = Decide(false);
                }
                else
                {
                    if (passwordLoginRequest) consentAccess.Text += "\n允许后，此设备以后可凭正确密码无人值守连接。可在「受信任的设备」撤销。";
                    state.Text="等待你的授权";detail.Text="还未共享桌面或开放输入。";
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
                state.Text="正在建立共享";detail.Text="已允许本次连接，正在准备屏幕。";request.Visibility=Visibility.Collapsed;break;
            case "denied":state.Text="已拒绝连接";detail.Text="没有共享桌面。";break;
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
            case "session-ended":request.Visibility=Visibility.Collapsed;displayedSessionId=null;state.Text="会话已结束";detail.Text="正在清理资源，随后继续等待新的连接。";break;
            case "ended":case "stopped":displayedSessionId=null;if(state.Text!="已拒绝连接")state.Text=kind=="stopped"?"已停止共享":"会话已结束";request.Visibility=Visibility.Collapsed;ip.IsEnabled=port.IsEnabled=start.IsEnabled=true;SetDisplayConfigurationEnabled(true);break;
        }
        UpdateConnectedDeviceTrust();
        UpdateServiceControls();
        RecoverWorker(change);
    }
}
