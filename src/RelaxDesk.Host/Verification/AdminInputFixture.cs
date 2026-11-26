using System.Security.Principal;
using RelaxDesk.Core;
using RelaxDesk.Session;

namespace RelaxDesk.Host;

internal static class AdminInputFixture
{
    public static async Task<AdminInputConnection> Start(bool disconnectOnInput, CancellationToken cancellation)
    {
        using var identity = WindowsIdentity.GetCurrent();
        string owner = identity.User?.Value ?? throw new IOException("Fixture owner SID unavailable");
        Guid grant = Guid.NewGuid();
        var server = LocalAgentPipe.CreateAdminServer(grant, owner);
        _ = RunPeer();
        return await AdminInputConnection.Accept(server, grant, Environment.ProcessId, cancellation);

        async Task RunPeer()
        {
            try
            {
                using var pipe = await LocalAgentPipe.ConnectAdmin(grant, cancellation);
                await Wire.Write(pipe, new(AdminInputChannel.Ready, Origin: grant.ToString("N")), cancellation);
                await AdminInputChannel.Run(pipe, grant, _ =>
                {
                    HostSessionLog.Write("test_admin_input_routed");
                    if (disconnectOnInput) throw new IOException("Injected input worker loss");
                }, () => HostSessionLog.Write("test_admin_input_released"), cancellation);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
            {
                HostSessionLog.Write($"test_admin_input_peer_ended type={error.GetType().Name}");
            }
        }
    }
}
