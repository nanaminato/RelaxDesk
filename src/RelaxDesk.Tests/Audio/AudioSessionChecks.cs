using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using RelaxDesk.Core;

internal static class AudioSessionChecks
{
    internal static int Policies()
    {
        int checks=0;void Check(bool value){if(!value)throw new IOException("Audio grant contract");checks++;}
        var gate=new AccessGate();
        gate.Approve(SessionCapabilities.All, InputProtocol.Version);
        foreach(string feature in SessionCapabilities.All)Check(gate.Allows(feature));
        foreach(string kind in new[]{"audio-state-v2","file-state-v2","clipboard-state-v2"})Check(!gate.AcceptControl(new(kind,1)));
        Check(gate.AcceptControl(new("ping-v2",1)));
        var clock=new AudioPlaybackClock();Check(clock.Decide(0,0)==AudioTiming.Wait);clock.Presented(10_000_000,20_000_000);
        Check(clock.Decide(10_000_000,20_000_000)==AudioTiming.Play);Check(clock.Decide(11_000_000,20_000_000)==AudioTiming.Wait);
        Check(clock.Decide(8_000_000,20_000_000)==AudioTiming.Drop);Check(clock.Decide(11_000_000,21_000_000)==AudioTiming.Play);
        clock.Presented(10_000_000,25_000_000);Check(clock.Decide(15_000_000,25_000_000)==AudioTiming.Play);
        clock.Presented(15_000_000,27_000_000);Check(clock.Decide(15_000_000,27_000_000)==AudioTiming.Play);
        try{clock.Decide(long.MaxValue,27_000_000);throw new Exception("Foreign audio clock accepted");}catch(InvalidDataException){checks++;}
        return checks;
    }
}
