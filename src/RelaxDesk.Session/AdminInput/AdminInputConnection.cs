using System.IO.Pipes;
using System.Security;
using RelaxDesk.Core;

namespace RelaxDesk.Session;

public sealed class AdminInputConsentDeniedException() : IOException("Local connection consent was declined");

// The caller creates the single-instance server before launching the installed
// worker. It supplies the PID from the launched process, never from the peer.
public sealed class AdminInputConnection : IAsyncDisposable
{
    readonly NamedPipeServerStream pipe;
    readonly string binding;
    readonly CancellationTokenSource closed = new();
    readonly CancellationTokenSource stoppingHeartbeat = new();
    readonly SemaphoreSlim writer = new(1);
    readonly Task heartbeat;
    SessionAgentClient? serviceLease;
    Task? serviceWatch;
    long sequence;
    int closing;

    AdminInputConnection(NamedPipeServerStream pipe, Guid grant)
    {
        this.pipe = pipe;
        binding = grant.ToString("N");
        heartbeat = RunHeartbeat();
    }

    public Task Completion => heartbeat;
    public ServiceDesktopConnection? Desktop { get; internal set; }
    internal void FailDesktop() => Close();

    public static async Task<AdminInputConnection> Accept(NamedPipeServerStream server, Guid grant,
        int? workerPid, CancellationToken cancellation, string? workerImage = null, string? elevatedOwner = null, bool systemWorker = false)
    {
        try
        {
            if (grant == Guid.Empty) throw new ArgumentException("Administrator grant required", nameof(grant));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(elevatedOwner is null ? 15 : 45));
            await server.WaitForConnectionAsync(deadline.Token);
            var ready = await Wire.Read(server, deadline.Token, 2048);
            LocalAgentPipe.AuthenticateAdminWorker(server, workerPid, workerImage, elevatedOwner, systemWorker);
            if (elevatedOwner is not null && ready == new Packet("admin-input-consent-denied", Origin: grant.ToString("N")))
                throw new AdminInputConsentDeniedException();
            if (ready != new Packet(AdminInputChannel.Ready, Origin: grant.ToString("N")))
                throw new SecurityException("Administrator worker readiness rejected");
            deadline.Token.ThrowIfCancellationRequested();
            return new(server, grant);
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    internal void BindServiceLease(SessionAgentClient lease)
    {
        serviceLease = lease;
        serviceWatch = Watch();
        async Task Watch()
        {
            try { await lease.Completion.WaitAsync(closed.Token); }
            catch (OperationCanceledException) when (closed.IsCancellationRequested) { return; }
            catch (Exception) { }
            Close();
        }
    }

    public Task Send(RemoteInput input, CancellationToken cancellation)
    {
        if (!InputProtocol.Valid(input)) throw new InvalidDataException("Invalid administrator input command");
        return Write("admin-input", input, cancellation);
    }

    public Task SelectDisplay(string display, CancellationToken cancellation)
    {
        if (!AdminInputChannel.ValidDisplay(display)) throw new InvalidDataException("Invalid administrator input display");
        return Write("admin-input-display", null, cancellation, display);
    }

    public Task Release(CancellationToken cancellation) => Write("admin-input-release", null, cancellation);

    public async Task End(CancellationToken cancellation)
    {
        stoppingHeartbeat.Cancel();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closed.Token, cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await heartbeat.WaitAsync(deadline.Token);
            await writer.WaitAsync(deadline.Token);
            try
            {
                await Wire.Write(pipe, new("admin-input-end", checked(++sequence), Origin: binding), deadline.Token);
                if (await Wire.Read(pipe, deadline.Token, 2048) != new Packet("admin-input-ended", sequence, Origin: binding))
                    throw new IOException("Administrator worker did not acknowledge input release");
            }
            finally { writer.Release(); }
        }
        catch (OperationCanceledException error) when (!closed.IsCancellationRequested && !cancellation.IsCancellationRequested)
        {
            throw new IOException("Administrator worker release acknowledgement timed out", error);
        }
        finally { Close(); }
    }

    async Task Write(string kind, RemoteInput? input, CancellationToken cancellation, string? text = null)
    {
        stoppingHeartbeat.Token.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(closed.Token, cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        await writer.WaitAsync(deadline.Token);
        try
        {
            stoppingHeartbeat.Token.ThrowIfCancellationRequested();
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                await Wire.Write(pipe, new(kind, checked(++sequence), Text: text, Origin: binding, Input: input,
                    Revision: input is not null && Desktop is not null ? Desktop.InputEpoch : 0), deadline.Token);
            }
            catch
            {
                // A partial record cannot safely be followed by another record.
                Close();
                throw;
            }
        }
        finally { writer.Release(); }
    }

    async Task RunHeartbeat()
    {
        using var delay = CancellationTokenSource.CreateLinkedTokenSource(closed.Token, stoppingHeartbeat.Token);
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), delay.Token);
                await Write("admin-input-heartbeat", null, closed.Token);
            }
        }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        catch { Close(); throw; }
    }

    void Close()
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        closed.Cancel();
        Desktop?.Dispose();
        pipe.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Close();
        try { await heartbeat; }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        if (serviceWatch is not null) await serviceWatch;
        if (serviceLease is not null) await serviceLease.DisposeAsync();
    }
}
