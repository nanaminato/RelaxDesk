using System.Security.Cryptography;

namespace RelaxDesk.Windows;

public static class WindowsDeviceKey
{
    // Keep the existing CNG key so previously enrolled fingerprints stay stable.
    const string Name = "RelaxDesk.WorkIdentity.v1";

    public static ECDsa Open()
    {
        var provider = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        using var key = GetKey(provider);
        return new ECDsaCng(key);
    }

    static CngKey GetKey(CngProvider provider)
    {
        if (CngKey.Exists(Name, provider)) return CngKey.Open(Name, provider);
        try
        {
            return CngKey.Create(CngAlgorithm.ECDsaP256, Name, new CngKeyCreationParameters
            {
                Provider = provider,
                KeyUsage = CngKeyUsages.Signing,
                ExportPolicy = CngExportPolicies.None
            });
        }
        catch (CryptographicException) when (CngKey.Exists(Name, provider))
        {
            // Another Viewer instance can create the same user key first.
            return CngKey.Open(Name, provider);
        }
    }
}
