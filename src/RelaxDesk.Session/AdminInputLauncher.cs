using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace RelaxDesk.Session;

public sealed class AdminInputDeploymentException(Exception cause) : IOException("Administrator input deployment unavailable", cause);

public static class AdminInputLauncher
{
    // The caller must have obtained explicit local approval for this grant.
    public static Task<AdminInputConnection> StartAfterLocalApproval(Guid grant, string display,
        CancellationToken cancellation)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string owner = identity.User?.Value ?? throw new IOException("Interactive owner SID unavailable");
        return Start(grant, display, owner, ServiceDeploymentTrust.ResolveElevatedInput, Process.Start, cancellation);
    }

    internal static async Task<AdminInputConnection> Start(Guid grant, string display, string owner,
        Func<string, string> resolve, Func<ProcessStartInfo, Process?> launch, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        owner = LocalAgentPipe.CanonicalUser(owner);
        string executable;
        try { executable = resolve(owner); }
        catch (Exception error) when (error is IOException or System.Security.SecurityException or UnauthorizedAccessException or InvalidDataException)
        { throw new AdminInputDeploymentException(error); }
        var start = CreateStartInfo(executable, grant, owner, display);
        var server = LocalAgentPipe.CreateAdminServer(grant, owner);
        try
        {
            // Close the rendezvous immediately if the approval is revoked while
            // Windows is still displaying its local consent prompt.
            using var revoked = cancellation.Register(server.Dispose);
            using var process = await WaitForLaunch(Task.Run(() => launch(start), cancellation), cancellation)
                ?? throw new IOException("Administrator input worker did not launch");
            cancellation.ThrowIfCancellationRequested();
            return await AdminInputConnection.Accept(server, grant, process.Id, cancellation);
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

    static ProcessStartInfo CreateStartInfo(string executable, Guid grant, string owner, string display)
    {
        if (grant == Guid.Empty || display.Length is < 1 or > 256 || display.Any(char.IsControl))
            throw new ArgumentException("Invalid administrator input grant or display");

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(grant.ToString("N"));
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add(owner);
        start.ArgumentList.Add(display);
        return start;
    }
}
