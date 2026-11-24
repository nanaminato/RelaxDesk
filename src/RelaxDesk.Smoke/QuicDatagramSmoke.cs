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
        using var certificate = pkcs12 ? MakeExportableFixture() : SessionCertificate.Create();
        using var wrong = SessionCertificate.Create();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using (var host = OpenListener(certificate, pkcs12))
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

        using var otherHost = OpenListener(certificate, pkcs12);
        using var wrongViewer = WindowsQuicDatagrams.Connect(new(IPAddress.Loopback, otherHost.Port), wrong);
        try { await wrongViewer.WaitForBudget(deadline.Token); }
        catch (IOException error) when (error.Data["QuicStatus"] is int status && status == unchecked((int)0x8041012A))
        {
            Console.WriteLine("PASS MsQuic DATAGRAM endpoint rejected wrong certificate");
            return;
        }
        throw new IOException("QUIC DATAGRAM authenticated the wrong host certificate");
    }

    static WindowsQuicDatagrams OpenListener(X509Certificate2 certificate, bool pkcs12)
    {
        if (!pkcs12) return WindowsQuicDatagrams.Listen(IPAddress.Loopback, certificate);
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] pfx = certificate.Export(X509ContentType.Pfx, password);
        try { return WindowsQuicDatagrams.ListenWithPkcs12(IPAddress.Loopback, certificate, pfx, password); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    static X509Certificate2 MakeExportableFixture()
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=RelaxDesk QUIC fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        string password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] pfx = temporary.Export(X509ContentType.Pfx, password);
        try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
