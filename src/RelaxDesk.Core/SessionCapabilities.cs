namespace RelaxDesk.Core;

// Optional v2 fields: older peers omit Features and negotiate no extensions.
// Media codec Capabilities and expiring media binding remain unchanged.
public static class SessionCapabilities
{
    public const string ClipboardText = "clipboard.text";
    public static string[] Validate(string[]? features)
    {
        if (features is null) return [];
        if (features.Length > 8 || features.Distinct(StringComparer.Ordinal).Count() != features.Length ||
            features.Any(feature => feature != ClipboardText)) throw new InvalidDataException("Unsupported session capabilities");
        return features.ToArray();
    }
    public static string[] Grant(string[]? requested, bool allowClipboard) => Validate(requested)
        .Where(feature => feature == ClipboardText && allowClipboard).ToArray();
    public static string[] ValidateGrant(string[]? requested, string[]? granted)
    {
        var result = Validate(granted); var offered = Validate(requested);
        if (result.Except(offered, StringComparer.Ordinal).Any()) throw new InvalidDataException("Host granted an unrequested capability");
        return result;
    }
}
