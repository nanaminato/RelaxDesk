using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    CancellationTokenSource? restartWait;
    WorkerRestartBudget? restartBudget;
    WindowsSessionSnapshot? restartOrigin;
    string restartBackend="dxgi";
    HostEvent? deferredExit;
    internal Func<WindowsSessionSnapshot> ReadRecoveryDesktop { get; set; } = WindowsSessionAccess.Read;
    internal bool RestartPending=>restartWait is not null;
    void CancelRecovery()
    {
        restartBudget?.Cancel();restartBudget=null;restartOrigin=null;restartWait?.Cancel();
    }
    void UpdateRecoveryControls()
    {
        if(!RestartPending)return;
        ip.IsEnabled=port.IsEnabled=useService.IsEnabled=connectService.IsEnabled=false;
        SetDisplayConfigurationEnabled(false);
    }
    async void RecoverWorker(HostEvent change)
    {
        if(change.Kind!="ended"||closing||hostTokenDenied||restartBudget is null||restartOrigin is not {} original)return;
        if(RestartPending){deferredExit=change;return;}
        bool allowed=!ServiceSetupBusy&&(!ServiceRequired||ServiceReady);
        try{ReadRecoveryDesktop().EnsureBrokerLeaseAllowedFrom(original);}catch{allowed=false;}
        var delay=restartBudget.Next(change.ExitCode,allowed);
        if(delay is null)return;
        using var wait=new CancellationTokenSource();restartWait=wait;UpdateServiceControls();
        LocalizedText.Bind(state,"Text","Ui.WorkerRecovery.Text357");LocalizedText.Bind(detail,"Text","Ui.WorkerRecovery.Text123");
        try
        {
            await Task.Delay(delay.Value,wait.Token);
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                if(closing||Host.Running||ServiceSetupBusy||(ServiceRequired&&!ServiceReady))return;
                var current = ReadRecoveryDesktop();
                current.EnsureBrokerLeaseAllowedFrom(original);
                if (!current.TemporaryInputDesktopUnavailable) break;
                LocalizedText.Bind(detail,"Text","Ui.WorkerRecovery.Text472");
                await Task.Delay(250,wait.Token);
            }
            await StartSharing(backend:restartBackend,recovering:true);
        }
        catch(OperationCanceledException){}
        catch(Exception e){LocalizedText.Bind(detail,"Text",()=>Strings.Get("Ui.WorkerRecovery.Text432")+e.Message);restartBudget?.Cancel();}
        finally
        {
            restartWait=null;
            if(!Host.Running){ip.IsEnabled=port.IsEnabled=true;SetDisplayConfigurationEnabled(true);if(wait.IsCancellationRequested){LocalizedText.Bind(state,"Text","Ui.HostWindow.Text198");LocalizedText.Bind(detail,"Text","Ui.WorkerRecovery.Text208");}}
            UpdateServiceControls();
            var ended=deferredExit;deferredExit=null;if(ended is not null)RecoverWorker(ended);
        }
    }
}
