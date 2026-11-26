using System.Threading.Channels;
namespace RelaxDesk.Core;

public sealed record SessionFailure(string Worker, string Stage, long CompletedFrames, Exception Error);

// Publish the causal fault before closing queues/cancelling sibling workers.
// A resulting ChannelClosedException must not replace that causal fault.
public sealed class FirstSessionFailure
{
    SessionFailure? first;
    public SessionFailure? First => Volatile.Read(ref first);
    public bool Record(string worker, string stage, long frames, Exception error)
    {
        while (error is ChannelClosedException or AggregateException && error.InnerException is not null) error = error.InnerException;
        return Interlocked.CompareExchange(ref first, new(worker, stage, frames, error), null) is null;
    }
}
