using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using RelaxDesk.Core;

internal static class FilePacingChecks
{
    sealed class MemorySink : IIncomingTransferFile
    {
        readonly MemoryStream bytes = new();
        public Task Write(long offset, byte[] data, CancellationToken ct)
        {
            if (offset != bytes.Length) throw new InvalidDataException("Sink offset mismatch");
            bytes.Write(data); return Task.CompletedTask;
        }
        public Task Commit(string expectedHash, CancellationToken ct)
        {
            if (Convert.ToHexString(SHA256.HashData(bytes.ToArray())) != expectedHash)
                throw new InvalidDataException("Paced download hash mismatch");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { bytes.Dispose(); return ValueTask.CompletedTask; }
    }

    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidDataException("File pacing assertion " + (checks + 1)); checks++; }
        foreach (string mode in new[] { "upload", "download", "download-heartbeat", "download-reduce", "release", "cancel", "download-cancel" })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var clientSocket = new TcpClient { NoDelay = true };
            var accept = listener.AcceptTcpClientAsync(timeout.Token);
            await clientSocket.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
            using var serverSocket = await accept; serverSocket.NoDelay = true;
            using var clientStream = new TrafficAccountingStream(clientSocket.GetStream(), leaveOpen: true);
            await using var client = new FileTransferClient(clientStream, timeout.Token, 0);
            client.SetLinkRateCeiling(FileTransferClient.LinkRateForVideoBitrate(100_000));
            byte[] payload = Enumerable.Range(0, mode == "download-heartbeat" ? 20_000 : 6_250).Select(i => (byte)i).ToArray();
            var firstChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int largestChunk = 0, pings = 0;
            var server = Task.Run(async () =>
            {
                Stream stream = serverSocket.GetStream(); long outgoing = 0;
                Task Send(TransferMessage m) => FileDataProtocol.Write(stream, m with { Sequence = ++outgoing }, timeout.Token);
                var request = await FileDataProtocol.Read(stream, timeout.Token);
                await Send(new("ready", request.Id, Length: payload.Length));
                if (request.Kind == "download")
                {
                    await Send(new("chunk", request.Id, Data: payload)); firstChunk.TrySetResult();
                }
                using var received = new MemoryStream();
                while (true)
                {
                    var message = await FileDataProtocol.Read(stream, timeout.Token);
                    switch (message.Kind)
                    {
                        case "ping": Interlocked.Increment(ref pings); await Send(new("pong", message.Id)); break;
                        case "cancel": await Send(new("result", request.Id, Error: "cancelled")); return;
                        case "chunk":
                            Check(message.Offset == received.Length);
                            largestChunk = Math.Max(largestChunk, message.Data!.Length);
                            received.Write(message.Data); await Send(new("progress", request.Id, Offset: received.Length));
                            firstChunk.TrySetResult(); break;
                        case "complete":
                            Check(received.ToArray().AsSpan().SequenceEqual(payload));
                            await Send(new("result", request.Id)); return;
                        case "progress":
                            Check(message.Offset == payload.Length);
                            await Send(new("complete", request.Id, Length: payload.Length, Hash: Convert.ToHexString(SHA256.HashData(payload)))); return;
                        default: throw new InvalidDataException("Unexpected pacing fixture message");
                    }
                }
            }, timeout.Token);
            var clock = Stopwatch.StartNew();
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task transfer = mode.StartsWith("download", StringComparison.Ordinal)
                ? client.Download("fixture.bin", _ => new MemorySink(), operation.Token)
                : client.Upload("fixture.bin", new MemoryStream(payload), false, operation.Token);
            await firstChunk.Task.WaitAsync(timeout.Token);
            if (mode == "download-reduce") client.SetLinkRateCeiling(1562);
            if (mode == "release") { client.SetLinkRateCeiling(0); await transfer; Check(clock.Elapsed.TotalSeconds < 3); }
            else if (mode is "cancel" or "download-cancel")
            {
                operation.Cancel();
                try { await transfer; throw new Exception("Paced transfer ignored cancellation"); }
                catch (OperationCanceledException) { checks++; }
                Check(clock.Elapsed.TotalSeconds < 3);
            }
            else
            {
                await transfer;
                Check(clock.Elapsed.TotalSeconds >= payload.Length / (mode == "download-reduce" ? 1562d : 3125d) * .9);
                if (mode == "upload") Check(largestChunk <= 3125 / 4);
                if (mode == "download-heartbeat") Check(pings >= 1);
            }
            await server.WaitAsync(timeout.Token);
            Check(clientStream.ReadBytes > 0 && clientStream.WrittenBytes > 0);
        }
        Console.WriteLine($"PASS {checks} file pacing loopback checks: 100 kbit/s target, upload/download payload budget, bounded upload chunks, heartbeat during download wait, live cap release and cancellation; no TLS/NIC shaping");
        return checks;
    }
}
