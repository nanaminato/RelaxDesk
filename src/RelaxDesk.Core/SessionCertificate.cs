using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace RelaxDesk.Core;

public static class SessionCertificate
{
    public static X509Certificate2 Create()
    {
        var certificate = CreateWithPkcs12(out var pfx, out _);
        CryptographicOperations.ZeroMemory(pfx);
        return certificate;
    }

    // The caller owns the encrypted PKCS#12 buffer and must clear it after the last listener.
    public static X509Certificate2 CreateWithPkcs12(out byte[] pfx, out string password)
    {
        using var key = RSA.Create(3072);
        var request = new CertificateRequest("CN=RelaxDesk session", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var temporary = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        pfx = temporary.Export(X509ContentType.Pfx, password);
        try { return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet); }
        catch { CryptographicOperations.ZeroMemory(pfx); throw; }
    }
}
