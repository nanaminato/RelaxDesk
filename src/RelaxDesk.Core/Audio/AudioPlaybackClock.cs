namespace RelaxDesk.Core;

public enum AudioTiming { Wait,Play,Drop }

// Correlates the Host's shared QPC timeline with local video submission time.
// This is not a hardware Present/speaker timestamp or physical sync proof.
public sealed class AudioPlaybackClock
{
    readonly object sync=new();bool anchored;long video,local;
    public void Presented(long host100ns,long local100ns)
    {
        if(host100ns<0||local100ns<0)throw new ArgumentOutOfRangeException();
        lock(sync){if(anchored&&host100ns<=video)return;video=host100ns;local=local100ns;anchored=true;}
    }
    public AudioTiming Decide(long audio100ns,long local100ns)
    {
        if(audio100ns<0||local100ns<0)throw new ArgumentOutOfRangeException();
        lock(sync)
        {
            if(!anchored)return AudioTiming.Wait;
            double difference=(double)audio100ns-video-((double)local100ns-local);
            if(difference>50_000_000)throw new InvalidDataException("Audio timeline outside video session");
            return difference< -1_500_000?AudioTiming.Drop:difference>500_000?AudioTiming.Wait:AudioTiming.Play;
        }
    }
}
