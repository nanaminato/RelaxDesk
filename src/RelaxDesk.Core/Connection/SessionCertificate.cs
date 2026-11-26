using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
namespace RelaxDesk.Core;

public static class SessionCertificate
{
    public static X509Certificate2 Create()
    {
        var certificate = CreateWithPkcs12(out var pfx, out var password);
        CryptographicOperations.ZeroMemory(pfx);
        CryptographicOperations.ZeroMemory(password);
        return certificate;
    }

    // The caller owns both buffers and clears them after the last listener.
    public static X509Certificate2 CreateWithPkcs12(out byte[] pfx, out byte[] password)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=RelaxDesk session", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        string exportPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        password = new byte[exportPassword.Length + 1];
        Encoding.ASCII.GetBytes(exportPassword, password);
        try
        {
            pfx = temporary.Export(X509ContentType.Pfx, exportPassword);
            try { return X509CertificateLoader.LoadPkcs12(pfx, exportPassword, X509KeyStorageFlags.UserKeySet); }
            catch { CryptographicOperations.ZeroMemory(pfx); throw; }
        }
        catch { CryptographicOperations.ZeroMemory(password); throw; }
    }
}
