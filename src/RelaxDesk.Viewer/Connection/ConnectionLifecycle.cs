using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
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
    sealed record ConnectionTarget(string Host, int Port, bool Strict, string Pin, string Token, string[] Features, bool FullScreen = false,int DisplayVersion=DisplayProtocol.TopologyVersion,NetworkMode Network=NetworkMode.Auto,bool FullControl=false,string? UserName=null)
    { public override string ToString() => "ConnectionTarget [credentials redacted]"; }
    sealed record AttemptResult(Exception? Error, bool Established, string Pin, string[] Features, bool FullScreen, bool Stable);
    void StopConnection() { connectionRun?.Cancel(); session?.Cancel(); }
    void SetConnectionPhase(ConnectionPhase phase)
    {
        connectionPhase = phase;
        if (phase == ConnectionPhase.Connected) ClearConnectionError();
        LocalizedText.Bind(status,"Text",()=>phase switch
        {
            ConnectionPhase.Connecting => Strings.Get("Ui.ConnectionLifecycle.Text367"),
            ConnectionPhase.Authenticating => Strings.Get("Ui.ConnectionLifecycle.Text369"),
            ConnectionPhase.AwaitingApproval => Strings.Get("Ui.ConnectionLifecycle.Text508"),
            ConnectionPhase.Negotiating => Strings.Get("Ui.ConnectionLifecycle.Text356"),
            ConnectionPhase.Connected => Strings.Get("Viewer.StatusAuthorized"),
            ConnectionPhase.Disconnected => Strings.Get("Ui.App.Text223"),
            _ => status.Text
        });
    }
    async Task ConnectVideo(bool fullControl = false)
    {
        if (connectionRun is not null) return;
        ClearConnectionError();
        if (!IPAddress.TryParse(host.Text.Trim(), out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || !int.TryParse(port.Text, out int number) || number is < 1 or > 65535)
        { LocalizedText.Bind(status,"Text","Ui.ConnectionLifecycle.Text461"); ShowConnectionError(new FormatException("Invalid endpoint"), "configuration"); return; }
        var target = new ConnectionTarget(address.ToString(), number, pinnedConnection.IsChecked == true, fingerprint.Text, token.Password, RequestedFeatures(), fullScreen || startFullScreen.IsChecked == true,DisplayProtocol.TopologyVersion,networkMode.SelectedIndex switch { 1 => NetworkMode.Tcp, 2 => NetworkMode.Udp, _ => NetworkMode.Auto }, fullControl);
        if (!App.VerificationMode || !IPAddress.IsLoopback(address) || loginName.Text.Length != 0)
        {
            string name = loginName.Text.Trim();
            if (!PasswordVerifier.ValidUserName(name) || !PasswordVerifier.ValidPasswordLength(target.Token.Length))
            {
                LocalizedText.Bind(status,"Text","Ui.ConnectionLifecycle.Text462",PasswordVerifier.MinimumPasswordLength,PasswordVerifier.MaximumPasswordLength);
                return;
            }
            target = target with { UserName = name, Pin = RelaxDesk.Windows.PasswordLoginSettings.ReadServerPin(new IPEndPoint(address, number).ToString()) ?? "", FullControl = true };
            token.Clear();
        }
        if (smoke?.RequireUdp == true) target = target with { Network = NetworkMode.Udp };
        bool remember = rememberCredentials.IsChecked == true, credentialSaved = false;
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
                var result = await ConnectVideoAttempt(target, approvedPin,
                    retry > 0 ? recoveryBudget.Token : lifetime.Token, () =>
                    {
                        recoveryBudget.CancelAfter(Timeout.InfiniteTimeSpan);
                        QueueConnectionHistory(target.Host, target.Port);
                        if (!credentialSaved)
                        {
                            SaveConnectedCredential(target, remember);
                            credentialSaved = true;
                        }
                    });
                if (result.Established)
                {
                    approvedPin = result.Pin;
                    target = target with { Token = target.UserName is null ? "" : target.Token, Pin = approvedPin, Features = result.Features, FullScreen = result.FullScreen };
                    // Briefly successful handshakes must not permit endless reconnect loops.
                    if (result.Stable) { retry = 0; outage = null; }
                }
                if (lifetime.IsCancellationRequested) { SetConnectionPhase(ConnectionPhase.Disconnected); break; }
                if (recoveryBudget.IsCancellationRequested)
                { SetConnectionPhase(ConnectionPhase.Failed); LocalizedText.Bind(status,"Text","Ui.ConnectionLifecycle.Text433"); break; }
                if (result.Error is null) { SetConnectionPhase(ConnectionPhase.Disconnected); break; }
                if (approvedPin is null || !PipelineAllowsReconnect || !ConnectionRecovery.IsTransient(result.Error) || (smoke is not null && !smoke.ReconnectTest))
                {
                    SetConnectionPhase(result.Error is RemoteSessionEndedException ? ConnectionPhase.Disconnected : ConnectionPhase.Failed);
                    LocalizedText.Bind(status,"Text",()=>result.Error switch
                    {
                        HostAdmissionException { Reason: "admission-busy-v5" } => Strings.Get("Admission.Busy"),
                        HostAdmissionException { Reason: "admission-pending-v5" } => Strings.Get("Admission.Pending"),
                        HostAdmissionException { Reason: "admission-rate-limited-v5" } => Strings.Get("Admission.RateLimited"),
                        HostAdmissionException { Reason: "admission-stale-v5" } => Strings.Get("Admission.Stale"),
                        HostAdmissionException { Reason: "takeover-cancel-v5" } => Strings.Get("Admission.Cancelled"),
                        RemoteSessionEndedException { Reason: SessionEndReason.ReplacedByViewer } => Strings.Get("Admission.Replaced"),
                        HostConsentRejectedException => Strings.Get("Ui.ConnectionLifecycle.Text028"),
                        PasswordLoginRejectedException => Strings.Get("Ui.ConnectionLifecycle.Text150"),
                        FullControlInputUnavailableException => Strings.Get("Ui.ConnectionLifecycle.Text036"),
                        UdpMediaUnavailableException { Reason: UdpMediaFailure.LocalUnavailable } => Strings.Get("Ui.ConnectionLifecycle.Text333"),
                        UdpMediaUnavailableException { Reason: UdpMediaFailure.HostUnavailable } => Strings.Get("Ui.ConnectionLifecycle.Text034"),
                        UdpMediaUnavailableException => Strings.Get("Ui.ConnectionLifecycle.Text286"),
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsLocked } => Strings.Get("Ui.ConnectionLifecycle.Text031"),
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsSessionChanged } => Strings.Get("Ui.ConnectionLifecycle.Text027"),
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsDesktopUnavailable } => Strings.Get("Ui.ConnectionLifecycle.Text038"),
                        RemoteSessionEndedException { Reason: SessionEndReason.InputWorkerUnavailable } => Strings.Get("Ui.ConnectionLifecycle.Text037"),
                        RemoteSessionEndedException { Reason: SessionEndReason.WindowsSessionUnavailable } => Strings.Get("Ui.ConnectionLifecycle.Text035"),
                        RemoteSessionEndedException => Strings.Get("Ui.ConnectionLifecycle.Text029"),
                        System.Security.Authentication.AuthenticationException => Strings.Get("Ui.ConnectionLifecycle.Text296"),
                        InvalidDataException => Strings.Get("Ui.ConnectionLifecycle.Text088"),
                        _ => Strings.Get("Ui.ConnectionLifecycle.Text485") + result.Error.Message
                    });
                    if (result.Error is not RemoteSessionEndedException && PipelineFailureText is { } pipelineText) status.Text = pipelineText;
                    break;
                }
                if (outage is null) outage = Stopwatch.StartNew();
                var delay = ConnectionRecovery.Delay(++retry, outage.Elapsed, Random.Shared.NextDouble());
                if (delay is null || recoveryBudget.IsCancellationRequested)
                { SetConnectionPhase(ConnectionPhase.Failed); LocalizedText.Bind(status,"Text","Ui.ConnectionLifecycle.Text434"); break; }
                var remaining = ConnectionRecovery.RecoveryWindow - outage.Elapsed;
                if (remaining <= TimeSpan.Zero)
                { SetConnectionPhase(ConnectionPhase.Failed); LocalizedText.Bind(status,"Text","Ui.ConnectionLifecycle.Text433"); break; }
                recoveryBudget.CancelAfter(remaining);
                SetConnectionPhase(ConnectionPhase.Reconnecting);
                LocalizedText.Bind(status,"Text","Ui.ConnectionLifecycle.Text486",delay.Value.TotalSeconds,retry,ConnectionRecovery.MaximumRetries);
                ViewerSessionLog.Record("reconnect-wait");
                await Task.Delay(delay.Value, recoveryBudget.Token);
            }
        }
        catch (OperationCanceledException)
        { SetConnectionPhase(lifetime.IsCancellationRequested ? ConnectionPhase.Disconnected : ConnectionPhase.Failed); if (!lifetime.IsCancellationRequested) LocalizedText.Bind(status,"Text","Ui.ConnectionLifecycle.Text433"); }
        finally
        {
            lifetime.Cancel(); connectionRun = null;
            connect.IsEnabled = connectionCard.IsEnabled = true; disconnect.IsEnabled = false;
            LoadSavedCredential(endpointChanged: false);
            connectionEnded.TrySetResult();
        }
    }
}
