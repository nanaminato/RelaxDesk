using RelaxDesk.SharedUI;
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
    static string AdminDeploymentNotice => Strings.Get("Ui.HostWindow.Text176");
    static string DesktopUnavailableNotice =>Strings.Get("Ui.HostWindow.Text079");
    string? displayedSessionId;
    public HostWindow(string? hostApplication=null, DeviceTrustStore? trustStore=null)
    {
        deviceTrust = trustStore ?? DeviceTrustStore.CurrentUser();
        application=hostApplication??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Host","RelaxDesk.Host.exe"));
        InitializeComponent();
        logs = (TextBox)Resources["HostLogPanel"];
        BuildVersion.Text = BuildIdentity.Display;
        var addresses = Addresses();
        ip.Text = addresses.FirstOrDefault() ?? "127.0.0.1";
        if (addresses.Length > 1)
        {
            foreach (string address in addresses)
            {
                var choice = new Button { Content = address,Padding = new(10, 5, 10, 5),FontSize = 12 };
                choice.Click += (_, _) => { if (!Host.Running && !RestartPending) ip.Text = address; };
                addressChoices.Children.Add(choice);
            }
        }

        ConfigureDisplaySettings();
        ConfigureLoginSettings();
        ConfigureSettingsWindow(openLogin, "Host.LoginSettings", loginSettings);
        ConfigureServiceSettings();
        ConfigureDeviceTrust();
        ConfigureSettingsWindow(openDevices, "Host.TrustedDevices", trustedDeviceSettings);
        ConfigureSettingsWindow(openLogs, "Host.DiagnosticsAndLogs", CreateDiagnosticsPanel());
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
        Host.Changed+=change=>Dispatcher.BeginInvoke(()=>Changed(change));Host.Diagnostic+=line=>Dispatcher.BeginInvoke(()=>{logs.Text=string.Join(Environment.NewLine,Host.Snapshot());if(line.StartsWith("producer_error")||line.StartsWith("Session ended:")||line.StartsWith("clipboard_")||line.StartsWith("file_transfer_disabled"))detail.Text=line;if(line.StartsWith("audio_disabled")){LocalizedText.Bind(detail,"Text","Ui.AudioSession.Text164");}});
        Closing+=async(_,e)=>{if(ServiceSetupBusy){e.Cancel=true;LocalizedText.Bind(serviceSetupNotice,"Text","Ui.HostWindow.Text353");return;}CancelRecovery();if(!Host.Running&&serviceAgent is null&&serviceAttempt is null&&serviceWatch is null)return;e.Cancel=true;if(closing)return;closing=true;await DisconnectService();await StopSharing();Close();};
    }
    bool showingSettings;
    void ConfigureLanguageSelector()
    {
        var manager = LocalizationManager.Current;
        languageSelector.ItemsSource = manager.AvailableLanguages;
        languageSelector.SelectedValue = manager.CurrentLanguageCode;
        languageSelector.SelectionChanged += (_, _) =>
        {
            if (languageSelector.SelectedItem is LanguageOption option && !string.Equals(option.Code, manager.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                manager.SetLanguage(option.Code);
        };
        manager.LanguageChanged += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                LocalizedText.Bind(PageTitle,"Text",showingSettings ? "Host.PageTitleSettings" : "Host.PageTitleSharing");
                if (!string.Equals(languageSelector.SelectedValue as string, manager.CurrentLanguageCode, StringComparison.OrdinalIgnoreCase))
                    languageSelector.SelectedValue = manager.CurrentLanguageCode;
            });
        };
    }

    internal void ShowPage(bool settings, FrameworkElement? section = null)
    {
        showingSettings = settings;
        LocalizedText.Bind(PageTitle,"Text",settings ? "Host.PageTitleSettings" : "Host.PageTitleSharing");
        (settings ? connectionNavigation : settingsNavigation).Style = (Style)Resources["NavButton"];
        (settings ? settingsNavigation : connectionNavigation).Style = (Style)Resources["NavButtonActive"];
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

    UIElement CreateDiagnosticsPanel()
    {
        var panel = new StackPanel();
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var copyButton = LocalizedText.Bind(new Button
        {
            VerticalAlignment = VerticalAlignment.Center,Margin = new Thickness(12, 0, 0, 0)
        },"Content","Ui.HostWindow.Text167");
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(logs.Text ?? "");
                LocalizedText.Bind(copyButton,"Content","Ui.HostWindow.Text214");
            }
            catch { }
        };
        DockPanel.SetDock(copyButton, Dock.Right);
        header.Children.Add(copyButton);

        var titles = new StackPanel();
        titles.Children.Add(LocalizedText.Bind(new TextBlock
        {
            FontSize = 20,FontWeight = FontWeights.Bold,Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 23, 42))
        },"Text","Host.DiagnosticsAndLogs"));
        titles.Children.Add(LocalizedText.Bind(new TextBlock
        {
            FontSize = 13,TextWrapping = TextWrapping.Wrap,Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),Margin = new Thickness(0, 4, 0, 0)
        },"Text","Ui.HostWindow.Text178"));
        header.Children.Add(titles);
        panel.Children.Add(header);
        panel.Children.Add(logs);
        return panel;
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
        var window = new HostPanelWindow(this, Strings.Get("Ui.HostWindow.Text048"), consent)
        {
            SizeToContent = SizeToContent.Height,            MaxHeight = SystemParameters.WorkArea.Height * 0.85
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
    internal string Status=>state.Text;
    internal string? DesktopStatusNotice=>detail.Text==desktopNotice?desktopNotice:null;
    internal bool AdminDeploymentNoticeVisible=>detail.Text==AdminDeploymentNotice;
    string ListeningDetail => desktopNotice ?? (adminDeploymentUnavailable ? AdminDeploymentNotice : adminStartupNotice ?? Strings.Format("Ui.HostWindow.Text052",ip.Text,port.Text));
    internal async Task StartSharing(string? address=null,int? number=null,string backend="dxgi",bool recovering=false)
    {
        if(ServiceSetupBusy || savingLogin || checkingInputDeployment || Host.Running)return;
        if(!recovering){if(RestartPending)return;desktopNotice=null;hostTokenDenied=false;}
        if(ServiceRequired&&!ServiceReady){LocalizedText.Bind(state,"Text","Ui.HostWindow.Text311");LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text454");UpdateServiceControls();return;}
        if (InputDeploymentForVerification is not null && !await CheckInputDeployment()) return;
        if (closing || ServiceSetupBusy || ServiceRequired && !ServiceReady) return;
        try{if(!backend.StartsWith("test-",StringComparison.Ordinal)){
                if(PasswordLoginSettings.Read() is null) throw new IOException(Strings.Get("Ui.HostWindow.Text452"));
            }
            if(selectedFileRoot is null)EnsureDefaultFileRoot();
            if (!recovering)
            {
                restartOrigin = ReadRecoveryDesktop();
                restartOrigin.EnsureAllowed();
                restartBudget = new();
                restartBackend = backend;
            }
            if(address is not null)ip.Text=address;if(number.HasValue)port.Text=number.Value.ToString();if(!int.TryParse(port.Text,out int selected))throw new ArgumentException(Strings.Get("Ui.HostWindow.Text398"));ip.IsEnabled=port.IsEnabled=start.IsEnabled=false;SetDisplayConfigurationEnabled(false);LocalizedText.Bind(state,"Text","Ui.HostWindow.Text350");await Host.Start(application,ip.Text.Trim(),selected,backend,selectedFileRoot,SelectedDisplay,loginVerifierForTests);}
        catch(Exception e){LocalizedText.Bind(state,"Text","Ui.HostWindow.Text287");detail.Text=e.Message;ip.IsEnabled=port.IsEnabled=start.IsEnabled=true;SetDisplayConfigurationEnabled(true);}
        finally{UpdateServiceControls();}
    }

    async Task<bool> CheckInputDeployment()
    {
        checkingInputDeployment = true;
        LocalizedText.Bind(state,"Text","Ui.HostWindow.Text360");
        UpdateServiceControls();
        try
        {
            var deployment = await Task.Run(InputDeploymentForVerification ?? InspectInputDeployment);
            if (closing || !IsLoaded) return false;
            adminDeploymentUnavailable = deployment != AdminWorkerDeployment.Verified;
            if (!adminDeploymentUnavailable) return true;
            LocalizedText.Bind(state,"Text","Ui.HostWindow.Text175");
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
        if (accepted && passwordLoginRequest && activeDeviceFingerprint is { } fingerprint && !KnownDevice(fingerprint))
        {
            try
            {
                await SaveDeviceTrust(fingerprint);
            }
            catch (Exception error) when (IsTrustStorageError(error) || error is System.Security.SecurityException)
            { LocalizedText.Bind(trustNotice,"Text","Ui.HostWindow.Text336"); }
            if (displayedSessionId != id || revokedDuringRun.Contains(fingerprint)) return;
        }
        activeInput = accepted;
        await Host.Decide(accepted,id);
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
            case "listening":CloseRequest();displayedSessionId=null;LocalizedText.Bind(state,"Text","Ui.HostWindow.Text408");detail.Text=ListeningDetail;if(change.ServerFingerprint is {} serverPin)loginFingerprint.Text=serverPin;break;
            case "desktop-unavailable":
                if (change.DesktopReason == "host-input-token-denied") hostTokenDenied = true;
                detail.Text = desktopNotice = change.DesktopReason switch
                {
                    "host-input-token-denied" => Strings.Get("Ui.HostWindow.Text450"),
                    "session-locked" => Strings.Get("Ui.HostWindow.Text054"),
                    "physical-console-session-changed" => Strings.Get("Ui.HostWindow.Text053"),
                    "input-desktop-unavailable" => Strings.Get("Ui.HostWindow.Text033"),
                    "input-desktop-unsupported" or "thread-desktop-unsupported" => Strings.Get("Ui.HostWindow.Text032"),
                    "expected-desktop-transition-expired" => Strings.Get("Ui.HostWindow.Text400"),
                    _ => DesktopUnavailableNotice
                };
                break;
            case "native-approval-request":
                desktopNotice = null;
                displayedSessionId = change.SessionId;
                nativeApproval = fullControlRequest = true;
                activeInput = false;
                LocalizedText.Bind(peer,"Text","Ui.HostWindow.Text458",value);
                ApplyDeviceTrust(change);
                CloseRequest();
                LocalizedText.Bind(state,"Text","Ui.HostWindow.Text402");
                LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text456");
                break;
            case "approval-request":desktopNotice=null;displayedSessionId=change.SessionId;fullControlRequest = change.FullControl;passwordLoginRequest=change.PasswordLogin; activeInput = nativeApproval = false;
                LocalizedText.Bind(peer,"Text","Ui.HostWindow.Text458",value);
                if(change.DeviceFingerprint is {Length:64} device)peer.Text+=Strings.Format("Ui.HostWindow.Text010",device);
                LocalizedText.Bind(consentAccess,"Text","Session.AllAccess");
                allow.IsEnabled=deny.IsEnabled=true;ApplyDeviceTrust(change);
                if (passwordLoginRequest && activeDeviceFingerprint is { } approvedDevice && KnownDevice(approvedDevice))
                {
                    CloseRequest();
                    _ = Decide(true);
                }
                else if (change.ReconnectOnly)
                {
                    CloseRequest();
                    _ = Decide(false);
                }
                else
                {
                    if (passwordLoginRequest) consentAccess.Text += Strings.Get("Ui.HostWindow.Text003");
                    LocalizedText.Bind(state,"Text","Ui.HostWindow.Text402");LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text474");
                    OpenRequest(displayedSessionId);
                }
                break;
            case "admin-starting":
                adminStartupNotice = null;
                LocalizedText.Bind(state,"Text","Ui.HostWindow.Text405");
                LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text456");
                break;
            case "admin-ready":adminDeploymentUnavailable=false;LocalizedText.Bind(state,"Text","Ui.HostWindow.Text345");LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text411");break;
            case "admin-deployment-unavailable":adminDeploymentUnavailable=true;detail.Text=AdminDeploymentNotice;break;
            case "admin-start-failed":
                LocalizedText.Bind(state,"Text","Ui.HostWindow.Text175");
                detail.Text = adminStartupNotice = Strings.Get("Ui.HostWindow.Text412");
                break;
            case "admin-worker-lost":
                LocalizedText.Bind(state,"Text","Ui.HostWindow.Text413");
                detail.Text = adminStartupNotice = Strings.Get("Ui.HostWindow.Text340");
                break;
            case "ordinary-input-blocked" when activeInput && !fullControlRequest:
                LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text055");
                break;
            case "ordinary-input-restored" when activeInput && !fullControlRequest:
                LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text304");
                break;
            case "approved":
                if (nativeApproval && displayedSessionId == change.SessionId)
                {
                    activeInput = true;
                }
                LocalizedText.Bind(state,"Text","Ui.HostWindow.Text355");LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text199");CloseRequest();break;
            case "denied":CloseRequest();LocalizedText.Bind(state,"Text","Ui.HostWindow.Text218");LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text373");break;
            case "streaming":LocalizedText.Bind(state,"Text","Ui.HostWindow.Text345");LocalizedText.Bind(detail,"Text",()=>fullControlRequest?Strings.Get("Ui.HostWindow.Text411"):activeInput?Strings.Get("Ui.HostWindow.Text180"):Strings.Get("Ui.HostWindow.Text503"));break;
            case "color":
                try
                {
                    using var d=JsonDocument.Parse(value!); var info=d.RootElement;
                    string transform=info.GetProperty("transform").GetString()??"SDR";
                    LocalizedText.Bind(color,"Text",()=>transform=="SDR"&&info.GetProperty("hdr_active").GetBoolean()
                        ? Strings.Get("Ui.HostWindow.Text025")
                        : (transform=="SDR"?Strings.Get("Ui.HostWindow.Text049"):Strings.Get("Ui.HostWindow.Text211")+transform)+Strings.Get("Ui.HostWindow.Text016")+info.GetProperty("sdr_white_nits").GetSingle().ToString("F0")+" nits");
                    if(!info.GetProperty("white_level_known").GetBoolean()) color.Text+=Strings.Get("Ui.HostWindow.Text518");
                }
                catch(JsonException){}break;
            case "session-ended":CloseRequest();displayedSessionId=null;LocalizedText.Bind(state,"Text","Ui.HostWindow.Text089");LocalizedText.Bind(detail,"Text","Ui.HostWindow.Text361");break;
            case "ended":case "stopped":displayedSessionId=null;if(state.Text!=Strings.Get("Ui.HostWindow.Text218"))LocalizedText.Bind(state,"Text",()=>kind=="stopped"?Strings.Get("Ui.HostWindow.Text198"):Strings.Get("Ui.HostWindow.Text089"));CloseRequest();ip.IsEnabled=port.IsEnabled=start.IsEnabled=true;SetDisplayConfigurationEnabled(true);break;
        }
        UpdateConnectedDeviceTrust();
        UpdateServiceControls();
        RecoverWorker(change);
    }
}
