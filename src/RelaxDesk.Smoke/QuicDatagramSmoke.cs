using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxDesk.Core;
using RelaxDesk.Windows;

internal static class QuicDatagramSmoke
{
    public static async Task Run(string native, bool pkcs12 = false)
    {
        string library = Path.GetFullPath(native);
        NativeLibrary.SetDllImportResolver(typeof(WindowsQuicDatagrams).Assembly,
            (name, _, _) => name == "RelaxDesk.Native.dll" ? NativeLibrary.Load(library) : 0);
        byte[]? pfx = null, password = null;
        using var certificate = pkcs12
            ? SessionCertificate.CreateWithPkcs12(out pfx, out password)
            : SessionCertificate.Create();
        try { await VerifyDatagrams(certificate, pfx, password); }
        finally
        {
            if (pfx is not null) CryptographicOperations.ZeroMemory(pfx);
            if (password is not null) CryptographicOperations.ZeroMemory(password);
        }
    }

    static async Task VerifyDatagrams(X509Certificate2 certificate, byte[]? pfx, byte[]? password)
    {
        WindowsQuicDatagrams OpenListener() => pfx is null
            ? WindowsQuicDatagrams.Listen(IPAddress.Loopback, certificate)
            : WindowsQuicDatagrams.ListenWithPkcs12(IPAddress.Loopback, certificate, pfx, password!);

        using var wrong = SessionCertificate.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using (var host = OpenListener())
        using (var viewer = WindowsQuicDatagrams.Connect(new(IPAddress.Loopback, host.Port), certificate))
        {
            int budget = Math.Min(await host.WaitForBudget(deadline.Token), await viewer.WaitForBudget(deadline.Token));
            var tx = new DatagramVideoChannel(budget, host.SendAsync, host.ReceiveAsync);
            var rx = new DatagramVideoChannel(budget, viewer.SendAsync, viewer.ReceiveAsync);
            var pixels = Enumerable.Range(0, 8200).Select(i => (byte)i).ToArray();
            var frame = new MediaFrame(MediaCodec.H264, 1, 50, 640, 360, true, true, 0, 0, [1, 2], pixels);
            var receive = rx.Read(deadline.Token);
            await tx.Write(frame, deadline.Token);
            var received = await receive;
            if (received.Sequence != 1 || !received.Data.SequenceEqual(pixels) || !received.Metadata.SequenceEqual(frame.Metadata))
                throw new IOException("QUIC video frame reassembly mismatch");
            byte[] response = [0x52, 0x44, 0x51, 0x02];
            await viewer.SendAsync(response, deadline.Token);
            if (!(await host.ReceiveAsync(deadline.Token)).Span.SequenceEqual(response))
                throw new IOException("QUIC reverse datagram mismatch");
            Console.WriteLine($"PASS encrypted MsQuic DATAGRAM video frame and reverse datagram; bytes={received.Data.Length} datagram_budget={budget}");
        }

        using var otherHost = OpenListener();
        using var wrongViewer = WindowsQuicDatagrams.Connect(new(IPAddress.Loopback, otherHost.Port), wrong);
        try { await wrongViewer.WaitForBudget(deadline.Token); }
        catch (IOException error) when (error.Data["QuicStatus"] is int status && status == unchecked((int)0x8041012A))
        {
            Console.WriteLine("PASS MsQuic DATAGRAM endpoint rejected wrong certificate");
            return;
        }
        throw new IOException("QUIC DATAGRAM authenticated the wrong host certificate");
    }

}
