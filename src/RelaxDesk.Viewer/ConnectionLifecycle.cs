using System.Diagnostics;
using System.IO;
using System.Net;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    CancellationTokenSource? connectionRun;
    ConnectionPhase connectionPhase;
    int connectionGeneration;
    sealed record ConnectionTarget(string Host, int Port, bool Strict, string Pin, string Token, string[] Features, bool FullScreen = false,int DisplayVersion=DisplayProtocol.TopologyVersion,SessionMode? Mode=null,NetworkMode Network=NetworkMode.Auto,bool IdentifyDevice=false)
    { public override string ToString() => "ConnectionTarget [credentials redacted]"; }
    sealed record AttemptResult(Exception? Error, bool Established, string Pin, string[] Features, bool FullScreen, bool Stable);
    void StopConnection() { connectionRun?.Cancel(); session?.Cancel(); }
    void SetConnectionPhase(ConnectionPhase phase)
    {
        connectionPhase = phase;
        if (phase == ConnectionPhase.Connected) ClearConnectionError();
        status.Text = phase switch
        {
            ConnectionPhase.Connecting => "正在连接远程电脑…",
            ConnectionPhase.Authenticating => "正在验证加密连接…",
            ConnectionPhase.AwaitingApproval => "等待 Host 允许本次连接…",
            ConnectionPhase.Negotiating => "正在建立已授权的桌面通道…",
            ConnectionPhase.Connected => "已获授权，正在接收桌面…",
            ConnectionPhase.Disconnected => "已断开连接",
            _ => status.Text
        };
    }
    async Task ConnectVideo()
    {
        if (connectionRun is not null) return;
        ClearConnectionError();
        if (!IPAddress.TryParse(host.Text.Trim(), out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || !int.TryParse(port.Text, out int number) || number is < 1 or > 65535)
        { status.Text = "请输入有效的远程 IP 和端口。"; ShowConnectionError(new FormatException("Invalid endpoint"), "configuration"); return; }
        var target = new ConnectionTarget(address.ToString(), number, pinnedConnection.IsChecked == true, fingerprint.Text, token.Password, RequestedFeatures(), fullScreen,requestTopology.IsChecked==true?DisplayProtocol.TopologyVersion:DisplayProtocol.Version,requestedMode.SelectedIndex switch { 1 => SessionMode.Guest, 2 => SessionMode.Admin, _ => SessionMode.Work }, networkMode.SelectedIndex switch { 1 => NetworkMode.Tcp, 2 => NetworkMode.Udp, _ => NetworkMode.Auto }, requestedMode.SelectedIndex == 2 || requestedMode.SelectedIndex == 0 && identifyWorkDevice.IsChecked == true);
        if (smoke?.RequireUdp == true) target = target with { Network = NetworkMode.Udp };
        using var lifetime = new CancellationTokenSource(); connectionRun = lifetime;
        connectionEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connect.IsEnabled = connectionCard.IsEnabled = false; disconnect.IsEnabled = true;
        string? approvedPin = null; int retry = 0;
        Stopwatch? outage = null;
        using var recoveryBudget = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            while (true)
            {
                var result = await ConnectVideoAttempt(target, approvedPin, retry > 0 ? recoveryBudget.Token : lifetime.Token, () => {recoveryBudget.CancelAfter(Timeout.InfiniteTimeSpan);QueueConnectionHistory(target.Host,target.Port);});
                if (result.Established)
                {
                    approvedPin = result.Pin;
                    target = target with { Token = "", Pin = approvedPin, Features = result.Features, FullScreen = result.FullScreen };
                    // Briefly successful handshakes must not permit endless reconnect loops.
                    if (result.Stable) { retry = 0; outage = null; }
                }
                if (lifetime.IsCancellationRequested) { SetConnectionPhase(ConnectionPhase.Disconnected); break; }
                if (recoveryBudget.IsCancellationRequested)
                { SetConnectionPhase(ConnectionPhase.Failed); status.Text = "自动重连已超时，请检查网络后手动连接。"; break; }
                if (result.Error is null) { SetConnectionPhase(ConnectionPhase.Disconnected); break; }
                if (approvedPin is null || !PipelineAllowsReconnect || !ConnectionRecovery.IsTransient(result.Error) || (smoke is not null && !smoke.ReconnectTest))
                {
                    SetConnectionPhase(result.Error is RemoteSessionEndedException ? ConnectionPhase.Disconnected : ConnectionPhase.Failed);
                    status.Text = result.Error switch
                    {
                        HostConsentRejectedException => "Host 已拒绝本次连接，已停止自动重连。",
                        UdpMediaUnavailableException { Reason: UdpMediaFailure.LocalUnavailable } => "本机无法使用 UDP 媒体。请选择 Auto 或 TCP 后重试。",
                        UdpMediaUnavailableException { Reason: UdpMediaFailure.HostUnavailable } => "Host 未提供 UDP 媒体。请选择 Auto 或 TCP 后重试。",
                        UdpMediaUnavailableException => "无法建立 UDP 媒体连接。请检查网络，或选择 Auto 后重试。",
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsLocked } => "Host 已锁屏，本次授权已结束。请在 Host 解锁后重新连接。",
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsSessionChanged } => "Host 已切换 Windows 会话。重新连接需要本机再次授权。",
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsDesktopUnavailable } => "Host 的输入桌面已不可用。请回到原用户的普通桌面后重新连接。",
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsSessionUnavailable } => "Host 的 Windows 会话已不可用，本次授权已结束。",
                        RemoteSessionEndedException => "Host 已结束本次连接。",
                        System.Security.Authentication.AuthenticationException => "无法验证 Host 身份，已停止连接；请重新核对指纹。",
                        InvalidDataException => "会话协议数据无效，已停止自动重连。",
                        _ => "连接已结束：" + result.Error.Message
                    };
                    if (result.Error is not RemoteSessionEndedException && PipelineFailureText is { } pipelineText) status.Text = pipelineText;
                    break;
                }
                if (outage is null) outage = Stopwatch.StartNew();
                var delay = ConnectionRecovery.Delay(++retry, outage.Elapsed, Random.Shared.NextDouble());
                if (delay is null || recoveryBudget.IsCancellationRequested)
                { SetConnectionPhase(ConnectionPhase.Failed); status.Text = "自动重连已达到限制，请检查网络后手动连接。"; break; }
                var remaining = ConnectionRecovery.RecoveryWindow - outage.Elapsed;
                if (remaining <= TimeSpan.Zero)
                { SetConnectionPhase(ConnectionPhase.Failed); status.Text = "自动重连已超时，请检查网络后手动连接。"; break; }
                recoveryBudget.CancelAfter(remaining);
                SetConnectionPhase(ConnectionPhase.Reconnecting);
                status.Text = $"连接意外中断，{delay.Value.TotalSeconds:F1} 秒后重试（{retry}/{ConnectionRecovery.MaximumRetries}）；仍需 Host 重新允许。";
                ViewerSessionLog.Record("reconnect-wait");
                await Task.Delay(delay.Value, recoveryBudget.Token);
            }
        }
        catch (OperationCanceledException)
        { SetConnectionPhase(lifetime.IsCancellationRequested ? ConnectionPhase.Disconnected : ConnectionPhase.Failed); if (!lifetime.IsCancellationRequested) status.Text = "自动重连已超时，请检查网络后手动连接。"; }
        finally
        {
            lifetime.Cancel();await DiscardFileRecovery(); connectionRun = null;
            connect.IsEnabled = connectionCard.IsEnabled = true; disconnect.IsEnabled = false;
            connectionEnded.TrySetResult();
        }
    }
}
