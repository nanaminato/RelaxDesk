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
                if (parts.Length != 2) continue;
                lock (sync)
                {
                    if (current is not { } scope || parts[1] != scope.Id) continue;
                    switch (parts[0])
                    {
                        case "DISCONNECT": scope.Lifetime.Cancel(); break;
                        case "REVOKE_CLIPBOARD": scope.Clipboard.Cancel(); break;
                        case "REVOKE_FILES": scope.Files.Cancel(); break;
                        case "REVOKE_AUDIO":scope.Audio.Cancel();break;
                        default:
                            if (!scope.Requested || scope.Consent.Decision.IsCompleted) break;
                            ConsentDecision? decision = AudioConsent.Parse(parts[0]);
                            if (!decision.HasValue) break;
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
        internal readonly CancellationTokenSource Lifetime, Clipboard = new(), Files = new(),Audio=new();
        internal bool Requested;
        bool disposed;
        public string Id => Consent.Id;
        public CancellationToken Token => Lifetime.Token;
        public CancellationToken ClipboardRevoked => Clipboard.Token;
        public CancellationToken FilesRevoked => Files.Token;
        public CancellationToken AudioRevoked=>Audio.Token;
        internal Session(ShellBridge owner, CancellationToken stop)
        { this.owner = owner; Lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop); }
        public Task<ConsentDecision> Request(string endpoint, string[] features, SessionMode? mode, string? deviceFingerprint, CancellationToken cancellation)
        {
            lock (owner.sync)
            {
                if (disposed || Requested) throw new InvalidOperationException("Consent request is no longer available");
                Requested = true;
                Emit("approval-request", endpoint, features: features, sessionId: Id, mode: mode, deviceFingerprint: deviceFingerprint);
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
                Consent.Dispose(); Lifetime.Cancel(); Lifetime.Dispose(); Clipboard.Dispose(); Files.Dispose();Audio.Dispose();
            }
        }
    }
}
