using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class AudioStreamContracts
{
    sealed class Endpoint : IAudioEndpoint
    {
        public readonly ConcurrentQueue<AudioFrame> Capture = new();
        public readonly ConcurrentQueue<byte[]> Written = new();
        public readonly TaskCompletionSource Attempted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Writable = true;
        public int MaximumWriteBytes = int.MaxValue;
        public bool IsDefaultDevice() => true;
        public AudioFrame? Read() => Capture.TryDequeue(out var frame) ? frame : null;
        public int Write(ReadOnlySpan<byte> samples)
        {
            Attempted.TrySetResult();
            if (!Volatile.Read(ref Writable)) return 0;
            int count = Math.Min(samples.Length, MaximumWriteBytes);
            Written.Enqueue(samples[..count].ToArray()); return count;
        }
        public void Flush() { }
        public void Dispose() { }
    }
    static long Now() => (long)(Stopwatch.GetTimestamp() * (10_000_000d / Stopwatch.Frequency));
    static AudioFrame Frame(long sequence, bool discontinuity, byte value) => new(sequence, Now(), discontinuity, Enumerable.Repeat(value, 1920).ToArray());

    public static async Task Run()
    {
        int checks = 0;
        void Check(bool value, string message) { if (!value) throw new IOException(message); checks++; }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task Until(Func<bool> condition) { while (!condition()) await Task.Delay(5, timeout.Token); }
        async Task WithPair(Func<NetworkStream, NetworkStream, Task> work)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
                using var server = await listener.AcceptTcpClientAsync(timeout.Token);
                await work(client.GetStream(), server.GetStream());
            }
            finally { listener.Stop(); }
        }
        await WithPair(async (writer, reader) =>
        {
            var endpoint = new Endpoint();
            await using var receiver = new AudioStreamSession(reader, false, () => endpoint, timeout.Token);
            var first = Frame(1, true, 17);
            await AudioProtocol.Write(writer, first, timeout.Token);
            await Until(() => receiver.Packets == 1);
            await Task.Delay(100, timeout.Token);
            Check(endpoint.Written.TryDequeue(out var output) && output.SequenceEqual(first.Data), "Discontinuity discarded the first real audio packet");
            Check(endpoint.Written.IsEmpty, "Audio samples duplicated");
        });
        await WithPair(async (writer, reader) =>
        {
            var endpoint = new Endpoint { Writable = false };
            await using var receiver = new AudioStreamSession(reader, false, () => endpoint, timeout.Token);
            await AudioProtocol.Write(writer, Frame(1, false, 11), timeout.Token);
            await endpoint.Attempted.Task.WaitAsync(timeout.Token);
            var replacement = Frame(2, true, 22);
            await AudioProtocol.Write(writer, replacement, timeout.Token);
            await Until(() => receiver.Packets == 2);
            await Task.Delay(30, timeout.Token);
            Volatile.Write(ref endpoint.Writable, true);
            await Task.Delay(100, timeout.Token);
            Check(endpoint.Written.TryDequeue(out var output) && output.SequenceEqual(replacement.Data), "Discontinuity lost new samples or retained old pending samples");
            Check(endpoint.Written.IsEmpty, "Stale audio replayed after discontinuity");
        });
        await WithPair(async (reader, writer) =>
        {
            var endpoint = new Endpoint();
            await using var sender = new AudioStreamSession(writer, true, () => endpoint, timeout.Token);
            await Task.Delay(120, timeout.Token);
            Check(sender.Packets == 0, "Empty loopback fabricated timed PCM and advanced the capture timeline");
            var actual = Frame(1, false, 31); endpoint.Capture.Enqueue(actual);
            var received = await AudioProtocol.Read(reader, timeout.Token);
            Check(received.Timestamp100ns == actual.Timestamp100ns && received.Data.SequenceEqual(actual.Data), "Capture changed real sample timing/data");
            await Task.Delay(100, timeout.Token);
            Check(sender.Packets == 1, "Capture inserted extra silence after a real packet");
        });
        await WithPair(async (writer, reader) =>
        {
            // A valid endpoint may accept only a small prefix. Progress must
            // refill available capacity without adding a poll delay per prefix.
            var endpoint = new Endpoint { MaximumWriteBytes = 48 };
            await using var receiver = new AudioStreamSession(reader, false, () => endpoint, timeout.Token);
            var frame = Frame(1, false, 42);
            await AudioProtocol.Write(writer, frame, timeout.Token);
            await Until(() => receiver.Packets == 1);
            await Task.Delay(100, timeout.Token);
            Check(endpoint.Written.SelectMany(bytes => bytes).SequenceEqual(frame.Data), "Partial audio writes were throttled below real-time playback or changed PCM");
        });
        Console.WriteLine($"PASS {checks} audio stream discontinuity/first-packet/stale-pending/no-fabricated-silence/PCM-timestamp contracts; real loopback TCP with synthetic endpoints, no physical sound claim");
    }
}
