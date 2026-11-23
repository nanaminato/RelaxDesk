using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal async Task ExerciseReconnect(RecoveryHost driver, RecoveryRelay relay, string hostPath, string root, CancellationToken ct)
    {
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(20, ct); }
        void Snapshot(string name)
        {
            UpdateLayout(); var content = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32); bitmap.Render(content);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(root, name)); encoder.Save(file);
        }
        async Task Approved(string command = "APPROVE")
        {
            var request = await driver.Request(ct);
            await Until(() => connectionPhase == ConnectionPhase.AwaitingApproval);
            int frames = smokeFrames;
            await driver.Send(command, request.Id);
            await Until(() => sessionPermissions is not null && smokeFrames > frames);
        }
        async Task ManualStart()
        {
            await driver.Listening(ct);
            _ = ConnectVideo(); await Approved();
        }
        // Initial first-contact approval, real production file grant and revoke.
        var first = await driver.Request(ct);
        if (first.Features is not [SessionCapabilities.FileTransfer]) throw new IOException("Initial requested feature missing");
        await driver.Send("APPROVE_FILES", first.Id);
        await Until(() => smokeFrames > 0 && fileQueue?.Enabled == true);
        StopFilePermission(); await Until(() => fileRevocationReplies > 0);
        ToggleFullScreen(); heldInputs.Update(65, false); // Local state only; no SendInput.
        int before = connectionGeneration;
        relay.Cut(); await Until(() => connectionPhase == ConnectionPhase.Reconnecting);
        Snapshot("reconnect-backoff.png");
        outgoing.TryWrite(new("input", 999, Code: 65)); // Must be discarded before reconnect.
        var fresh = await driver.Request(ct);
        if (fresh.Features.Length != 0 || fresh.Id == first.Id || sessionPermissions is not null) throw new IOException("Recovery inherited access or skipped consent");
        int oldFrames = smokeFrames;
        await driver.Send("APPROVE", fresh.Id);
        await Until(() => connectionGeneration == before + 1 && smokeFrames > oldFrames);
        await Task.Delay(300, ct);
        if (!fullScreen || sessionPermissions?.Allows(SessionCapabilities.FileTransfer) != false || fileQueue is not null || sessionToolbar?.FilesButton.IsEnabled != false || heldInputs.ReleaseAll().Length != 0) throw new IOException("Recovery viewport/input/permission state incorrect");
        int delayedNotices = relay.DelayedControlWrites;
        relay.DelayControlDownstream(TimeSpan.FromMilliseconds(150));
        await driver.Send("DISCONNECT", fresh.Id); await connectionEnded.Task.WaitAsync(ct);
        if (relay.DelayedControlWrites <= delayedNotices) throw new IOException("Explicit-stop control delay fixture was not exercised");
        int ended = connectionGeneration; await Task.Delay(1500, ct);
        if (connectionGeneration != ended || connectionPhase != ConnectionPhase.Disconnected || connectionRun is not null || !GraphicsDiagnostics.Resources().Empty) throw new IOException("Explicit Host end retried or leaked resources");
        Console.WriteLine("PASS actual WPF recovery: loopback network cut, fresh Host approval, same identity, revoked files not requested again, fresh decoder/video/toolbar, fullscreen restored, old held/queued input cleared, delayed explicit Host-end control notice stops retry after media close, native resources zero");

        // A denied automatic retry must stop rather than prompting repeatedly.
        await ManualStart(); relay.Cut();
        var denied = await driver.Request(ct); await driver.Send("DENY", denied.Id);
        await connectionEnded.Task.WaitAsync(ct); ended = connectionGeneration; await Task.Delay(1500, ct);
        if (connectionGeneration != ended || connectionPhase != ConnectionPhase.Failed) throw new IOException("Denied recovery continued retrying");
        Snapshot("reconnect-denied.png");
        Console.WriteLine("PASS WPF denied recovery stops with no repeated consent request");

        // A user disconnect during backoff cancels the full run, not one attempt.
        await ManualStart(); relay.Cut(); await Until(() => connectionPhase == ConnectionPhase.Reconnecting);
        disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await connectionEnded.Task.WaitAsync(ct);
        ended = connectionGeneration; await Task.Delay(1500, ct);
        if (connectionGeneration != ended || connectionPhase != ConnectionPhase.Disconnected || connectionRun is not null) throw new IOException("User disconnect did not cancel backoff");
        Console.WriteLine("PASS WPF user disconnect cancels pending backoff without a new connection");

        // A restarted Host has a new ephemeral certificate: never silently trust it.
        await ManualStart(); relay.Cut(); await Until(() => connectionPhase == ConnectionPhase.Reconnecting);
        await driver.Stop(ct);
        await using var replacement = new RecoveryHost(hostPath, driver.Port, root);
        await replacement.Listening(ct); await connectionEnded.Task.WaitAsync(ct);
        ended = connectionGeneration; await Task.Delay(1500, ct);
        if (connectionGeneration != ended || replacement.Requests != 0 || connectionPhase != ConnectionPhase.Failed || !GraphicsDiagnostics.Resources().Empty) throw new IOException("Changed Host identity accepted or retried");
        Console.WriteLine("PASS WPF changed Host certificate stops recovery before approval; no identity downgrade; all Viewer native resources released");

        // Real retries against an unavailable backend must exhaust at five,
        // including when the relay accepts TCP but cannot reach the Host.
        driver = replacement;
        await ManualStart(); int connectedGeneration = connectionGeneration;
        relay.Cut(); await Until(() => connectionPhase == ConnectionPhase.Reconnecting); await replacement.Stop(ct);
        await connectionEnded.Task.WaitAsync(ct);
        if (connectionGeneration != connectedGeneration + ConnectionRecovery.MaximumRetries || connectionPhase != ConnectionPhase.Failed || !GraphicsDiagnostics.Resources().Empty) throw new IOException("Recovery retry limit not enforced");
        Console.WriteLine("PASS WPF unavailable Host exhausts exactly five real backoff retries and releases resources");
    }
}

