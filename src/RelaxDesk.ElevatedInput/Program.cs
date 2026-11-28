using System.Security;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;

bool serviceInput = args is ["--service-input", ..];
if (serviceInput) args = args[1..];
string? fingerprint = args is ["--local-consent", var device, ..] ? device : null;
if (fingerprint is not null && !LocalConnectionConsent.ValidFingerprint(fingerprint)) return 2;
var request = AdminInputLaunchRequest.Parse(fingerprint is null ? args : args[2..]);
if (request is null) return 2;

try { if (serviceInput) WindowsInputPrivilege.EnsureSystemInputProcess(); else WindowsInputPrivilege.EnsureElevatedProcess(); }
catch (IOException) { return 4; }

try
{
    string installed = ServiceDeploymentTrust.ResolveElevatedInput(request.Owner);
    if (!string.Equals(Environment.ProcessPath, installed, StringComparison.OrdinalIgnoreCase))
        return 3;
    using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var pipe = await LocalAgentPipe.ConnectAdmin(request.Grant, startup.Token);
    LocalAgentPipe.AuthenticateAdminCoordinator(pipe, request.CoordinatorPid, request.Owner);
    if (fingerprint is not null && !LocalConnectionConsent.Confirm(fingerprint, pipe.SafePipeHandle))
    {
        using var rejected = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Wire.Write(pipe, new("admin-input-consent-denied", Origin: request.Grant.ToString("N")), rejected.Token);
        return 5;
    }
    // Readiness comes from the worker after it checks the administrator token
    // and the interactive desktop.
    if (serviceInput)
    {
        using var desktop = await LocalAgentPipe.ConnectDesktop(request.Grant, startup.Token);
        LocalAgentPipe.AuthenticateAdminCoordinator(desktop, request.CoordinatorPid, request.Owner);
        await ServiceDesktopWorker.Run(pipe, desktop, request.Grant, () => new WindowsDisplaySelection(request.Display), CancellationToken.None);
    }
    else await ElevatedInputWorker.Run(pipe, request.Grant, () => new WindowsDisplaySelection(request.Display), CancellationToken.None);
    return 0;
}
catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or
    System.ComponentModel.Win32Exception or ArgumentException or InvalidDataException or OperationCanceledException)
{
    return 3;
}
