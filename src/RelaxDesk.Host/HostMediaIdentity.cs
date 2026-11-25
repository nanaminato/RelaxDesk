using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Host;

internal sealed class HostMediaIdentity : IDisposable
{
    readonly byte[]? pkcs12;
    readonly byte[]? password;

    public HostMediaIdentity(bool persistent = false)
    {
        if (persistent)
        {
            Certificate = OpenPersistentCertificate();
            if (WindowsQuicDatagrams.ReadTlsProvider() == QuicTlsProvider.OpenSsl)
            {
                string secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                password = Encoding.ASCII.GetBytes(secret + '\0');
                pkcs12 = Certificate.Export(X509ContentType.Pfx, secret);
            }
            return;
        }
        Certificate = WindowsQuicDatagrams.ReadTlsProvider() == QuicTlsProvider.OpenSsl
            ? SessionCertificate.CreateWithPkcs12(out pkcs12, out password)
            : SessionCertificate.Create();
    }

    static X509Certificate2 OpenPersistentCertificate()
    {
        const string keyName = "RelaxDesk.PasswordServer.v1";
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        var parameters = new CngKeyCreationParameters
        {
            Provider = provider, KeyUsage = CngKeyUsages.Signing, ExportPolicy = CngExportPolicies.AllowExport
        };
        using var key = CngKey.Exists(keyName, provider)
            ? CngKey.Open(keyName, provider) : CngKey.Create(CngAlgorithm.Rsa, keyName, parameters);
        using var rsa = new RSACng(key);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates)
        {
            using (certificate)
            {
                if (certificate.Subject != "CN=RelaxDesk Password Server" || certificate.NotAfter <= DateTime.Now.AddDays(1))
                    continue;
                using var publicKey = certificate.GetRSAPublicKey();
                if (publicKey is null || !publicKey.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(rsa.ExportSubjectPublicKeyInfo()))
                    continue;
                using var publicCertificate = X509CertificateLoader.LoadCertificate(certificate.RawData);
                return publicCertificate.CopyWithPrivateKey(rsa);
            }
        }
        var request = new CertificateRequest("CN=RelaxDesk Password Server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var current = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(2));
        using var publicOnly = X509CertificateLoader.LoadCertificate(current.RawData);
        store.Add(publicOnly);
        return publicOnly.CopyWithPrivateKey(rsa);
    }

    public X509Certificate2 Certificate { get; }

    public WindowsQuicDatagrams Listen(IPAddress address) => pkcs12 is null
        ? WindowsQuicDatagrams.Listen(address, Certificate)
        : WindowsQuicDatagrams.ListenWithPkcs12(address, Certificate, pkcs12, password!);

    public void Dispose()
    {
        if (pkcs12 is not null) CryptographicOperations.ZeroMemory(pkcs12);
        if (password is not null) CryptographicOperations.ZeroMemory(password);
        Certificate.Dispose();
    }
}