internal sealed class RecoveryHost : IAsyncDisposable
{
    internal sealed record Notice(string Type, string? Id, string[] Features);
    readonly Process child;
    readonly Task output;
    readonly Task<string> errors;
    readonly Channel<Notice> events = Channel.CreateUnbounded<Notice>();
    readonly ConcurrentQueue<string> logs = new();
    int requests;
    public int Port { get; }
    public int Requests => Volatile.Read(ref requests);
    public RecoveryHost(string host, int port, string root)
    {
        Port = port;
        var start = new ProcessStartInfo(host.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? host : "dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (!host.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(host);
        foreach (string argument in new[] { "--shell", "--file-root", root, "127.0.0.1", port.ToString(), "test-hardware" }) start.ArgumentList.Add(argument);
        child = Process.Start(start) ?? throw new IOException("Recovery Host launch failed");
        output = Pump(); errors = child.StandardError.ReadToEndAsync();
    }
    async Task Pump()
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                if (!line.StartsWith("RD_EVENT ")) { logs.Enqueue(line); continue; }
                using var json = JsonDocument.Parse(line[9..]); var root = json.RootElement;
                string type = root.GetProperty("type").GetString()!;
                string? id = root.TryGetProperty("sessionId", out var identity) ? identity.GetString() : null;
                string[] features = root.TryGetProperty("features", out var values) && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().Select(value => value.GetString()!).ToArray() : [];
                if (type == "approval-request") Interlocked.Increment(ref requests);
                events.Writer.TryWrite(new(type, id, features));
            }
        }
        finally { events.Writer.TryComplete(); }
    }
    async Task<Notice> Until(string type, CancellationToken ct)
    { while (true) { var next = await events.Reader.ReadAsync(ct); if (next.Type == type) return next; } }
    public Task<Notice> Request(CancellationToken ct) => Until("approval-request", ct);
    public Task Listening(CancellationToken ct) => Until("listening", ct);
    public async Task Send(string command, string? id)
    { await child.StandardInput.WriteLineAsync(id is null ? command : command + " " + id); await child.StandardInput.FlushAsync(); }
    public async Task Stop(CancellationToken ct)
    {
        if (!child.HasExited) { await Send("STOP", null); await child.WaitForExitAsync(ct); }
        await output; string stderr = await errors;
        if (child.ExitCode != 0 || stderr.Length != 0 || logs.Any(line => line.Contains("native_live_resources=1")||line.StartsWith("file_resume_cleanup_failed"))) throw new IOException("Recovery Host cleanup/exit failed: " + stderr);
    }
    public async ValueTask DisposeAsync()
    {
        if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); }
        await output; child.Dispose();
    }
}

