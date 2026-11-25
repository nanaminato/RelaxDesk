using System.Security;
using RelaxDesk.Core;
using RelaxDesk.Session;
using RelaxDesk.Windows;

var request = AdminInputLaunchRequest.Parse(args);
if (request is null) return 2;

try { WindowsInputPrivilege.EnsureElevatedProcess(); }
catch (IOException) { return 4; }

try
{
    string installed = ServiceDeploymentTrust.ResolveElevatedInput(request.Owner);
    if (!string.Equals(Environment.ProcessPath, installed, StringComparison.OrdinalIgnoreCase))
        return 3;
    using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var pipe = await LocalAgentPipe.ConnectAdmin(request.Grant, startup.Token);
    LocalAgentPipe.AuthenticateAdminCoordinator(pipe, request.CoordinatorPid, request.Owner);
    // Readiness comes from the worker after it checks the administrator token
    // and the interactive desktop.
    await ElevatedInputWorker.Run(pipe, request.Grant, () => new WindowsDisplaySelection(request.Display), CancellationToken.None);
    return 0;
}
catch (Exception error) when (error is IOException or SecurityException or UnauthorizedAccessException or
    System.ComponentModel.Win32Exception or ArgumentException or InvalidDataException or OperationCanceledException)
{
    return 3;
}
