namespace RelaxDesk.Windows;

// An explicitly armed local transition can preserve the session while video
// and input are paused. It grants no access to the non-Default desktop.
public sealed class DesktopElevationPause(TimeProvider? clock = null)
{
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);
    readonly object sync = new();
    readonly TimeProvider time = clock ?? TimeProvider.System;
    TaskCompletionSource? parked;
    bool autoParked;
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
            if (current.Denial is not (null or "input-desktop-unavailable" or "input-desktop-unsupported"))
                current.EnsureAllowed();
        }
    }

    /// <summary>
    /// Automatically parks capture/input when an unexpected secure desktop (UAC)
    /// appears in ordinary-user mode and resumes once the Default desktop returns,
    /// keeping the remote session connected.
    /// </summary>
    public void CheckOrAutoPause(WindowsSessionSnapshot current)
    {
        lock (sync)
        {
            if (parked is null)
            {
                if (current.TemporaryInputDesktopUnavailable && current.SessionId is { } sid)
                {
                    origin = sid;
                    startedOnPhysicalConsole = current.ConsoleSessionId == sid;
                    started = time.GetTimestamp();
                    parked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    autoParked = true;
                    return;
                }
                current.EnsureAllowed();
                return;
            }
            RequireSameSession(current);
            if (autoParked && current.Denial is null)
            {
                parked = null;
                autoParked = false;
                return;
            }
            if (current.Denial is not (null or "input-desktop-unavailable" or "input-desktop-unsupported"))
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

    public async Task ResumeWhenAvailable(Func<WindowsSessionSnapshot> read, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var current = read();
            Check(current);
            if (current.Denial is null)
            {
                cancellation.ThrowIfCancellationRequested();
                Resume(current);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), time, cancellation);
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
