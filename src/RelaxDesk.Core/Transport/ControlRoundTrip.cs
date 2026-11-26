namespace RelaxDesk.Core;

public sealed class ControlRoundTrip(TimeProvider? time=null)
{
    readonly TimeProvider clock=time??TimeProvider.System;readonly object sync=new();
    readonly Dictionary<long,long> sent=new();long newest;
    public const int Capacity=4;
    public void Sent(long sequence)
    {
        if(sequence<=0)throw new ArgumentOutOfRangeException(nameof(sequence));
        lock(sync)
        {
            if(sequence<=newest)throw new InvalidDataException("RTT sequence regressed");newest=sequence;
            if(sent.Count==Capacity)sent.Remove(sent.Keys.Min());sent.Add(sequence,clock.GetTimestamp());
        }
    }
    public double? Reply(long sequence)
    {lock(sync){if(!sent.Remove(sequence,out long start))return null;return Math.Max(0,clock.GetElapsedTime(start).TotalMilliseconds);}}
}
