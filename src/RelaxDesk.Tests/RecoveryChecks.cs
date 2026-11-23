using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class RecoveryChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException($"Recovery assertion {count + 1}"); count++; }
        foreach (Exception error in new Exception[] { new IOException(), new EndOfStreamException(), new SocketException(), new AggregateException(new IOException()), new IOException("control silence timeout", new OperationCanceledException()) }) Check(ConnectionRecovery.IsTransient(error));
        foreach (Exception error in new Exception[] { new AuthenticationException(), new HostConsentRejectedException(), new RemoteSessionEndedException(), new InvalidDataException(), new OperationCanceledException(), new InvalidOperationException(), new AggregateException(new InvalidDataException()), new AuthenticationException("TLS", new IOException()), new IOException("wrapped TLS", new AuthenticationException()) }) Check(!ConnectionRecovery.IsTransient(error));
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            double seconds = Math.Min(8, Math.Pow(2, attempt - 1));
            Check(ConnectionRecovery.Delay(attempt, TimeSpan.Zero, 0) == TimeSpan.FromSeconds(seconds * .8));
            Check(ConnectionRecovery.Delay(attempt, TimeSpan.Zero, 1) == TimeSpan.FromSeconds(seconds * 1.2));
        }
        Check(ConnectionRecovery.Delay(0, TimeSpan.Zero, .5) is null);
        Check(ConnectionRecovery.Delay(6, TimeSpan.Zero, .5) is null);
        Check(ConnectionRecovery.Delay(1, ConnectionRecovery.RecoveryWindow, .5) is null);
        Check(ConnectionRecovery.Delay(1, TimeSpan.FromSeconds(119.5), .5) is null);
        Check(ConnectionRecovery.IsExplicitEnd(new("session-ended-v2", Text: "host-ended")));
        Check(!ConnectionRecovery.IsExplicitEnd(new("pong-v2", 1)));
        foreach (var packet in new[] { new Packet("session-ended-v2"), new Packet("session-ended-v2", Text: "unknown"), new Packet("session-ended-v2", Text: "host-ended", Features: [SessionCapabilities.FileTransfer]), new Packet("session-ended-v2", 1, Text: "host-ended") })
        {
            bool rejected = false; try { ConnectionRecovery.IsExplicitEnd(packet); } catch (InvalidDataException) { rejected = true; }
            Check(rejected);
        }
        return count;
    }
}
