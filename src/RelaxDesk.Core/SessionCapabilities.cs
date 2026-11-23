namespace RelaxDesk.Core;

// Optional v2 fields: older peers omit Features and negotiate no extensions.
// Media codec Capabilities and expiring media binding remain unchanged.
public static class SessionCapabilities
{
    public const string ClipboardText = "clipboard.text";
    public const string ClipboardImage = "clipboard.image";
    public const string ClipboardHtml = "clipboard.html";
    public const string ClipboardFiles = "clipboard.files";
    public const string FileTransfer = "file.transfer";
    public static bool IsClipboard(string feature)=>feature is ClipboardText or ClipboardImage or ClipboardHtml or ClipboardFiles;
    public static bool RichClipboard(IEnumerable<string> features)=>features.Any(feature=>feature is ClipboardImage or ClipboardHtml or ClipboardFiles);
    public static void RevokeClipboard(AccessGate gate){foreach(string feature in new[]{ClipboardText,ClipboardImage,ClipboardHtml,ClipboardFiles})gate.RevokeFeature(feature);}
    public static string ClipboardDescription(IEnumerable<string> features)=>string.Join("、",features.Where(IsClipboard).Select(feature=>feature switch{ClipboardText=>"文本（64KiB）",ClipboardImage=>"图片（8MiB / 1600万像素）",ClipboardHtml=>"HTML（512KiB）",_=>"文件清单（需文件目录授权与传输确认）"}));
    public static string[] Validate(string[]? features)
    {
        if (features is null) return [];
        if (features.Length > 8 || features.Distinct(StringComparer.Ordinal).Count() != features.Length ||
            features.Any(feature => feature is not (ClipboardText or ClipboardImage or ClipboardHtml or ClipboardFiles or FileTransfer))||features.Contains(ClipboardFiles)&&!features.Contains(FileTransfer)) throw new InvalidDataException("Unsupported session capabilities");
        return features.ToArray();
    }
    public static string[] Grant(string[]? requested, bool allowClipboard, bool allowFiles = false) => Validate(requested)
        .Where(feature => IsClipboard(feature) && allowClipboard&&(feature!=ClipboardFiles||allowFiles) || feature == FileTransfer && allowFiles).ToArray();
    public static string[] ValidateGrant(string[]? requested, string[]? granted)
    {
        var result = Validate(granted); var offered = Validate(requested);
        if (result.Except(offered, StringComparer.Ordinal).Any()) throw new InvalidDataException("Host granted an unrequested capability");
        return result;
    }
}
