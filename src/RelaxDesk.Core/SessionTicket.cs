using System.Security.Cryptography;
using System.Text;
namespace RelaxDesk.Core;

public sealed class SessionTicket
{
    readonly object sync = new();
    readonly DateTimeOffset expires;
    bool used;
    public string Value { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public SessionTicket(DateTimeOffset expires) => this.expires = expires;
    public bool Consume(string? supplied, DateTimeOffset now)
    {
        lock (sync)
        {
            if (used || now >= expires || supplied is null || supplied.Length != Value.Length) return false;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(Value))) return false;
            used = true; return true;
        }
    }
}
