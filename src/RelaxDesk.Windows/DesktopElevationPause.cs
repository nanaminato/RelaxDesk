namespace RelaxDesk.Windows;

// An explicitly armed local transition can preserve the session while video
// and input are paused. It grants no access to the non-Default desktop.
public sealed class DesktopElevationPause(TimeProvider? clock = null)
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    readonly object sync = new();
    readonly TimeProvider time = clock ?? TimeProvider.System;
    TaskCompletionSource? parked;
    uint origin;
    bool startedOnPhysicalConsole;
    long started;

    public bool IsPaused { get { lock (sync) return parked is not null; } }

    public Task Begin(WindowsSessionSnapshot current)
    {
        current.EnsureAllowed();
        lock (sync)
        {
            if (parked is not null) throw new InvalidOperationException("Desktop elevation already pending");
            origin = current.SessionId!.Value;
            startedOnPhysicalConsole = current.ConsoleSessionId == origin;
            started = time.GetTimestamp();
            parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return parked.Task;
        }
    }

    public void CaptureParked()
    {
        lock (sync) parked?.TrySetResult();
    }

    public void Check(WindowsSessionSnapshot current)
    {
        lock (sync)
        {
            if (parked is null) { current.EnsureAllowed(); return; }
            RequireSameSession(current);
            if (current.Denial is not (null or "input-desktop-unavailable"))
                current.EnsureAllowed();
        }
    }

    public void Resume(WindowsSessionSnapshot current)
    {
        lock (sync)
        {
            RequireSameSession(current);
            current.EnsureAllowed();
            if (parked?.Task.IsCompletedSuccessfully != true)
                throw new InvalidOperationException("Capture must acknowledge the desktop pause");
            parked = null;
        }
    }

    void RequireSameSession(WindowsSessionSnapshot snapshot)
    {
        if (snapshot.SessionId is null)
            throw new WindowsSessionUnavailableException("session-query-failed");
        if (snapshot.SessionId != origin || startedOnPhysicalConsole && snapshot.ConsoleSessionId != origin)
            throw new WindowsSessionUnavailableException("physical-console-session-changed");
        if (time.GetElapsedTime(started) >= Limit)
            throw new WindowsSessionUnavailableException("expected-desktop-transition-expired");
    }
}
