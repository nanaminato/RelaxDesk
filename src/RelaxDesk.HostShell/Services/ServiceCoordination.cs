using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Session;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    CheckBox useService => serviceSettings.UseService;
    Button connectService => serviceSettings.ConnectService;
    TextBlock serviceStatus => serviceSettings.CoordinationStatus;
    SessionAgentClient? serviceAgent;
    CancellationTokenSource? serviceAttempt,serviceWatch;
    Task? serviceConnectingOperation,serviceObserver;
    int serviceGeneration;
    bool serviceConnecting,serviceCleaning;
    internal bool ServiceRequired{get=>useService.IsChecked==true;set=>useService.IsChecked=value;}
    internal bool ServiceReady=>serviceAgent?.Healthy==true&&!serviceConnecting&&!serviceCleaning;
    internal string ServiceStatus=>serviceStatus.Text;
    void ConfigureServiceSettings()
    {
        ConfigureServiceSetup();
        connectService.Click+=async(_,_)=>await ConnectService();
        useService.Checked+=(_,_)=>{if(!ServiceReady)LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceCoordination.Text316");UpdateServiceControls();};
        useService.Unchecked+=async(_,_)=>{if(Host.Running){useService.IsChecked=true;return;}await DisconnectService();LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceCoordination.Text222");UpdateServiceControls();};
    }
    void UpdateServiceControls()
    {
        useService.IsEnabled=!Host.Running&&!serviceConnecting&&!serviceCleaning&&!closing&&!ServiceSetupBusy;
        connectService.IsEnabled=ServiceRequired&&!Host.Running&&!serviceConnecting&&!serviceCleaning&&!ServiceReady&&!closing&&!ServiceSetupBusy;
        bool sharing = Host.Running || RestartPending;
        LocalizedText.Bind(start,"Content",()=>sharing ? Strings.Get("Host.StopSharing") : Strings.Get("Host.StartSharing"));
        start.Style = (Style)Resources[sharing ? "DangerButton" : "PrimaryButton"];
        start.IsEnabled = !closing && !stoppingSharing && (sharing ||
            !savingLogin && !checkingInputDeployment && !serviceConnecting && !serviceCleaning && !ServiceSetupBusy && (!ServiceRequired || ServiceReady));
        saveLogin.IsEnabled=!Host.Running&&!savingLogin&&!RestartPending&&!closing;
        UpdateServiceSetupControls();
        UpdateRecoveryControls();
    }
    internal Task ConnectService(Func<CancellationToken,Task<SessionAgentClient>>? connect=null)
    {
        if(serviceConnectingOperation is {IsCompleted:false})return serviceConnectingOperation;
        return serviceConnectingOperation=ConnectServiceCore(connect??SessionAgentClient.Connect);
    }
    async Task ConnectServiceCore(Func<CancellationToken,Task<SessionAgentClient>> connect,bool setupCompletion=false)
    {
        if(!ServiceRequired||Host.Running||serviceCleaning||ServiceReady||closing||ServiceSetupBusy&&!setupCompletion)return;
        int generation=++serviceGeneration;serviceConnecting=true;serviceAttempt=new();UpdateServiceControls();
        LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceCoordination.Text370");
        SessionAgentClient? candidate=null;
        try
        {
            var original = WindowsSessionAccess.Read();
            original.EnsureAllowed();
            candidate=await connect(serviceAttempt.Token);
            var current = WindowsSessionAccess.Read();
            current.EnsureBrokerLeaseAllowedFrom(original);
            current.EnsureAllowed();
            if(generation!=serviceGeneration||closing||!ServiceRequired)throw new OperationCanceledException();
            serviceAgent=candidate;candidate=null;serviceWatch?.Dispose();serviceWatch=new();serviceObserver=ObserveService(serviceAgent,generation,original,serviceWatch.Token);
            if(serviceAgent?.Healthy==true)LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceCoordination.Text418");
        }
        catch(Exception error)
        {LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceCoordination.Text295");logs.AppendText($"service_connect_failed error_type={error.GetType().Name}\n");}
        finally
        {if(candidate is not null)await candidate.DisposeAsync();serviceAttempt.Dispose();serviceAttempt=null;serviceConnecting=false;UpdateServiceControls();}
    }
    async Task ObserveService(SessionAgentClient agent,int generation,WindowsSessionSnapshot original,CancellationToken ct)
    {
        try
        {
            while(true)
            {
                ct.ThrowIfCancellationRequested();
                if(agent.Completion.IsCompleted){await agent.Completion;throw new IOException("Agent lease completed");}
                WindowsSessionAccess.Read().EnsureBrokerLeaseAllowedFrom(original);
                await Task.Delay(250,ct);
            }
        }
        catch(Exception error)
        {
            if(ct.IsCancellationRequested||generation!=serviceGeneration||!ReferenceEquals(serviceAgent,agent))return;
            serviceCleaning=true;serviceAgent=null;UpdateServiceControls();
            LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceCoordination.Text417");
            string reason = error is WindowsSessionUnavailableException desktop ? " reason=" + desktop.Reason : "";
            logs.AppendText($"service_lease_ended error_type={error.GetType().Name}{reason}\n");
            try{await StopSharing();await agent.DisposeAsync();}
            finally
            {serviceCleaning=false;LocalizedText.Bind(serviceStatus,"Text","Ui.ServiceCoordination.Text422");if(!ServiceSetupBusy)LocalizedText.Bind(serviceSetupNotice,"Text","Ui.ServiceCoordination.Text380");UpdateServiceControls();}
        }
    }
    internal async Task DisconnectService()
    {
        ++serviceGeneration;serviceAttempt?.Cancel();serviceWatch?.Cancel();
        if(serviceConnectingOperation is {} connecting)try{await connecting;}catch(Exception){}
        if(serviceObserver is {} observing)try{await observing;}catch(Exception){}
        var agent=serviceAgent;serviceAgent=null;if(agent is not null)await agent.DisposeAsync();
        serviceWatch?.Dispose();serviceWatch=null;serviceObserver=null;UpdateServiceControls();
    }
}