// Test-only TCP relay: cutting established streams models real transport EOF
// without changing production cancellation policy or injecting desktop input.
internal sealed class RecoveryRelay : IAsyncDisposable
{
    readonly TcpListener listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource stop = new();
    readonly object sync = new();
    readonly List<TcpClient> sockets = [];
    readonly List<Task> workers = [];
    readonly Task accept;
    readonly int destination;
    int nextLink, delayedControlWrites;
    long controlDelayUntil;
    public int DelayedControlWrites => Volatile.Read(ref delayedControlWrites);
    public void DelayControlDownstream(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero || duration > TimeSpan.FromSeconds(1)) throw new ArgumentOutOfRangeException(nameof(duration));
        Interlocked.Exchange(ref controlDelayUntil, Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency));
    }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public RecoveryRelay(int destination) { this.destination = destination; listener.Start(); accept = Accept(); }
    async Task Accept()
    {
        try
        {
            while (true)
            {
                var peer = await listener.AcceptTcpClientAsync(stop.Token);
                lock (sync) { workers.RemoveAll(task => task.IsCompleted); workers.Add(Forward(peer, nextLink++ == 0)); }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    async Task CopyOutbound(Stream source, Stream target, bool control)
    {
        byte[] bytes = new byte[4096];
        while (true)
        {
            int count = await source.ReadAsync(bytes, stop.Token); if (count == 0) return;
            double remaining = control ? (Interlocked.Read(ref controlDelayUntil) - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency : 0;
            if (remaining > 0) { Interlocked.Increment(ref delayedControlWrites); await Task.Delay(TimeSpan.FromSeconds(remaining), stop.Token); }
            await target.WriteAsync(bytes.AsMemory(0, count), stop.Token);
        }
    }
    async Task Forward(TcpClient peer, bool control)
    {
        using var upstream = new TcpClient(); using (peer)
        {
            lock (sync) { sockets.Add(peer); sockets.Add(upstream); }
            try
            {
                peer.NoDelay = upstream.NoDelay = true; await upstream.ConnectAsync(IPAddress.Loopback, destination, stop.Token);
                Task inbound = peer.GetStream().CopyToAsync(upstream.GetStream(), stop.Token);
                Task outbound = CopyOutbound(upstream.GetStream(), peer.GetStream(), control);
                await Task.WhenAny(inbound, outbound); peer.Close(); upstream.Close();
                try { await Task.WhenAll(inbound, outbound); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            finally { lock (sync) { sockets.Remove(peer); sockets.Remove(upstream); } }
        }
    }
    public void Cut() { lock (sync) { foreach (var socket in sockets) socket.Close(); nextLink = 0; } }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); listener.Stop(); Cut(); await accept;
        Task[] pending; lock (sync) pending = workers.ToArray(); await Task.WhenAll(pending); stop.Dispose();
    }
}

internal static class ReconnectVerification
{
    public static void Run(Application app, string host, string output)
    {
        string root = Path.Combine(Path.GetFullPath(output), "recovery-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var reservation = new TcpListener(IPAddress.Loopback, 0); reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        var driver = new RecoveryHost(host, port, root); var relay = new RecoveryRelay(port);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        driver.Listening(deadline.Token).GetAwaiter().GetResult();
        var window = new ViewerWindow(new("127.0.0.1", relay.Port, "", "", Renderer: "cpu", Frames: 1, ReconnectTest: true));
        window.Loaded += async (_, _) =>
        {
            try { await window.ExerciseReconnect(driver, relay, host, root, deadline.Token); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally
            {
                await relay.DisposeAsync(); await driver.DisposeAsync(); window.Close();
            }
        };
        app.Run(window);
    }
}
