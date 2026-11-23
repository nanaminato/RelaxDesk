using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow
{
    readonly CheckBox restartWorker=new(){Content="Host 异常退出后重启等待连接（最多三次，仍需重新授权）"};
    CancellationTokenSource? restartWait;
    WorkerRestartBudget? restartBudget;
    string restartBackend="dxgi";
    HostEvent? deferredExit;
    internal bool RestartEnabled{get=>restartWorker.IsChecked==true;set=>restartWorker.IsChecked=value;}
    internal bool RestartPending=>restartWait is not null;
    void AddRecoverySettings(Panel settings)
    {
        settings.Children.Add(new Expander{Header="异常恢复（默认关闭）",Content=restartWorker});
        restartWorker.Unchecked+=(_,_)=>CancelRecovery();
    }
    void CancelRecovery()
    {
        restartBudget?.Cancel();restartBudget=null;restartWait?.Cancel();
    }
    void UpdateRecoveryControls()
    {
        if(!RestartPending)return;
        ip.IsEnabled=port.IsEnabled=start.IsEnabled=useService.IsEnabled=connectService.IsEnabled=false;
        SetFileConfigurationEnabled(false);SetDisplayConfigurationEnabled(false);stop.IsEnabled=!closing;
    }
    async void RecoverWorker(HostEvent change)
    {
        if(change.Kind!="ended"||closing||restartBudget is null)return;
        if(RestartPending){deferredExit=change;return;}
        bool allowed=!ServiceRequired||ServiceReady;
        try{WindowsSessionAccess.EnsureAllowed();}catch{allowed=false;}
        var delay=restartBudget.Next(RestartEnabled,change.ExitCode,allowed);
        if(delay is null)return;
        using var wait=new CancellationTokenSource();restartWait=wait;UpdateRecoveryControls();
        state.Text="等待重启 Host";detail.Text=$"{delay.Value.TotalSeconds} 秒后重新等待连接；旧授权不会恢复。";
        try
        {
            await Task.Delay(delay.Value,wait.Token);
            WindowsSessionAccess.EnsureAllowed();wait.Token.ThrowIfCancellationRequested();
            if(closing||!RestartEnabled||Host.Running||(ServiceRequired&&!ServiceReady))return;
            await StartSharing(backend:restartBackend,recovering:true);
        }
        catch(OperationCanceledException){}
        catch(Exception e){detail.Text="自动恢复已停止："+e.Message;restartBudget?.Cancel();}
        finally
        {
            restartWait=null;
            if(!Host.Running){ip.IsEnabled=port.IsEnabled=true;SetFileConfigurationEnabled(true);SetDisplayConfigurationEnabled(true);stop.IsEnabled=false;if(wait.IsCancellationRequested){state.Text="已停止共享";detail.Text="已取消自动恢复。";}}
            UpdateServiceControls();
            var ended=deferredExit;deferredExit=null;if(ended is not null)RecoverWorker(ended);
        }
    }
}
