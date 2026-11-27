using System.Formats.Asn1;
using System.Security.Cryptography;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile;

// Narrow adapter for DeviceIdentity and SessionClient.OpenDeviceKey. The
// provider signs the unhashed transcript once; prehashed signing is forbidden.
internal abstract class ProviderDeviceKey : ECDsa
{
    readonly byte[] publicKey;

    protected ProviderDeviceKey(byte[] subjectPublicKeyInfo)
    {
        using var peer = DeviceIdentity.Import(Convert.ToBase64String(subjectPublicKeyInfo));
        publicKey = peer.ExportSubjectPublicKeyInfo();
        KeySizeValue = 256;
        LegalKeySizesValue = [new KeySizes(256, 256, 0)];
    }

    public override byte[] ExportSubjectPublicKeyInfo() => (byte[])publicKey.Clone();

    public override byte[] SignHash(byte[] hash) => throw new NotSupportedException("Requires the device proof transcript.");

    public override bool VerifyHash(byte[] hash, byte[] signature) =>
        throw new NotSupportedException("Use the imported public key to verify device proofs.");

    protected override byte[] SignDataCore(ReadOnlySpan<byte> data, HashAlgorithmName hashAlgorithm,
        DSASignatureFormat signatureFormat)
    {
        if (hashAlgorithm != HashAlgorithmName.SHA256 ||
            signatureFormat != DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            throw new NotSupportedException("Unsupported device proof algorithm or format.");
        return ConvertSignature(SignSha256Der(data.ToArray()));
    }

    protected abstract byte[] SignSha256Der(byte[] transcript);

    internal static byte[] ConvertSignature(byte[] der)
    {
        if (der.Length is < 8 or > 72)
            throw new CryptographicException("Invalid P-256 DER signature length.");
        try
        {
            var input = new AsnReader(der, AsnEncodingRules.DER);
            var seq = input.ReadSequence();
            byte[] result = new byte[64];
            CopyScalar(seq.ReadIntegerBytes().Span, result.AsSpan(0, 32));
            CopyScalar(seq.ReadIntegerBytes().Span, result.AsSpan(32));
            seq.ThrowIfNotEmpty();
            input.ThrowIfNotEmpty();
            return result;
        }
        catch (AsnContentException e)
        {
            throw new CryptographicException("Invalid P-256 DER signature.", e);
        }
    }

    static readonly byte[] CurveOrder = Convert.FromHexString(
        "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");

    static void CopyScalar(ReadOnlySpan<byte> scalar, Span<byte> output)
    {
        // AsnReader has already required minimal signed INTEGER encodings.
        if ((scalar[0] & 0x80) != 0 || (scalar.Length == 1 && scalar[0] == 0))
            throw new CryptographicException("Invalid P-256 signature scalar.");
        if (scalar[0] == 0) scalar = scalar[1..];
        if (scalar.Length > 32 || (scalar.Length == 32 && scalar.SequenceCompareTo(CurveOrder) >= 0))
            throw new CryptographicException("Invalid P-256 signature scalar.");
        scalar.CopyTo(output[^scalar.Length..]);
    }
}
