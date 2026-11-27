using System.Security.Cryptography;
using RelaxDesk.Core;
using RelaxDesk.Mobile;

internal static class DeviceProofTests
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
        }
        void RejectDer(byte[] bytes)
        {
            try { _ = ProviderDeviceKey.ConvertSignature(bytes); }
            catch (CryptographicException) { count++; return; }
            throw new InvalidOperationException("Noncanonical or invalid DER accepted.");
        }

        using var key = new DerProvider(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        string spki = DeviceIdentity.Export(key);
        using var peer = DeviceIdentity.Import(spki);
        Check(DeviceIdentity.Fingerprint(key) == DeviceIdentity.Fingerprint(peer), "Canonical P-256 SPKI");
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        string pin = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        byte[] signature = DeviceIdentity.Sign(key, challenge, pin);
        Check(key.SignCount == 1 && signature.Length == 64, "One raw-transcript signing call, fixed-width signature");
        Check(DeviceIdentity.Verify(peer, challenge, pin, signature), "Shared Host verifier accepts proof");
        var wrongChallenge = (byte[])challenge.Clone();
        wrongChallenge[0] ^= 1;
        Check(!DeviceIdentity.Verify(peer, wrongChallenge, pin, signature), "Proof bound to challenge");
        var wrongPin = Convert.FromHexString(pin);
        wrongPin[0] ^= 1;
        Check(!DeviceIdentity.Verify(peer, challenge, Convert.ToHexString(wrongPin), signature), "Proof bound to certificate");
        using var wrongPeer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Check(!DeviceIdentity.Verify(wrongPeer, challenge, pin, signature), "Proof bound to key");
        try { _ = key.SignHash(new byte[32]); throw new InvalidOperationException("Prehashed signing accepted."); }
        catch (NotSupportedException) { count++; }
        try
        {
            _ = key.SignData(challenge, HashAlgorithmName.SHA384, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            throw new InvalidOperationException("Non-SHA256 signing accepted.");
        }
        catch (NotSupportedException) { count++; }
        try
        {
            _ = key.SignData(challenge, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            throw new InvalidOperationException("DER signing requested through the wire adapter was accepted.");
        }
        catch (NotSupportedException) { count++; }
        Check(key.SignCount == 1, "Unsupported operations never invoke provider");

        byte[] small = [0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x02];
        byte[] padded = new byte[64];
        padded[31] = 1;
        padded[63] = 2;
        Check(ProviderDeviceKey.ConvertSignature(small).SequenceEqual(padded), "Integers are left padded independently");
        byte[] high = [0x30, 0x26, 0x02, 0x21, 0x00, 0x80, .. new byte[31], 0x02, 0x01, 0x01];
        padded[0] = 0x80;
        padded[31] = 0;
        padded[63] = 1;
        Check(ProviderDeviceKey.ConvertSignature(high).SequenceEqual(padded), "Required sign pad is removed");
        RejectDer([.. small, 0]);
        RejectDer([0x30, 0x09, .. small[2..], 0x02, 0x01, 0x03]);
        RejectDer([0x30, 0x81, 0x06, .. small[2..]]);
        RejectDer([0x30, 0x06, 0x02, 0x01, 0x80, .. small[5..]]);
        RejectDer([0x30, 0x06, 0x02, 0x01, 0x00, .. small[5..]]);
        RejectDer([0x30, 0x07, 0x02, 0x02, 0x00, 0x01, .. small[5..]]);
        RejectDer([0x30, 0x26, 0x02, 0x21, 0x01, .. new byte[32], .. small[5..]]);
        RejectDer([0x30, 0x26, 0x02, 0x21, 0x00, .. Convert.FromHexString(
            "FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551"), .. small[5..]]);
        return count;
    }

    // Managed stand-in for a DER-returning provider; intentionally not a
    // substitute for running the Android probe against Android Keystore.
    sealed class DerProvider(ECDsa key) : ProviderDeviceKey(key.ExportSubjectPublicKeyInfo())
    {
        public int SignCount { get; private set; }
        protected override byte[] SignSha256Der(byte[] transcript)
        {
            SignCount++;
            return key.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) key.Dispose();
            base.Dispose(disposing);
        }
    }
}
