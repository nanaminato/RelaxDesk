namespace RelaxDesk.Core;

// Optional v2 fields: older peers omit Features and negotiate no extensions.
// Media codec Capabilities and expiring media binding remain unchanged.
public static class SessionCapabilities
{
    public const string ClipboardText = "clipboard.text";
    public const string ClipboardImage = "clipboard.image";
    public const string ClipboardHtml = "clipboard.html";
    public const string FileTransfer = "file.transfer";
    public static string[] Validate(string[]? features)
    {
        if (features is null) return [];
        if (features.Length > 8 || features.Distinct(StringComparer.Ordinal).Count() != features.Length ||
            features.Any(feature => feature is not (ClipboardText or ClipboardImage or ClipboardHtml or FileTransfer))) throw new InvalidDataException("Unsupported session capabilities");
        return features.ToArray();
    }
    public static string[] Grant(string[]? requested, bool allowClipboard, bool allowFiles = false) => Validate(requested)
        .Where(feature => feature == ClipboardText && allowClipboard || feature == FileTransfer && allowFiles).ToArray();
    public static string[] ValidateGrant(string[]? requested, string[]? granted)
    {
        var result = Validate(granted); var offered = Validate(requested);
        if (result.Except(offered, StringComparer.Ordinal).Any()) throw new InvalidDataException("Host granted an unrequested capability");
        return result;
    }
}
