using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace RelaxDesk.Core;

public sealed record PasswordVerifier(string UserName, byte[] Salt, byte[] Hash)
{
    const int Iterations = 600_000;

    public static bool ValidUserName(string? name) =>
        name is { Length: > 0 and <= 64 } && name == name.Trim() && !name.Any(char.IsControl);

    public static byte[] EncodePassword(string password)
    {
        if (password.Length is < 12 or > 128) throw new ArgumentException("密码长度须为 12 至 128 个字符。");
        return Encoding.UTF8.GetBytes(password);
    }

    public static PasswordVerifier Create(string name, string password)
    {
        if (!ValidUserName(name)) throw new ArgumentException("请输入 1 至 64 个字符的用户名。");
        byte[] bytes = EncodePassword(password);
        try
        {
            byte[] salt = RandomNumberGenerator.GetBytes(32);
            return new(name, salt, Derive(bytes, salt));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public bool IsValid => ValidUserName(UserName) && Salt is { Length: 32 } && Hash is { Length: 32 };

    public bool Verify(string? name, byte[]? password)
    {
        if (!IsValid || password is not { Length: >= 12 and <= 512 }) return false;
        byte[] candidate = Derive(password, Salt);
        try { return CryptographicOperations.FixedTimeEquals(candidate, Hash) && UserName == name; }
        finally { CryptographicOperations.ZeroMemory(candidate); }
    }

    static byte[] Derive(byte[] password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public override string ToString() => "PasswordVerifier [redacted]";
}

public sealed class PasswordLoginRejectedException() : AuthenticationException("Username or password was rejected");
