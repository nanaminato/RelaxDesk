using System.Net;
using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.Security;
using System.Net.Quic;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class QuicMediaAttachmentChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool condition)
        {
            if (!condition) throw new IOException($"QUIC media attachment check {count + 1} failed");
            count++;
        }

        var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddMinutes(1));
        var foreign = new SessionTicket(DateTimeOffset.UtcNow.AddMinutes(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        async Task Verify(SessionTicket expected, Func<Stream, Task> client, bool accepted, int selected = 0)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                using var viewer = new TcpClient();
                await viewer.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
                using var host = await listener.AcceptTcpClientAsync(timeout.Token);
                var serving = QuicMediaAttachment.Accept(host.GetStream(), expected, 1100, timeout.Token);
                await client(viewer.GetStream());
                try
                {
                    int result = await serving;
                    Check(accepted && result == selected);
                }
                catch (AuthenticationException) { Check(!accepted); }
            }
            finally { listener.Stop(); }
        }

        Task Bind(Stream stream, string supplied, int width, int code = 0) =>
            Wire.Write(stream, new("quic-media-bind-v2", Width: width, Text: supplied, Code: code), timeout.Token);

        await Verify(ticket, stream => Bind(stream, foreign.Value, 1200), false);
        await Verify(ticket, stream => Bind(stream, ticket.Value, 1200, 1), false);
        await Verify(ticket, stream => Bind(stream, ticket.Value, 1201), false);
        await Verify(ticket, async stream => Check(await QuicMediaAttachment.Connect(stream, ticket.Value, 1200, timeout.Token) == 1100), true, 1100);
        await Verify(ticket, stream => Bind(stream, ticket.Value, 1100), false);
        var expired = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(-1));
        await Verify(expired, stream => Bind(stream, expired.Value, 1000), false);
        var smaller = new SessionTicket(DateTimeOffset.UtcNow.AddMinutes(1));
        await Verify(smaller, async stream => Check(await QuicMediaAttachment.Connect(stream, smaller.Value, 850, timeout.Token) == 850), true, 850);
        Console.WriteLine($"PASS {count} QUIC media attachment contracts over reliable loopback stream; transport encryption verified separately");
        count += await VerifyDatagramBinding(timeout.Token);
        var offer = QuicRecoveryProtocol.Offer(2, 42100, ticket.Value);
        using (var recoveryWire = new MemoryStream())
        {
            await Wire.Write(recoveryWire, offer, timeout.Token);
            recoveryWire.Position = 0;
            Check(QuicRecoveryProtocol.IsOffer(await Wire.Read(recoveryWire, timeout.Token, 2048), 1));
        }
        Check(!QuicRecoveryProtocol.IsOffer(offer, 2));
        Check(!QuicRecoveryProtocol.IsOffer(offer with { QuicPort = 0 }, 1));
        Check(!QuicRecoveryProtocol.IsOffer(offer with { QuicTicket = "1234" }, 1));
        Check(!QuicRecoveryProtocol.IsOffer(offer with { DeviceKey = "unexpected" }, 1));
        Check(QuicRecoveryProtocol.ReadResult(QuicRecoveryProtocol.Result(2, 1100), 2, 1100) == 1100);
        Check(QuicRecoveryProtocol.ReadResult(QuicRecoveryProtocol.Result(2, 0), 2, 1100) == 0);
        foreach (var invalid in new[] { QuicRecoveryProtocol.Result(1, 1100), QuicRecoveryProtocol.Result(2, 1200), QuicRecoveryProtocol.Result(2, 1100) with { Code = 1 } })
        {
            bool rejected = false;
            try { QuicRecoveryProtocol.ReadResult(invalid, 2, 1100); }
            catch (AuthenticationException) { rejected = true; }
            Check(rejected);
        }
        Console.WriteLine("PASS QUIC recovery offer/result framing, replay and bound-budget checks");
        if (OperatingSystem.IsWindows() && QuicListener.IsSupported)
        {
            await VerifyPinnedQuic(timeout.Token);
            count++;
        }
        await VerifyRecoveryDeadline();
        count++;
        return count;
    }

    static async Task VerifyRecoveryDeadline()
    {
        using var control = new MemoryStream();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var tcpFrame = new TaskCompletionSource<MediaFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var key = new MediaFrame(MediaCodec.H264, 500, 0, 1280, 720, true, false, 0, 0, [], [1]);
        int deltas = 0, closed = 0;
        var tcp = new ReadChannel("TCP/TLS", ct => tcpFrame.Task.WaitAsync(ct));
        var udp = new ReadChannel("UDP", async ct =>
        {
            await Task.Delay(25, ct);
            return key with { Sequence = ++deltas, KeyFrame = false };
        });
        var channel = new AutoFallbackVideoChannel(null, tcp, control, () =>
        {
            closed++;
            tcpFrame.SetResult(key);
        }, _ => throw new IOException("A delta frame activated UDP"), allowRecovery: true);
        if (!channel.StageRecovery(udp)) throw new IOException("QUIC fixture could not stage the candidate");
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var delivered = await channel.Read(timeout.Token);
        control.Position = 0;
        if (delivered != key || channel.TransportName != "TCP/TLS" || closed != 1 || deltas < 5 ||
            timer.Elapsed < QuicRecoveryProtocol.FirstFrameLimit - TimeSpan.FromMilliseconds(250) ||
            await Wire.Read(control, timeout.Token) != new Packet("media-fallback-v2"))
            throw new IOException("QUIC recovery did not expire while receiving undecodable frames");
        Console.WriteLine($"PASS QUIC recovery first-frame deadline after {deltas} UDP delta frames; TCP key frame and fallback request");
    }

    sealed class ReadChannel(string name, Func<CancellationToken, Task<MediaFrame>> read) : IVideoFrameChannel
    {
        public string TransportName => name;
        public Task<MediaFrame> Read(CancellationToken cancellation) => read(cancellation);
        public Task Write(MediaFrame frame, CancellationToken cancellation) => throw new NotSupportedException();
    }

    static async Task<int> VerifyDatagramBinding(CancellationToken cancellation)
    {
        int count = 0;
        void Check(bool condition)
        {
            if (!condition) throw new IOException($"QUIC datagram binding check {count + 1} failed");
            count++;
        }

        async Task Reject(Func<Task> operation)
        {
            try { await operation(); }
            catch (AuthenticationException) { count++; return; }
            throw new IOException("QUIC datagram binding accepted an invalid offer");
        }

        var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddMinutes(1));
        using var viewer = new DatagramFixture(1200);
        await Reject(() => QuicDatagramAttachment.Connect(viewer, "00", cancellation));
        await Reject(() => QuicDatagramAttachment.Connect(viewer, new string('z', 64), cancellation));
        Check(viewer.Sent is null);

        byte[] response = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(response, 0x31435152);
        BinaryPrimitives.WriteInt32LittleEndian(response.AsSpan(4), 1100);
        viewer.Incoming = response;
        Check(await QuicDatagramAttachment.Connect(viewer, ticket.Value, cancellation) == 1100);
        Check(viewer.SendBuffer.Span.IndexOfAnyExcept((byte)0) == -1);

        using var invalidHost = new DatagramFixture(1) { Incoming = viewer.Sent! };
        await Reject(() => QuicDatagramAttachment.Accept(invalidHost, ticket, cancellation));
        Check(invalidHost.Sent is null);
        using var host = new DatagramFixture(1100) { Incoming = viewer.Sent! };
        var foreign = new SessionTicket(DateTimeOffset.UtcNow.AddMinutes(1));
        await Reject(() => QuicDatagramAttachment.Accept(host, foreign, cancellation));
        Check(await QuicDatagramAttachment.Accept(host, ticket, cancellation) == 1100);
        Check(host.Sent!.AsSpan().SequenceEqual(response));
        await Reject(() => QuicDatagramAttachment.Accept(host, ticket, cancellation));
        Console.WriteLine($"PASS {count} QUIC datagram binding contracts using in-memory datagrams");
        return count;
    }

    sealed class DatagramFixture(int capacity) : IQuicDatagrams
    {
        public ReadOnlyMemory<byte> Incoming { get; set; }
        public byte[]? Sent { get; private set; }
        public ReadOnlyMemory<byte> SendBuffer { get; private set; }
        public Task<int> WaitForBudget(CancellationToken cancellation) => Task.FromResult(capacity);
        public ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellation) => ValueTask.FromResult(Incoming);
        public ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation)
        {
            SendBuffer = data;
            Sent = data.ToArray();
            return ValueTask.CompletedTask;
        }
        public void Dispose() { }
    }

    [SupportedOSPlatform("windows")]
    static async Task VerifyPinnedQuic(CancellationToken cancellation)
    {
        using var certificate = SessionCertificate.Create();
        byte[] expectedPin = certificate.GetCertHash(HashAlgorithmName.SHA256);
        var protocol = new SslApplicationProtocol("relaxdesk-media-attachment-test");
        await using var listener = await QuicListener.ListenAsync(new()
        {
            ListenEndPoint = new IPEndPoint(IPAddress.Loopback, 0),
            ApplicationProtocols = [protocol],
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(new QuicServerConnectionOptions
            {
                DefaultCloseErrorCode = 1,
                DefaultStreamErrorCode = 1,
                MaxInboundBidirectionalStreams = 1,
                ServerAuthenticationOptions = new() { ApplicationProtocols = [protocol], ServerCertificate = certificate }
            })
        }, cancellation);
        var pending = listener.AcceptConnectionAsync(cancellation);
        await using var client = await QuicConnection.ConnectAsync(new()
        {
            RemoteEndPoint = listener.LocalEndPoint,
            DefaultCloseErrorCode = 1,
            DefaultStreamErrorCode = 1,
            ClientAuthenticationOptions = new()
            {
                TargetHost = "RelaxDesk",
                ApplicationProtocols = [protocol],
                RemoteCertificateValidationCallback = (_, peer, _, _) => peer is not null &&
                    CryptographicOperations.FixedTimeEquals(peer.GetCertHash(HashAlgorithmName.SHA256), expectedPin)
            }
        }, cancellation);
        await using var server = await pending;
        var ticket = new SessionTicket(DateTimeOffset.UtcNow.AddSeconds(10));
        var incoming = server.AcceptInboundStreamAsync(cancellation);
        await using var outgoing = await client.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellation);
        var viewer = QuicMediaAttachment.Connect(outgoing, ticket.Value, 1150, cancellation);
        await using var stream = await incoming;
        int hostBudget = await QuicMediaAttachment.Accept(stream, ticket, 1100, cancellation);
        if (hostBudget != 1100 || await viewer != hostBudget)
            throw new IOException("Pinned QUIC attachment negotiated different media budgets");
        Console.WriteLine("PASS one-use media binding over a real certificate-pinned QUIC stream");
    }
}
