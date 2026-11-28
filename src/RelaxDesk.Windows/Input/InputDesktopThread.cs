using System.Collections.Concurrent;

namespace RelaxDesk.Windows;

// Desktop association belongs to an OS thread. Never move a CLR pool thread
// onto Winlogon and return it to unrelated capture, clipboard or UI work.
internal sealed class InputDesktopThread : IDisposable
{
    readonly BlockingCollection<(Action Work, TaskCompletionSource Completion)> commands = new(64);
    readonly Thread thread;
    public InputDesktopThread()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "RelaxDesk input desktop" };
        thread.Start();
    }
    void Run()
    {
        try
        {
            foreach (var command in commands.GetConsumingEnumerable())
            {
                try { command.Work(); command.Completion.SetResult(); }
                catch (Exception error) { command.Completion.SetException(error); }
            }
        }
        finally { WindowsDesktopSwitch.ReleaseThreadDesktop(); }
    }
    public void Invoke(Action work)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        commands.Add((work, completion));
        completion.Task.GetAwaiter().GetResult();
    }
    public void Dispose()
    {
        commands.CompleteAdding();
        thread.Join();
        commands.Dispose();
    }
}
