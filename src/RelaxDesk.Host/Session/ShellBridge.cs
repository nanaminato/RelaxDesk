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
    // The UI queues this before editing the trust file. Serializing it with
    // the commit point prevents a later confirmation from kicking another session.
    readonly HashSet<string> revoked = new(StringComparer.Ordinal);
    bool revocationOverflow;

    public bool IsDeviceRevoked(string fingerprint)
    {
        lock (sync) return revocationOverflow || revoked.Contains(fingerprint);
    }

    public T? IfDeviceNotRevoked<T>(string fingerprint, Func<T?> action) where T : class
    {
        lock (sync) return revocationOverflow || revoked.Contains(fingerprint) ? default : action();
    }
    public ShellBridge(CancellationTokenSource stop)
    { this.stop = stop; _ = Task.Run(ReadCommands); }
    public static void Emit(string type, string? address = null, int port = 0, string[]? features = null, string? sessionId = null, bool fullControl = false, string? deviceFingerprint = null, string? requestId = null, string? desktopReason = null, string? serverFingerprint = null, bool passwordLogin = false, bool reconnectOnly = false)
        => Console.WriteLine("RD_EVENT " + JsonSerializer.Serialize(new { type, address, port, features, sessionId, fullControl, deviceFingerprint, requestId, desktopReason, serverFingerprint, passwordLogin, reconnectOnly, version = BuildIdentity.Version }));
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
                if (parts.Length != 2) continue;
                lock (sync)
                {
                    if (parts[0] == "REVOKE" && RelaxDesk.Windows.DeviceTrustStore.IsFingerprint(parts[1]))
                    {
                        if (revoked.Count >= 128) revocationOverflow = true;
                        else revoked.Add(parts[1]);
                        if (current?.Device == parts[1]) current.Lifetime.Cancel();
                        continue;
                    }
                    if (current is not { } scope || parts[1] != scope.Id) continue;
                    switch (parts[0])
                    {
                        case "DISCONNECT": scope.Lifetime.Cancel(); break;
                        default:
                            if (!scope.Requested || scope.Consent.Decision.IsCompleted) break;
                            ConsentDecision? decision = parts[0] switch { "APPROVE" or "APPROVE_NEW"=>ConsentDecision.Allow,"DENY"=>ConsentDecision.Deny,_=>null };
                            if (!decision.HasValue) break;
                            if (decision == ConsentDecision.Allow && scope.Device is { } device)
                            {
                                if (parts[0] == "APPROVE_NEW") revoked.Remove(device);
                                else if (revoked.Contains(device) || revocationOverflow) decision = ConsentDecision.Deny;
                            }
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
        internal readonly CancellationTokenSource Lifetime;
        internal bool Requested;
        internal string? Device;
        bool disposed;
        public string Id => Consent.Id;
        public CancellationToken Token => Lifetime.Token;
        internal Session(ShellBridge owner, CancellationToken stop)
        { this.owner = owner; Lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop); }
        public Task<ConsentDecision> Request(string endpoint, string[] features, bool fullControl, string? deviceFingerprint, CancellationToken cancellation, bool passwordLogin = false, bool reconnectOnly = false)
        {
            lock (owner.sync)
            {
                if (disposed || Requested) throw new InvalidOperationException("Consent request is no longer available");
                Requested = true;
                Device = deviceFingerprint;
                Emit("approval-request", endpoint, features: features, sessionId: Id, fullControl: fullControl, deviceFingerprint: deviceFingerprint, passwordLogin: passwordLogin, reconnectOnly: reconnectOnly);
                return Consent.Decision.WaitAsync(cancellation);
            }
        }
        public void Dispose()
        {
            lock (owner.sync)
            {
                if (disposed) return;
                disposed = true;
                if (ReferenceEquals(owner.current, this)) owner.current = null;
                Consent.Dispose(); Lifetime.Cancel(); Lifetime.Dispose();
            }
        }
    }
}
