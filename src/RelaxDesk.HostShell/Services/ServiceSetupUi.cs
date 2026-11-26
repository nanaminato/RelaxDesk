using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Session;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    Button enableService => serviceSettings.EnableService;
    Button installService => serviceSettings.InstallService;
    Button uninstallService => serviceSettings.UninstallService;
    Button refreshService => serviceSettings.RefreshService;
    ComboBox serviceStartup => serviceSettings.Startup;
    TextBlock serviceInstallation => serviceSettings.Installation;
    TextBlock serviceSetupNotice => serviceSettings.Progress;
    MachineServiceStatus? serviceSnapshot;
    bool serviceStatusUnavailable;
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
    void ConfigureServiceSetup()
    {
        enableService.Click += async (_, _) => await EnableSystemService();
        installService.Click+=async(_,_)=>await RunServiceSetup(false);
        uninstallService.Click+=async(_,_)=>await RunServiceSetup(true);
        refreshService.Click+=async(_,_)=>await InspectServiceInstallation();
        Loaded+=async(_,_)=>await InspectServiceInstallation();
        Closed+=(_,_)=>serviceSetupClosed=true;
    }
    void UpdateServiceSetupControls()
    {
        bool idle=!Host.Running&&!checkingInputDeployment&&!RestartPending&&!serviceConnecting&&!serviceCleaning&&!closing&&!ServiceSetupBusy;
        enableService.IsEnabled=idle&&!ServiceReady;LocalizedText.Bind(enableService,"Content",ServiceReady?"Ui.ServiceSetupUi.Text419":ServiceSetupBusy?"Ui.ServiceSetupUi.Text352":"Ui.ServiceSetupUi.Text156");
        installService.IsEnabled=uninstallService.IsEnabled=serviceStartup.IsEnabled=idle;
        refreshService.IsEnabled=!ServiceSetupBusy&&!serviceInspecting&&!closing;
        string overviewKey = ServiceSetupBusy || serviceConnecting ? "Ui.ServiceSetupUi.Text363" :
            ServiceReady ? "Ui.ServiceSetupUi.Text212" :
            serviceCleaning ? "Ui.ServiceSetupUi.Text325" :
            serviceStatusUnavailable ? "Ui.ServiceSetupUi.Text292" :
            serviceSnapshot switch
            {
                null => "Host.CheckingSystemService",
                { PendingDelete: true } => "Ui.ServiceSetupUi.Text403",
                { Installed: false } => "Ui.ServiceSetupUi.Text326",
                { State: 4 } => "Ui.ServiceSetupUi.Text365",
                { State: 1 } => "Ui.ServiceSetupUi.Text217",
                _ => "Ui.ServiceSetupUi.Text351"
            };
        LocalizedText.Bind(serviceOverview,"Text",overviewKey);
    }
    internal Task EnableSystemService()=>RunServiceSetup(false,true);
    async Task CompleteServiceEnable()
    {
        ServiceRequired=true;LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text366");
        serviceConnectingOperation=ConnectServiceCore(SetupBackend.Connect,true);await serviceConnectingOperation;
        if(ServiceReady){LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceSetupUi.Text420");LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text213");}
        else LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text324");
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
            serviceSnapshot = status;
            serviceStatusUnavailable = false;
            if (status.WorkerDeployment is { } workerDeployment)
            {
                adminDeploymentUnavailable = workerDeployment != AdminWorkerDeployment.Verified;
                if (state.Text == Strings.Get("Ui.HostWindow.Text408")) detail.Text = ListeningDetail;
            }
            LocalizedText.Bind(serviceInstallation,"Text",()=>!status.Installed?Strings.Get("Ui.ServiceSetupUi.Text421"):status.PendingDelete?Strings.Get("Ui.ServiceSetupUi.Text320"):Strings.Format("Ui.ServiceSetupUi.Text314",(status.State==4?Strings.Get("Ui.ServiceSetupUi.Text471"):status.State==1?Strings.Get("Ui.ServiceSetupUi.Text197"):Strings.Get("Ui.ServiceSetupUi.Text382")),(status.Startup==2?Strings.Get("Ui.ServiceSetupUi.Text231"):Strings.Get("Ui.ServiceSetupUi.Text234"))));
            serviceInstallation.Text += status.WorkerDeployment switch
            {
                AdminWorkerDeployment.Verified => Strings.Get("Ui.ServiceSetupUi.Text014"),
                AdminWorkerDeployment.AccessDenied => Strings.Get("Ui.ServiceSetupUi.Text011"),
                AdminWorkerDeployment.RepairRequired => Strings.Get("Ui.ServiceSetupUi.Text015"),
                _ => ""
            };
        }
        catch(Exception error)
        {
            if (!serviceSetupClosed && generation == serviceSetupGeneration)
            {
                serviceStatusUnavailable = true;
                LocalizedText.Bind(serviceInstallation,"Text",()=>Strings.Get("Ui.ServiceSetupUi.Text297") + error.GetType().Name);
            }
        }
        finally{serviceInspecting=false;if(!serviceSetupClosed){UpdateServiceSetupControls();if(serviceInspectAgain&&!ServiceSetupBusy){serviceInspectAgain=false;await InspectServiceInstallation();}}}
    }
    internal async Task RunServiceSetup(bool uninstall,bool simpleEnable=false)
    {
        if(ServiceSetupBusy||Host.Running||RestartPending||serviceConnecting||serviceCleaning||closing||serviceSetupClosed)return;
        if(simpleEnable&&ServiceReady)return;
        ServiceSetupBusy=true;++serviceSetupGeneration;UpdateServiceControls();
        string step = Strings.Get("Ui.ServiceSetupUi.Text343");
        LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text359");
        try
        {
            if(simpleEnable)
            {
                step = Strings.Get("Ui.ServiceSetupUi.Text467");
                LocalizedText.Bind(serviceSetupNotice,"Text","Host.CheckingSystemService");var current=await SetupBackend.Inspect();
                if(current.PendingDelete){LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text319");return;}
                if(current.Installed&&current.State==4){await CompleteServiceEnable();return;}
                if(current.Installed&&current.State!=1){LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text317");return;}
                LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text348");
            }
            step = Strings.Get("Ui.ServiceSetupUi.Text343");
            bool? automatic=serviceStartup.SelectedIndex==0?null:serviceStartup.SelectedIndex==2;
            // The simple action preserves installed startup policy. Advanced
            // choices are applied only by the explicit repair/update action.
            var plan=await SetupBackend.Prepare(application,uninstall,simpleEnable?null:automatic);
            if(simpleEnable)plan=plan with{OnlyIssues=true};
            string action=uninstall?Strings.Get("Ui.ServiceSetupUi.Text142"):Strings.Format("Ui.ServiceSetupUi.Text172",(automatic is null?Strings.Get("Ui.ServiceSetupUi.Text102"):automatic.Value?Strings.Get("Ui.ServiceSetupUi.Text231"):Strings.Get("Ui.ServiceSetupUi.Text234")));
            bool accepted=simpleEnable||(SetupConfirmation?.Invoke(plan)??MessageBox.Show(this,Strings.Format("Ui.ServiceSetupUi.Text056",action,plan.Owner,plan.Version), Strings.Get("Ui.ServiceSetupUi.Text045"),MessageBoxButton.OKCancel,MessageBoxImage.Warning)==MessageBoxResult.OK);
            if(!accepted){LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text209");return;}
            await DisconnectService();
            step = Strings.Get("Ui.ServiceSetupUi.Text155");
            LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text362");
            int code=await SetupBackend.Run(plan);
            if(simpleEnable&&code==0){await CompleteServiceEnable();return;}
            if(uninstall&&code==0)ServiceRequired=false;
            LocalizedText.Bind(serviceSetupNotice,"Text",()=>code==0?(uninstall?Strings.Get("Ui.ServiceSetupUi.Text140"):Strings.Get("Ui.ServiceSetupUi.Text315")):
                code==2?Strings.Get("Ui.ServiceSetupUi.Text321"):Strings.Format("Ui.ServiceSetupUi.Text323",code));
        }
        catch(Win32Exception error)when(error.NativeErrorCode==1223){LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text207");}
        catch(Exception error)
        {
            LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceSetupUi.Text061",step,error.GetType().Name,error.HResult);
            logs.AppendText($"service_setup_failed step={step} type={error.GetType().Name} hresult=0x{error.HResult:X8}\n");
        }
        finally{ServiceSetupBusy=false;UpdateServiceControls();await InspectServiceInstallation();}
    }
}
