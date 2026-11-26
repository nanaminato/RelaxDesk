using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Session;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly Button enableService=new(){Content="启用系统服务"};
    readonly ServiceSettingsView serviceSettings;
    Button installService => serviceSettings.InstallService;
    Button uninstallService => serviceSettings.UninstallService;
    Button refreshService => serviceSettings.RefreshService;
    ComboBox serviceStartup => serviceSettings.Startup;
    TextBlock serviceInstallation => serviceSettings.Installation;
    readonly TextBlock serviceSetupNotice=new(){Text="自动完成安装、启动和连接。首次启用需要 Windows 管理员授权；不会自动共享桌面。",TextWrapping=TextWrapping.Wrap};
    internal IServiceSetupBackend SetupBackend{get;set;}=new ServiceSetupBackend();
    internal Func<ServiceSetupPlan,bool>? SetupConfirmation{get;set;}
    internal bool ServiceSetupBusy{get;private set;}
    internal string SetupNotice=>serviceSetupNotice.Text;
    internal string InstallationStatus=>serviceInstallation.Text;
    internal bool SetupAllowsSharing=>!Host.Running&&!RestartPending&&start.IsEnabled;
    internal Button InstallServiceButton=>installService;
    internal Button EnableServiceButton=>enableService;
    internal Button UninstallServiceButton=>uninstallService;
    internal ComboBox ServiceStartupChoice=>serviceStartup;
    bool serviceInspecting,serviceSetupClosed,serviceInspectAgain;
    int serviceSetupGeneration;
    void AddServiceSetup(Panel panel)
    {
        var shortcuts = new WrapPanel();
        shortcuts.Children.Add(enableService);
        panel.Children.Add(shortcuts);
        panel.Children.Add(serviceSetupNotice);
        enableService.Click+=async(_,_)=>await EnableSystemService();
        AddWindowButton(shortcuts, "服务设置", serviceSettings);
        installService.Click+=async(_,_)=>await RunServiceSetup(false);
        uninstallService.Click+=async(_,_)=>await RunServiceSetup(true);
        refreshService.Click+=async(_,_)=>await InspectServiceInstallation();
        Loaded+=async(_,_)=>await InspectServiceInstallation();
        Closed+=(_,_)=>serviceSetupClosed=true;
    }
    void UpdateServiceSetupControls()
    {
        bool idle=!Host.Running&&!checkingInputDeployment&&!RestartPending&&!serviceConnecting&&!serviceCleaning&&!closing&&!ServiceSetupBusy;
        enableService.IsEnabled=idle&&!ServiceReady;enableService.Content=ServiceReady?"系统服务已启用":ServiceSetupBusy?"正在处理…":"启用系统服务";
        installService.IsEnabled=uninstallService.IsEnabled=serviceStartup.IsEnabled=idle;
        refreshService.IsEnabled=!ServiceSetupBusy&&!serviceInspecting&&!closing;
    }
    internal Task EnableSystemService()=>RunServiceSetup(false,true);
    async Task CompleteServiceEnable()
    {
        ServiceRequired=true;serviceSetupNotice.Text="正在连接系统服务…";
        serviceConnectingOperation=ConnectServiceCore(SetupBackend.Connect,true);await serviceConnectingOperation;
        if(ServiceReady){serviceStatus.Text="系统服务已启用。";serviceSetupNotice.Text="已启用，点击“开始共享”即可。";}
        else serviceSetupNotice.Text="服务连接失败。请点击“启用系统服务”重试；尚未开始共享。";
    }
    internal async Task InspectServiceInstallation()
    {
        if(serviceSetupClosed||ServiceSetupBusy)return;
        if(serviceInspecting){serviceInspectAgain=true;return;}
        int generation=serviceSetupGeneration;
        serviceInspectAgain=false;
        serviceInspecting=true;UpdateServiceSetupControls();
        try
        {
            var status=await SetupBackend.Inspect();if(serviceSetupClosed)return;
            if(generation!=serviceSetupGeneration){serviceInspectAgain=true;return;}
            if (status.WorkerDeployment is { } workerDeployment)
            {
                adminDeploymentUnavailable = workerDeployment != AdminWorkerDeployment.Verified;
                if (state.Text == "等待连接") detail.Text = ListeningDetail;
            }
            serviceInstallation.Text=!status.Installed?"系统服务未安装（普通用户模式仍可使用）。":status.PendingDelete?"服务正在等待删除；关闭服务管理窗口后再次卸载，保留现有文件。":$"服务已安装 · {(status.State==4?"运行中":status.State==1?"已停止":"状态转换中")} · {(status.Startup==2?"开机自动启动":"手动启动")}";
            serviceInstallation.Text += status.WorkerDeployment switch
            {
                AdminWorkerDeployment.Verified => "\n管理员输入代理部署已验证。每次会话仍需本机授权。",
                AdminWorkerDeployment.AccessDenied => "\n当前用户无法读取管理员输入代理部署。请通过下方按钮更新或修复。",
                AdminWorkerDeployment.RepairRequired => "\n管理员输入代理部署需要修复。请通过下方按钮更新或修复。",
                _ => ""
            };
        }
        catch(Exception error){if(!serviceSetupClosed&&generation==serviceSetupGeneration)serviceInstallation.Text="无法验证当前用户的服务状态；请检查账户授权。错误类型："+error.GetType().Name;}
        finally{serviceInspecting=false;if(!serviceSetupClosed){UpdateServiceSetupControls();if(serviceInspectAgain&&!ServiceSetupBusy){serviceInspectAgain=false;await InspectServiceInstallation();}}}
    }
    internal async Task RunServiceSetup(bool uninstall,bool simpleEnable=false)
    {
        if(ServiceSetupBusy||Host.Running||RestartPending||serviceConnecting||serviceCleaning||closing||serviceSetupClosed)return;
        if(simpleEnable&&ServiceReady)return;
        ServiceSetupBusy=true;++serviceSetupGeneration;UpdateServiceControls();
        string step = "校验发布包";
        serviceSetupNotice.Text="正在校验完整发布包；尚未请求管理员授权…";
        try
        {
            if(simpleEnable)
            {
                step = "读取系统服务状态";
                serviceSetupNotice.Text="正在检查系统服务…";var current=await SetupBackend.Inspect();
                if(current.PendingDelete){serviceSetupNotice.Text="服务正在等待删除，请稍后重试或到“服务设置”完成卸载。";return;}
                if(current.Installed&&current.State==4){await CompleteServiceEnable();return;}
                if(current.Installed&&current.State!=1){serviceSetupNotice.Text="服务正在启动或停止，请稍后再次点击启用。";return;}
                serviceSetupNotice.Text="正在准备系统服务…";
            }
            step = "校验发布包";
            bool? automatic=serviceStartup.SelectedIndex==0?null:serviceStartup.SelectedIndex==2;
            // The simple action preserves installed startup policy. Advanced
            // choices are applied only by the explicit repair/update action.
            var plan=await SetupBackend.Prepare(application,uninstall,simpleEnable?null:automatic);
            if(simpleEnable)plan=plan with{OnlyIssues=true};
            string action=uninstall?"卸载系统服务（会停止服务协调，保留未知或修改过的文件）":$"安装／更新并立即启动系统服务\n启动设置：{(automatic is null?"保留当前设置，首次安装为手动":automatic.Value?"开机自动启动":"手动启动")}";
            bool accepted=simpleEnable||(SetupConfirmation?.Invoke(plan)??MessageBox.Show(this,$"{action}\n\n授权用户 SID：{plan.Owner}\n发布版本：{plan.Version}\n\n不会自动共享桌面或批准 Viewer。接下来 Windows 将请求管理员授权。", "RelaxDesk 服务维护",MessageBoxButton.OKCancel,MessageBoxImage.Warning)==MessageBoxResult.OK);
            if(!accepted){serviceSetupNotice.Text="已取消，没有启动服务安装程序。";return;}
            await DisconnectService();
            step = "启动管理员维护程序";
            serviceSetupNotice.Text="正在等待 Windows 授权和服务维护完成；不会开始共享。完成前请保留窗口。";
            int code=await SetupBackend.Run(plan);
            if(simpleEnable&&code==0){await CompleteServiceEnable();return;}
            if(uninstall&&code==0)ServiceRequired=false;
            serviceSetupNotice.Text=code==0?(uninstall?"卸载操作已完成；如提示保留文件，请按结果窗口检查。":"服务更新／修复已完成。点击“启用系统服务”自动连接，然后开始共享。"):
                code==2?"服务等待删除；保留文件，关闭相关服务管理窗口后再次卸载。":$"服务维护未成功完成（退出码 {code}）。请查看维护结果窗口；可能保留了恢复记录，不要手动删除服务目录。";
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223){serviceSetupNotice.Text="已取消 Windows 管理员授权，没有启动服务维护。";}
        catch(Exception error)
        {
            serviceSetupNotice.Text=$"{step}失败：{error.GetType().Name} (0x{error.HResult:X8})。";
            logs.AppendText($"service_setup_failed step={step} type={error.GetType().Name} hresult=0x{error.HResult:X8}\n");
        }
        finally{ServiceSetupBusy=false;UpdateServiceControls();await InspectServiceInstallation();}
    }
}
