using System.Globalization;
using System.Text;

namespace RelaxDesk.Core;

// An allowlisted diagnostic snapshot, not an Exception.ToString redactor.
// Never retains messages, Data, Source, stack paths, endpoints or credentials.
public sealed class ConnectionErrorDetails
{
    static readonly HashSet<string> stages = new(StringComparer.Ordinal)
    { "configuration", "connect", "authenticating", "awaiting-approval", "negotiating", "session-setup", "control-read", "control-write", "media-read", "media-close", "media-validate", "cpu-decode", "gpu-decode", "gpu-renderer-init", "gpu-present", "cpu-submit", "viewer-update", "legacy", "unknown" };
    readonly (string Type, int Code, int? NativeCode)[] causes;
    public string Stage { get; }
    public string CorrelationId { get; }
    public DateTimeOffset RecordedUtc { get; }
    public bool CausesTruncated { get; }
    ConnectionErrorDetails(string stage, string correlation, DateTimeOffset recorded, (string, int, int?)[] causes, bool truncated)
    { Stage = stage; CorrelationId = correlation; RecordedUtc = recorded.ToUniversalTime(); this.causes = causes; CausesTruncated = truncated; }
    public static ConnectionErrorDetails Capture(Exception error, string stage, string? correlation, DateTimeOffset? now = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        var causes = new List<(string, int, int?)>(4); Exception? current = error;
        while (current is not null && causes.Count < 4)
        {
            string type = new(current.GetType().Name.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_').Take(96).ToArray());
            causes.Add((type.Length == 0 ? "Exception" : type, current.HResult, current is System.ComponentModel.Win32Exception native ? native.NativeErrorCode : null)); current = current.InnerException;
        }
        string id = correlation is { Length: 32 } && correlation.All(char.IsAsciiHexDigit) ? correlation.ToLowerInvariant() : "none";
        return new(stages.Contains(stage) ? stage : "unknown", id, now ?? DateTimeOffset.UtcNow, causes.ToArray(), current is not null);
    }
    public string Report()
    {
        var text = new StringBuilder(); text.AppendLine("RelaxDesk diagnostic summary");
        text.AppendLine("recorded_utc=" + RecordedUtc.ToString("O", CultureInfo.InvariantCulture));
        text.AppendLine("stage=" + Stage); text.AppendLine("correlation_id=" + CorrelationId);
        for (int i = 0; i < causes.Length; i++)
        {
            text.Append($"cause_{i}_type={causes[i].Type} hresult=0x{causes[i].Code.ToString("X8", CultureInfo.InvariantCulture)}");
            if (causes[i].NativeCode is { } native) text.Append(" native_error=" + native.ToString(CultureInfo.InvariantCulture));
            text.AppendLine();
        }
        if (CausesTruncated) text.AppendLine("causes_truncated=true");
        return text.ToString();
    }
}
