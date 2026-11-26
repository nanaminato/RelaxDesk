namespace RelaxDesk.Core;

public enum ConsentDecision { Deny, Allow }

// Fresh one-shot local approval. This is not a remote credential: its identifier
// prevents delayed UI/IPC actions from approving a different connection.
public sealed class ConsentRequest : IDisposable
{
    readonly object sync = new();
    readonly TaskCompletionSource<ConsentDecision> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool closed;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public Task<ConsentDecision> Decision => result.Task;
    public bool TryDecide(string id, ConsentDecision decision)
    {
        lock (sync)
        {
            if (closed || !Enum.IsDefined(decision) || !string.Equals(Id, id, StringComparison.Ordinal)) return false;
            closed = true;
            return result.TrySetResult(decision);
        }
    }
    public void Dispose()
    {
        lock (sync) { closed = true; result.TrySetCanceled(); }
    }
}
