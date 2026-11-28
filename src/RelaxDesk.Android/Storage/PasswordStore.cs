using System.Security.Cryptography;
using System.Text;
using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace RelaxDesk.Mobile;

/// <summary>
/// Stores per-host passwords in private SharedPreferences.
/// Prefers Android Keystore AES-256-GCM encryption; if Keystore encryption is unavailable,
/// callers must explicitly ask the user whether to store in plaintext or not store at all.
/// </summary>
internal static class PasswordStore
{
    const string PrefsName = "relaxdesk_creds_v1";
    const string KeyAlias = "relaxdesk.viewer.cred.aes256.v1";
    const string Provider = "AndroidKeyStore";
    const string Transformation = "AES/GCM/NoPadding";
    const string PrefixEncrypted = "enc1:";
    const string PrefixPlain = "plain:";
    static readonly object KeyLock = new();

    static string KeyFor(string host, int port, string user) =>
        $"{host.Trim().ToLowerInvariant()}:{port}:{user.Trim()}";

    public static bool TrySaveEncrypted(Context context, string host, int port, string user, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            Clear(context, host, port, user);
            return true;
        }
        try
        {
            var secretKey = GetOrCreateSecretKey();
            using var cipher = Cipher.GetInstance(Transformation)!;
            cipher.Init(Javax.Crypto.CipherMode.EncryptMode, secretKey);
            byte[] iv = cipher.GetIV() ?? throw new CryptographicException("Missing GCM IV");
            byte[] cipherBytes = cipher.DoFinal(Encoding.UTF8.GetBytes(password))
                                 ?? throw new CryptographicException("Encryption returned null");

            byte[] combined = new byte[1 + iv.Length + cipherBytes.Length];
            combined[0] = (byte)iv.Length;
            Buffer.BlockCopy(iv, 0, combined, 1, iv.Length);
            Buffer.BlockCopy(cipherBytes, 0, combined, 1 + iv.Length, cipherBytes.Length);

            WriteRaw(context, KeyFor(host, port, user), PrefixEncrypted + Convert.ToBase64String(combined));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void SavePlaintext(Context context, string host, int port, string user, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            Clear(context, host, port, user);
            return;
        }
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
        WriteRaw(context, KeyFor(host, port, user), PrefixPlain + encoded);
    }

    public static string? Load(Context context, string host, int port, string user)
    {
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        string? raw = prefs?.GetString(KeyFor(host, port, user), null);
        if (string.IsNullOrEmpty(raw)) return null;

        try
        {
            if (raw.StartsWith(PrefixPlain, StringComparison.Ordinal))
            {
                byte[] bytes = Convert.FromBase64String(raw[PrefixPlain.Length..]);
                return Encoding.UTF8.GetString(bytes);
            }
            if (raw.StartsWith(PrefixEncrypted, StringComparison.Ordinal))
            {
                byte[] combined = Convert.FromBase64String(raw[PrefixEncrypted.Length..]);
                if (combined.Length < 14) return null;
                int ivLen = combined[0];
                if (ivLen < 12 || 1 + ivLen >= combined.Length) return null;

                var secretKey = GetOrCreateSecretKey();
                using var cipher = Cipher.GetInstance(Transformation)!;
                using var spec = new GCMParameterSpec(128, combined, 1, ivLen);
                cipher.Init(Javax.Crypto.CipherMode.DecryptMode, secretKey, spec);
                byte[] plain = cipher.DoFinal(combined, 1 + ivLen, combined.Length - 1 - ivLen)
                               ?? throw new CryptographicException("Decryption returned null");
                return Encoding.UTF8.GetString(plain);
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    public static void Clear(Context context, string host, int port, string user)
    {
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        using var editor = prefs?.Edit();
        editor?.Remove(KeyFor(host, port, user));
        editor?.Apply();
    }

    static void WriteRaw(Context context, string key, string value)
    {
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        using var editor = prefs?.Edit();
        editor?.PutString(key, value);
        editor?.Apply();
    }

    static ISecretKey GetOrCreateSecretKey()
    {
        lock (KeyLock)
        {
            using var store = KeyStore.GetInstance(Provider)!;
            store.Load(null);
            if (!store.ContainsAlias(KeyAlias))
            {
                using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, Provider)!;
                using var spec = new KeyGenParameterSpec.Builder(KeyAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                    .SetBlockModes(KeyProperties.BlockModeGcm)!
                    .SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)!
                    .SetKeySize(256)!
                    .Build();
                generator.Init(spec);
                using var created = generator.GenerateKey();
            }

            using var entry = store.GetEntry(KeyAlias, null);
            if (entry is not KeyStore.SecretKeyEntry secretEntry || secretEntry.SecretKey is null)
                throw new CryptographicException("Android Keystore AES key unavailable.");
            return secretEntry.SecretKey;
        }
    }
}
