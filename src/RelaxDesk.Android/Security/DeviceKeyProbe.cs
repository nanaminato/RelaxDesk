using System.Security.Cryptography;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile;

internal static class DeviceKeyProbe
{
    internal static void Run()
    {
        string identity;
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] certificateFingerprint = RandomNumberGenerator.GetBytes(32);
        string pin = Convert.ToHexString(certificateFingerprint);
        byte[] proof;
        using (var key = AndroidDeviceKey.OpenProbeKey())
        {
            identity = DeviceIdentity.Export(key);
            proof = DeviceIdentity.Sign(key, challenge, pin);
        }

        using var reopened = AndroidDeviceKey.OpenProbeKey();
        using var verifier = DeviceIdentity.Import(DeviceIdentity.Export(reopened));
        if (identity != DeviceIdentity.Export(verifier) ||
            !DeviceIdentity.Verify(verifier, challenge, pin, proof) ||
            !DeviceIdentity.Verify(verifier, challenge, pin, DeviceIdentity.Sign(reopened, challenge, pin)))
            throw new CryptographicException("The reopened device key did not verify both proofs.");

        certificateFingerprint[0] ^= 1;
        if (DeviceIdentity.Verify(verifier, challenge, Convert.ToHexString(certificateFingerprint), proof))
            throw new CryptographicException("The proof was not bound to the certificate.");
    }
}
