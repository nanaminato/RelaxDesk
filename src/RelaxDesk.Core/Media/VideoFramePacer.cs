namespace RelaxDesk.Core;

public sealed class VideoFramePacer(TimeProvider? time=null)
{
    readonly TimeProvider clock=time??TimeProvider.System;long? last;
    public TimeSpan Delay(int framesPerSecond)
    {
        if(framesPerSecond is <5 or >60)throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if(last is null)return TimeSpan.Zero;
        double remaining=1000d/framesPerSecond-clock.GetElapsedTime(last.Value).TotalMilliseconds;
        return TimeSpan.FromMilliseconds(Math.Max(0,Math.Ceiling(remaining)));
    }
    public void Started()=>last=clock.GetTimestamp();
}
