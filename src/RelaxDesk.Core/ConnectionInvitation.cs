using System.Net;
namespace RelaxDesk.Core;

// One-session secret. Never log or persist the complete invitation.
public sealed record ConnectionInvitation(string Host, int Port, string Fingerprint, string Token)
{
    static bool Hex(string value, int length) => value.Length == length && value.All(c => char.IsAsciiHexDigit(c));
    public static ConnectionInvitation Parse(string text)
    {
        if (text.Length > 1024 || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "relaxdesk" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/"
            || !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || uri.Port is < 1 or > 65535)
            throw new FormatException("连接信息格式不正确，请粘贴 Host 本次显示的完整 relaxdesk:// 连接信息。");
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string item in uri.Query.TrimStart('?').Split('&'))
        {
            var pair = item.Split('=', 2);
            if (pair.Length != 2 || !fields.TryAdd(pair[0], pair[1])) throw new FormatException("连接信息有重复或缺失字段。");
        }
        if (fields.Count != 2 || !fields.TryGetValue("fingerprint", out var pin) || !fields.TryGetValue("token", out var token) || !Hex(pin, 64) || !Hex(token, 32))
            throw new FormatException("连接信息需要有效的本次指纹和一次性凭证。");
        return new(address.ToString(), uri.Port, pin.ToUpperInvariant(), token.ToUpperInvariant());
    }
    public string Encode()
    {
        var builder = new UriBuilder("relaxdesk", Host, Port) { Path = "/", Query = $"fingerprint={Fingerprint}&token={Token}" };
        string value = builder.Uri.AbsoluteUri; _ = Parse(value); return value;
    }
    public override string ToString() => $"{Host}:{Port} (credentials hidden)";
}
