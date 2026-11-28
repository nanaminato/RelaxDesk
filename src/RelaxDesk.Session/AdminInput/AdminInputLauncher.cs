using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace RelaxDesk.Session;

public sealed class AdminInputDeploymentException(Exception cause) : IOException("Administrator input deployment unavailable", cause);

public static class AdminInputLauncher
{
    public static async Task<AdminInputConnection> StartService(Guid grant, string display, CancellationToken cancellation)
    {
        using var ownerIdentity = WindowsIdentity.GetCurrent();
        string owner = ownerIdentity.User?.Value ?? throw new IOException("Interactive owner SID unavailable");
        var request = new AdminInputLaunchRequest(grant, Environment.ProcessId, owner, display);
        string executable = ServiceDeploymentTrust.ResolveElevatedInput(owner);
        var server = LocalAgentPipe.CreateAdminServer(grant, owner);
        SessionAgentClient? lease = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            lease = await SessionAgentClient.ConnectInput(request, deadline.Token);
            var input = await AdminInputConnection.Accept(server, grant, lease.InputWorkerPid, deadline.Token,
                executable, systemWorker: true);
            input.BindServiceLease(lease);
            lease = null;
            return input;
        }
        catch
        {
            server.Dispose();
            if (lease is not null) await lease.DisposeAsync();
            throw;
        }
    }

    public static void CheckScheduledDeployment(string owner)
    {
        string executable = ServiceDeploymentTrust.ResolveElevatedInput(owner);
        new AdminInputTask().CheckReady(executable, owner);
    }

    public static async Task<AdminInputConnection> StartScheduled(Guid grant, string display, string fingerprint,
        CancellationToken cancellation)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string owner = identity.User?.Value ?? throw new IOException("Interactive owner SID unavailable");
        using var coordinator = Process.GetCurrentProcess();
        int sessionId = coordinator.SessionId;
        var request = new AdminInputLaunchRequest(grant, coordinator.Id, owner, display);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var startupToken = startup.Token;
        var server = LocalAgentPipe.CreateAdminServer(grant, owner);
        try
        {
            using var revoked = startupToken.Register(server.Dispose);
            string executable;
            try
            {
                executable = ServiceDeploymentTrust.ResolveElevatedInput(owner);
                var task = Task.Run(() =>
                {
                    startupToken.ThrowIfCancellationRequested();
                    new AdminInputTask().Run(executable, request, fingerprint, sessionId);
                }, startupToken);
                try { await task.WaitAsync(startupToken); }
                catch (OperationCanceledException)
                {
                    _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    throw;
                }
            }
            catch (Exception error) when (error is IOException or System.Security.SecurityException or UnauthorizedAccessException or InvalidDataException or COMException)
            {
                throw new AdminInputDeploymentException(error);
            }
            return await AdminInputConnection.Accept(server, grant, null, startupToken, executable, owner);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    internal static async Task<AdminInputConnection> Start(Guid grant, string display, string owner,
        Func<string, string> resolve, Func<ProcessStartInfo, Process?> launch, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var request = new AdminInputLaunchRequest(grant, Environment.ProcessId, owner, display);
        owner = request.Owner;
        string executable;
        try { executable = resolve(owner); }
        catch (Exception error) when (error is IOException or System.Security.SecurityException or UnauthorizedAccessException or InvalidDataException)
        { throw new AdminInputDeploymentException(error); }
        cancellation.ThrowIfCancellationRequested();
        var start = CreateStartInfo(executable, request);
        var server = LocalAgentPipe.CreateAdminServer(grant, owner);
        try
        {
            // Close the rendezvous immediately if the approval is revoked while
            // Windows is still displaying its local consent prompt.
            using var revoked = cancellation.Register(server.Dispose);
            using var process = await WaitForLaunch(Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                return launch(start);
            }, cancellation), cancellation)
                ?? throw new IOException("Administrator input worker did not launch");
            cancellation.ThrowIfCancellationRequested();
            return await AdminInputConnection.Accept(server, grant, process.Id, cancellation, executable);
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        {
            server.Dispose();
            throw new OperationCanceledException("Local administrator elevation was cancelled", error);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    static async Task<Process?> WaitForLaunch(Task<Process?> launch, CancellationToken cancellation)
    {
        try { return await launch.WaitAsync(cancellation); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // ShellExecute may stay in Windows consent after the session ends.
            // A late worker cannot attach once the caller closes its pipe.
            _ = launch.ContinueWith(static completed =>
            {
                if (completed.IsCompletedSuccessfully) completed.Result?.Dispose();
                else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }

    static ProcessStartInfo CreateStartInfo(string executable, AdminInputLaunchRequest request)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (string argument in request.Arguments()) start.ArgumentList.Add(argument);
        return start;
    }
}
