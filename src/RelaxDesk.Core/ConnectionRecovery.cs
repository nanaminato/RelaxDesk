using System.Net.Sockets;
using System.Security.Authentication;
namespace RelaxDesk.Core;

public enum ConnectionPhase { Idle, Connecting, Authenticating, AwaitingApproval, Negotiating, Connected, Reconnecting, Disconnected, Failed }
public sealed class HostConsentRejectedException() : AuthenticationException("Host declined this connection");
public enum SessionEndReason { WindowsSessionUnavailable = 1, WindowsLocked = 2, WindowsSessionChanged = 3, WindowsDesktopUnavailable = 4 }
public sealed class RemoteSessionEndedException(SessionEndReason? reason = null) : IOException("Host explicitly ended this session")
{
    public SessionEndReason? Reason { get; } = reason;
}

public static class ConnectionRecovery
{
    public const int MaximumRetries = 5;
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(120);
    public static bool IsTransient(Exception error)
    {
        while (error is AggregateException aggregate && aggregate.InnerExceptions.Count == 1) error = aggregate.InnerExceptions[0];
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
            if (cause is AuthenticationException or InvalidDataException or RemoteSessionEndedException) return false;
        return error is IOException or SocketException;
    }
    // retry is one-based. The total outage budget includes consent/negotiation.
    public static TimeSpan? Delay(int retry, TimeSpan elapsed, double jitter)
    {
        if (jitter is < 0 or > 1 || !double.IsFinite(jitter) || elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(jitter));
        if (retry < 1 || retry > MaximumRetries || elapsed >= RecoveryWindow) return null;
        var delay = TimeSpan.FromSeconds(Math.Min(8, 1 << (retry - 1)) * (.8 + .4 * jitter));
        return elapsed + delay < RecoveryWindow ? delay : null;
    }
    public static bool IsExplicitEnd(Packet packet)
    {
        if (packet.Kind != "session-ended-v2") return false;
        if (packet.EndReason is { } reason && !Enum.IsDefined(reason) ||
            packet != new Packet("session-ended-v2", Text: "host-ended", EndReason: packet.EndReason))
            throw new InvalidDataException("Malformed session end response");
        return true;
    }
}
