using System.Net.Sockets;
using System.Text.Json;
namespace RelaxDesk.Host;

// Explicit stdio bridge for the separate WPF shell. No UI assembly is loaded
// by Host. Request decisions are accepted only while that request is pending.
internal sealed class ShellBridge : IDisposable
{
    readonly CancellationTokenSource stop;
    readonly TaskCompletionSource<string> decision = new(TaskCreationOptions.RunContinuationsAsynchronously);
    int pending;
    public ShellBridge(CancellationTokenSource stop)
    { this.stop = stop; _ = Task.Run(ReadCommands); }
    public static void Emit(string type, string? address = null, int port = 0)
        => Console.WriteLine("RD_EVENT " + JsonSerializer.Serialize(new { type, address, port, version = RelaxDesk.Core.BuildIdentity.Version }));
    public Task<string> Request(string endpoint)
    { Interlocked.Exchange(ref pending, 1); Emit("approval-request", endpoint); return decision.Task.WaitAsync(stop.Token); }
    async Task ReadCommands()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                string? command = await Console.In.ReadLineAsync(stop.Token);
                if (command is null or "STOP") { stop.Cancel(); return; }
                if (command is "APPROVE" or "DENY" && Interlocked.CompareExchange(ref pending, 0, 1) == 1) decision.TrySetResult(command);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
    }
    public void Dispose() { Interlocked.Exchange(ref pending, 0); }
}
