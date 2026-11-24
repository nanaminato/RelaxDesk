using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

internal sealed class HostMediaIdentity : IDisposable
{
    readonly byte[]? pkcs12;
    readonly string? password;

    public HostMediaIdentity()
    {
        Certificate = WindowsQuicDatagrams.UsesOpenSsl()
            ? SessionCertificate.CreateWithPkcs12(out pkcs12, out password)
            : SessionCertificate.Create();
    }

    public X509Certificate2 Certificate { get; }

    public WindowsQuicDatagrams Listen(IPAddress address) => pkcs12 is null
        ? WindowsQuicDatagrams.Listen(address, Certificate)
        : WindowsQuicDatagrams.ListenWithPkcs12(address, Certificate, pkcs12, password!);

    public void Dispose()
    {
        if (pkcs12 is not null) CryptographicOperations.ZeroMemory(pkcs12);
        Certificate.Dispose();
    }
}
