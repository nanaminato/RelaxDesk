using System.Text.Json;
using RelaxDesk.Core;
namespace RelaxDesk.Host;

// The UI bridge is local stdio only. Every action except STOP names the current
// session so delayed UI commands cannot affect a new connection.
internal sealed class ShellBridge : IDisposable
{
    readonly CancellationTokenSource stop;
    readonly object sync = new();
    Session? current;
    public ShellBridge(CancellationTokenSource stop)
    { this.stop = stop; _ = Task.Run(ReadCommands); }
    public static void Emit(string type, string? address = null, int port = 0, string[]? features = null, string? sessionId = null, SessionMode? mode = null, string? deviceFingerprint = null, string? requestId = null, string? desktopReason = null)
        => Console.WriteLine("RD_EVENT " + JsonSerializer.Serialize(new { type, address, port, features, sessionId, mode = mode?.ToString(), deviceFingerprint, requestId, desktopReason, version = BuildIdentity.Version }));
    public Session Begin()
    {
        lock (sync)
        {
            if (current is not null) throw new InvalidOperationException("Session already active");
            return current = new Session(this, stop.Token);
        }
    }
    async Task ReadCommands()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                string? line = await Console.In.ReadLineAsync(stop.Token);
                if (line is null or "STOP") { stop.Cancel(); return; }
                if (line.Length > 96) continue;
                string[] parts = line.Split(' ');
                if (parts.Length is not (2 or 3)) continue;
                lock (sync)
                {
                    if (current is not { } scope || parts[1] != scope.Id) continue;
                    if (parts.Length == 3)
                    {
                        if (parts[0] is "ALLOW_ADMIN_INPUT" or "DENY_ADMIN_INPUT")
                            scope.DecideAdmin(parts[2], parts[0] == "ALLOW_ADMIN_INPUT");
                        continue;
                    }
                    switch (parts[0])
                    {
                        case "DISCONNECT": scope.Lifetime.Cancel(); break;
                        case "REVOKE_CLIPBOARD": scope.Clipboard.Cancel(); break;
                        case "REVOKE_FILES": scope.Files.Cancel(); break;
                        case "REVOKE_AUDIO":scope.Audio.Cancel();break;
                        case "REVOKE_ADMIN_INPUT": if (scope.Mode is SessionMode.Admin or SessionMode.Work) scope.AdminInput.Cancel(); break;
                        case "REVOKE_GUEST_INPUT": if (scope.Mode == SessionMode.Guest && scope.GuestInput) scope.Input.Cancel(); break;
                        default:
                            if (!scope.Requested || scope.Consent.Decision.IsCompleted) break;
                            bool input = scope.Mode == SessionMode.Guest && parts[0].EndsWith("_INPUT", StringComparison.Ordinal);
                            bool admin = scope.Mode == SessionMode.Admin && parts[0].EndsWith("_ADMIN", StringComparison.Ordinal);
                            ConsentDecision? decision = AudioConsent.Parse(input || admin ? parts[0][..^6] : parts[0]);
                            if (!decision.HasValue) break;
                            scope.AdminConfirmed = admin && decision != ConsentDecision.Deny;
                            scope.GuestInput = input && decision != ConsentDecision.Deny;
                            scope.Consent.TryDecide(parts[1], decision.Value);
                            break;
                    }
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    public void Dispose() { lock (sync) { current?.Dispose(); } }

    internal sealed class Session : IDisposable
    {
        readonly ShellBridge owner;
        internal readonly ConsentRequest Consent = new();
        internal readonly CancellationTokenSource Lifetime, Clipboard = new(), Files = new(),Audio=new(),Input=new(),AdminInput=new();
        internal bool Requested;
        internal SessionMode? Mode;
        public bool AdminConfirmed { get; internal set; }
        public bool GuestInput { get; internal set; }
        bool disposed;
        ConsentRequest? pendingAdmin;
        public string Id => Consent.Id;
        public CancellationToken Token => Lifetime.Token;
        public CancellationToken ClipboardRevoked => Clipboard.Token;
        public CancellationToken FilesRevoked => Files.Token;
        public CancellationToken AudioRevoked=>Audio.Token;
        public CancellationToken AdminInputRevoked => AdminInput.Token;
        public CancellationToken InputRevoked => Input.Token;
        internal Session(ShellBridge owner, CancellationToken stop)
        { this.owner = owner; Lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop); }
        public Task<ConsentDecision> Request(string endpoint, string[] features, SessionMode? mode, string? deviceFingerprint, CancellationToken cancellation)
        {
            lock (owner.sync)
            {
                if (disposed || Requested) throw new InvalidOperationException("Consent request is no longer available");
                Requested = true;
                Mode = mode;
                Emit("approval-request", endpoint, features: features, sessionId: Id, mode: mode, deviceFingerprint: deviceFingerprint);
                return Consent.Decision.WaitAsync(cancellation);
            }
        }
        // The caller must park remote input before exposing this prompt.
        public async Task<bool> RequestAdmin(CancellationToken cancellation)
        {
            ConsentRequest request;
            CancellationTokenSource deadline;
            lock (owner.sync)
            {
                if (disposed || cancellation.IsCancellationRequested || Lifetime.IsCancellationRequested ||
                    Mode != SessionMode.Work || pendingAdmin is not null ||
                    !Consent.Decision.IsCompletedSuccessfully || Consent.Decision.Result == ConsentDecision.Deny)
                    return false;
                deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                request = pendingAdmin = new ConsentRequest();
            }
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                Emit("admin-approval-request", sessionId: Id, mode: SessionMode.Admin, requestId: request.Id);
                var decision = await request.Decision.WaitAsync(deadline.Token);
                return decision == ConsentDecision.ScreenAndInput && !deadline.IsCancellationRequested;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
            finally
            {
                lock (owner.sync) pendingAdmin = null;
                deadline.Dispose();
                request.Dispose();
                Emit("admin-approval-closed", sessionId: Id, requestId: request.Id);
            }
        }

        internal void DecideAdmin(string requestId, bool allow) =>
            pendingAdmin?.TryDecide(requestId, allow ? ConsentDecision.ScreenAndInput : ConsentDecision.Deny);
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (disposed) return;
                disposed = true;
                if (ReferenceEquals(owner.current, this)) owner.current = null;
                Consent.Dispose(); Lifetime.Cancel(); Lifetime.Dispose(); Clipboard.Dispose(); Files.Dispose();Audio.Dispose();Input.Dispose();AdminInput.Dispose();
            }
        }
    }
}
