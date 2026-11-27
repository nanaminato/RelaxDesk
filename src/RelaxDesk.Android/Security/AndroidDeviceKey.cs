using System.Security.Cryptography;
using Android.Security.Keystore;
using Java.Security;
using Java.Security.Spec;

namespace RelaxDesk.Mobile;

internal sealed class AndroidDeviceKey : ProviderDeviceKey
{
    const string Provider = "AndroidKeyStore";
    // Reserved for the offline probe. Production pairing/key-reset policy is
    // deliberately not coupled to this alias; never replace an existing key here.
    const string Alias = "relaxdesk.viewer.probe.device-p256.v1";
    static readonly object KeyStoreGate = new();
    IPrivateKey? privateKey;

    AndroidDeviceKey(byte[] publicKey, IPrivateKey privateKey) : base(publicKey) => this.privateKey = privateKey;

    internal static AndroidDeviceKey OpenProbeKey()
    {
        lock (KeyStoreGate)
        {
            using var store = KeyStore.GetInstance(Provider)!;
            store.Load(null);
            if (!store.ContainsAlias(Alias))
            {
                using var generator = KeyPairGenerator.GetInstance(KeyProperties.KeyAlgorithmEc, Provider)!;
                using var curve = new ECGenParameterSpec("secp256r1");
                using var builder = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Sign);
                builder.SetAlgorithmParameterSpec(curve);
                builder.SetDigests(KeyProperties.DigestSha256);
                using var spec = builder.Build();
                generator.Initialize(spec);
                using var generated = generator.GenerateKeyPair();
            }

            using var cert = store.GetCertificate(Alias);
            using var peer = cert?.PublicKey;
            byte[] spki = peer?.GetEncoded() ?? throw new CryptographicException("Device public key is unavailable.");
            var key = store.GetKey(Alias, null);
            if (key is not IPrivateKey signingKey)
            {
                key?.Dispose();
                throw new CryptographicException("Device signing key is unavailable.");
            }
            try
            {
                // Do not attempt a private-key export, or silently substitute
                // a software-managed key if this alias fails.
                return new AndroidDeviceKey(spki, signingKey);
            }
            catch { signingKey.Dispose(); throw; }
        }
    }

    protected override byte[] SignSha256Der(byte[] transcript)
    {
        var key = privateKey ?? throw new ObjectDisposedException(nameof(AndroidDeviceKey));
        using var signature = Signature.GetInstance("SHA256withECDSA")!;
        signature.InitSign(key);
        signature.Update(transcript);
        return signature.Sign() ?? throw new CryptographicException("Device proof signing failed.");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            privateKey?.Dispose();
            privateKey = null;
        }
        base.Dispose(disposing);
    }
}
