using System.Security.Cryptography;

namespace RelaxDesk.Core;

// Device identity is proof of possession, separate from local permission
// to share. The signature is restricted to this TLS server certificate and
// a fresh challenge for the current connection.
public static class WorkDeviceIdentity
{
    const string P256 = "1.2.840.10045.3.1.7";

    public static string Export(ECDsa key) => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    public static ECDsa Import(string publicKey)
    {
        if (publicKey.Length is < 1 or > 160) throw new CryptographicException("Invalid Work device public key");
        byte[] bytes = Convert.FromBase64String(publicKey);
        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(bytes, out int consumed);
            if (consumed != bytes.Length || key.ExportParameters(false).Curve.Oid.Value != P256 ||
                !key.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(bytes) || Export(key) != publicKey)
                throw new CryptographicException("Invalid Work device public key");
            return key;
        }
        catch { key.Dispose(); throw; }
    }

    public static string Fingerprint(ECDsa key) => Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));

    public static byte[] Sign(ECDsa key, byte[] challenge, string tlsFingerprint) =>
        key.SignData(Transcript(challenge, tlsFingerprint), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool Verify(ECDsa key, byte[] challenge, string tlsFingerprint, byte[] signature) =>
        signature.Length == 64 && key.VerifyData(Transcript(challenge, tlsFingerprint), signature,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    static byte[] Transcript(byte[] challenge, string tlsFingerprint)
    {
        if (challenge.Length != 32 || tlsFingerprint.Length != 64 || !tlsFingerprint.All(char.IsAsciiHexDigit))
            throw new CryptographicException("Invalid Work device challenge");
        ReadOnlySpan<byte> domain = "RelaxDesk Work device proof v1\0"u8;
        byte[] bytes = new byte[domain.Length + 64];
        domain.CopyTo(bytes);
        challenge.CopyTo(bytes, domain.Length);
        Convert.FromHexString(tlsFingerprint).CopyTo(bytes, domain.Length + 32);
        return bytes;
    }
}
