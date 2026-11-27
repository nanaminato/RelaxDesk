using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

internal sealed class HostAdmission(TcpListener listener, HostMediaIdentity identity, ShellBridge shell, PasswordVerifier password,
    string backend, string? fileRoot, FileTransferResumeStore? resumes, string? display, CancellationToken stop)
{
    readonly AttachmentRouter attachments = new();
    readonly SingleSessionAdmission sessions = new();
    readonly AdmissionRateLimits limits = new();
    readonly DeviceTrustStore trust = DeviceTrustStore.CurrentUser();
    readonly SemaphoreSlim ingress = new(16);
    readonly SemaphoreSlim passwordWork = new(2);
    readonly HashSet<Task> workers = new();
    readonly object sync = new();
    int inputCleanupFailed;

    public async Task Run()
    {
        listener.Start(16);
        Listening();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var socket = await listener.AcceptTcpClientAsync(stop);
                var key = AdmissionRateLimits.AddressKey(((IPEndPoint)socket.Client.RemoteEndPoint!).Address);
                if (!ingress.Wait(0)) { socket.Dispose(); continue; }
                if (!limits.Take("tls-global", "", 480) || !limits.Take("tls-ip", key, 60))
                { ingress.Release(); socket.Dispose(); continue; }
                var work = Handle(socket, key);
                lock (sync) workers.Add(work);
                _ = work.ContinueWith(completed =>
                {
                    if (completed.IsFaulted)
                        HostSessionLog.Write($"admission_worker_failure type={completed.Exception!.GetBaseException().GetType().Name}");
                    lock (sync) workers.Remove(completed);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        finally
        {
            listener.Stop();
            Task[] remaining;
            lock (sync) remaining = workers.ToArray();
            try { await Task.WhenAll(remaining); }
            catch (Exception e) when (Expected(e)) { }
        }
    }

    void Listening() => ShellBridge.Emit("listening", ((IPEndPoint)listener.LocalEndpoint).Address.ToString(),
        ((IPEndPoint)listener.LocalEndpoint).Port, serverFingerprint: identity.Certificate.GetCertHashString(HashAlgorithmName.SHA256));

    async Task Handle(TcpClient socket, string key)
    {
        bool routed = false;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop);
        handshake.CancelAfter(TimeSpan.FromSeconds(8));
        var stream = new SslStream(socket.GetStream(), false);
        try
        {
            socket.NoDelay = true;
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity.Certificate, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
            }, handshake.Token);
            // The first Viewer may have to check the Host certificate before sending login.
            handshake.CancelAfter(TimeSpan.FromSeconds(20));
            Packet hello = await Wire.Read(stream, handshake.Token, 16 * 1024);
            try
            {
                if (hello.Kind != FullControlProtocol.PasswordRequest)
                {
                    if (hello.Data is null && hello.Kind is ("media-v2" or "data-v2" or "clipboard-v2" or "audio-v2"))
                        routed = attachments.Route(new(socket, stream, hello));
                    return;
                }
                handshake.CancelAfter(TimeSpan.FromSeconds(8));
                await Authenticate(socket, stream, hello, key, handshake.Token);
            }
            finally
            {
                if (hello.Data is not null) CryptographicOperations.ZeroMemory(hello.Data);
            }
        }
        catch (Exception error) when (Expected(error))
        {
            if (error is WindowsSessionUnavailableException desktop)
                ShellBridge.Emit("desktop-unavailable", desktopReason: desktop.Reason);
            HostSessionLog.Write($"admission_closed type={error.GetType().Name}");
        }
        finally
        {
            try { if (!routed) { socket.Dispose(); stream.Dispose(); } }
            finally { ingress.Release(); }
        }
    }

    async Task Authenticate(TcpClient socket, SslStream tls, Packet hello, string ip, CancellationToken handshake)
    {
        if (!limits.Take("auth-global", "", 120) || !limits.Take("auth-ip", ip, 20))
        { await Status(tls, "admission-rate-limited-v5", handshake); return; }
        if (hello.DeviceKey is not { Length: > 0 and <= 512 } key || hello.DeviceCredential is { Length: > 800 })
        { await Status(tls, FullControlProtocol.PasswordRejected, handshake); return; }

        var features = HostConnection.ValidateRequest(hello);
        string device = await HostConnection.VerifyDeviceIdentity(tls, identity.Certificate, key, handshake);
        string ipFailure = "ip:" + ip, deviceFailure = "device:" + device;
        if (!limits.CanVerifyPassword(ipFailure) || !limits.CanVerifyPassword(deviceFailure) ||
            !limits.Take("password-global", "", 60) || !limits.Take("password-ip", ip, 10) ||
            !limits.Take("password-device", device, 6) || !passwordWork.Wait(0))
        { await Status(tls, "admission-rate-limited-v5", handshake); return; }
        bool correct;
        try { correct = await Task.Run(() => password.Verify(hello.Text, hello.Data), handshake); }
        finally
        {
            passwordWork.Release();
            if (hello.Data is not null) CryptographicOperations.ZeroMemory(hello.Data);
        }
        if (!correct)
        {
            limits.PasswordFailed(ipFailure);
            limits.PasswordFailed(deviceFailure);
            await Status(tls, FullControlProtocol.PasswordRejected, handshake);
            return;
        }
        limits.ClearPasswordFailures(deviceFailure);
        handshake.ThrowIfCancellationRequested();
        hello = hello with { Data = null, Text = null, DeviceKey = null };

        if (Volatile.Read(ref inputCleanupFailed) != 0)
        { await Status(tls, FullControlProtocol.InputUnavailable, handshake); return; }
        SingleSessionAdmission.Lease? lease = sessions.Begin();
        bool replacing = lease is null;
        using var decision = CancellationTokenSource.CreateLinkedTokenSource(stop);
        decision.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (lease is null)
            {
                var current = sessions.ReadReady();
                if (current is null)
                { await Status(tls, "admission-pending-v5", decision.Token); return; }
                if (!await Known(device) || hello.ReconnectOnly || hello.DeviceCredential is null)
                { await Status(tls, "admission-busy-v5", decision.Token); return; }

                var offer = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(30));
                await Wire.Write(tls, new("takeover-offer-v5", Text: offer.Value), decision.Token);
                var confirm = await Wire.Read(tls, decision.Token, 2048);
                if (confirm == new Packet("takeover-cancel-v5")) return;
                if (confirm != new Packet("takeover-confirm-v5", Text: confirm.Text) ||
                    !offer.Consume(confirm.Text, DateTimeOffset.UtcNow))
                    throw new AuthenticationException("Invalid take-over confirmation");
                if (!await Known(device))
                { await Status(tls, "admission-busy-v5", decision.Token); return; }

                decision.Token.ThrowIfCancellationRequested();
                CheckDesktop();
                lease = shell.IfDeviceNotRevoked(device, () => sessions.ReserveReplacement(current));
                if (lease is null)
                { await Status(tls, "admission-stale-v5", decision.Token); return; }
                // The user's decision timeout ends at confirmation. Give the
                // old session an independent, bounded cleanup interval.
                HostSessionLog.Write("session_takeover confirmed");
                decision.CancelAfter(TimeSpan.FromSeconds(12));
                await current.Completion.WaitAsync(decision.Token);
                if (Volatile.Read(ref inputCleanupFailed) != 0)
                { await Status(tls, FullControlProtocol.InputUnavailable, decision.Token); return; }
                if (!await Known(device) || !sessions.Promote(lease))
                { await Status(tls, "admission-stale-v5", decision.Token); return; }
            }

            stop.ThrowIfCancellationRequested();
            CheckDesktop();
            using var diagnostic = DiagnosticCorrelation.Begin();
            using var scope = shell.Begin();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(scope.Token, lease.Replacement);
            try
            {
                // The shell remains responsible for the first approval. A
                // replacement may only use current trust and must never prompt.
                var approval = await HostConnection.RequestConsent(socket, scope, features, false, device,
                    lifetime.Token, passwordLogin: true, reconnectOnly: replacing || hello.ReconnectOnly);
                if (approval != "APPROVE")
                {
                    ShellBridge.Emit("denied", sessionId: scope.Id);
                    await Status(tls, "denied-v2", lifetime.Token);
                    return;
                }
                string? credential = null;
                if (hello.DeviceCredential is { } previous && await Known(device) &&
                    !DeviceAdmissionCredential.Verify(identity.Certificate, device, previous, DateTimeOffset.UtcNow) &&
                    limits.Take("issue-global", "", 24) && limits.Take("issue-ip", ip, 6) &&
                    limits.Take("issue-device", device, 2))
                    credential = DeviceAdmissionCredential.Issue(identity.Certificate, device, DateTimeOffset.UtcNow);
                ShellBridge.Emit("approved", sessionId: scope.Id);
                await HostConnection.RunApproved(listener, socket, tls, identity, hello, backend, fileRoot,
                    scope, lifetime.Token, resumes, display, attachments, lease, sessions, credential);
            }
            finally { ShellBridge.Emit("session-ended", sessionId: scope.Id); }
        }
        catch (SessionInputCleanupException)
        {
            // Fail closed: no subsequent session may inject into this desktop
            // if the previous remote input release was not confirmed.
            Volatile.Write(ref inputCleanupFailed, 1);
            ShellBridge.Emit("admission-input-cleanup-failed");
            throw;
        }
        finally
        {
            if (lease is not null)
            {
                // No state publication from a pending contender can overwrite
                // the active Host UI. End only the lease held by this worker.
                bool clean = Volatile.Read(ref inputCleanupFailed) == 0;
                sessions.End(lease, clean, stop.IsCancellationRequested ? null : Listening);
            }
        }
    }

    void CheckDesktop()
    {
        WindowsInputPrivilege.EnsureOrdinaryProcess();
        WindowsSessionAccess.EnsureAllowed();
        _ = new WindowsDisplaySelection(display).Device;
    }

    async Task<bool> Known(string device)
    {
        try { return !shell.IsDeviceRevoked(device) && Array.IndexOf(await trust.Load(), device) >= 0; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or System.Security.SecurityException)
        { return false; }
    }

    static Task Status(SslStream stream, string value, CancellationToken ct) => Wire.Write(stream, new(value), ct);
    static bool Expected(Exception error) => error is IOException or SocketException or AuthenticationException or
        OperationCanceledException or InvalidDataException or JsonException or CryptographicException or
        System.Runtime.InteropServices.ExternalException or System.Threading.Channels.ChannelClosedException;
}

internal sealed class SessionInputCleanupException(Exception inner) : IOException("Could not confirm remote input release", inner);
