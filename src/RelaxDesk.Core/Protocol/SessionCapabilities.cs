namespace RelaxDesk.Core;

// Capabilities belong to whole-device approval.
public static class SessionCapabilities
{
    public const string ClipboardText = "clipboard.text";
    public const string ClipboardImage = "clipboard.image";
    public const string ClipboardHtml = "clipboard.html";
    public const string ClipboardFiles = "clipboard.files";
    public const string FileTransfer = "file.transfer";
    public const string SystemAudio="audio.system";
    public static string[] All => [ClipboardText, ClipboardImage, ClipboardHtml, ClipboardFiles, FileTransfer, SystemAudio];
    public static string[] RequireAll(string[]? features)
    {
        var result=Validate(features);
        if(result.Length!=All.Length||All.Except(result,StringComparer.Ordinal).Any())throw new InvalidDataException("A current session requires every desktop capability");
        return All;
    }
    public static bool IsClipboard(string feature)=>feature is ClipboardText or ClipboardImage or ClipboardHtml or ClipboardFiles;
    public static bool RichClipboard(IEnumerable<string> features)=>features.Any(feature=>feature is ClipboardImage or ClipboardHtml or ClipboardFiles);
    public static void RevokeClipboard(AccessGate gate){foreach(string feature in new[]{ClipboardText,ClipboardImage,ClipboardHtml,ClipboardFiles})gate.RevokeFeature(feature);}
    public static string[] Validate(string[]? features)
    {
        if (features is null) return [];
        if (features.Length > 8 || features.Distinct(StringComparer.Ordinal).Count() != features.Length ||
            features.Any(feature => feature is not (ClipboardText or ClipboardImage or ClipboardHtml or ClipboardFiles or FileTransfer or SystemAudio))||features.Contains(ClipboardFiles)&&!features.Contains(FileTransfer)) throw new InvalidDataException("Unsupported session capabilities");
        return features.ToArray();
    }
    public static string[] ValidateGrant(string[]? requested, string[]? granted)
    {
        var result = Validate(granted); var offered = Validate(requested);
        if (result.Except(offered, StringComparer.Ordinal).Any()) throw new InvalidDataException("Host granted an unrequested capability");
        return result;
    }
}
