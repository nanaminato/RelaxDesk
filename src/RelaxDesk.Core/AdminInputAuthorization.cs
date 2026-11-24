namespace RelaxDesk.Core;

// Owned by one approved control connection. A request is single-use and does
// not authorize input until a separate local decision and agent startup.
public sealed class AdminInputAuthorization(SessionMode baseMode, TimeProvider? clock = null)
{
    static readonly TimeSpan ApprovalWindow = TimeSpan.FromSeconds(60);
    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object sync = new();
    Guid? pending;
    Guid? active;
    long requestedAt;
    bool ended;

    public Guid? Request()
    {
        lock (sync)
        {
            if (baseMode != SessionMode.Work || ended || active is not null) return null;
            if (pending is not null && time.GetElapsedTime(requestedAt) < ApprovalWindow) return null;
            requestedAt = time.GetTimestamp();
            return pending = Guid.NewGuid();
        }
    }

    public bool Decide(Guid request, bool locallyApproved, bool agentReady)
    {
        lock (sync)
        {
            if (ended || pending != request) return false;
            pending = null;
            if (!locallyApproved || !agentReady || time.GetElapsedTime(requestedAt) >= ApprovalWindow) return false;
            active = request;
            return true;
        }
    }

    public bool TryExecute(Guid grant, Action input)
    {
        lock (sync)
        {
            if (ended || active != grant) return false;
            input();
            return true;
        }
    }

    public void Revoke()
    {
        lock (sync) pending = active = null;
    }

    public void End()
    {
        lock (sync)
        {
            ended = true;
            pending = active = null;
        }
    }
}
