using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;

internal static class SessionTerminationTests
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        async Task Expect<T>(Task work, Func<T, bool> valid) where T : Exception
        {
            try { await work; }
            catch (T error) when (valid(error)) { checks++; return; }
            throw new Exception("Wrong session termination result");
        }
        var eof = new EndOfStreamException("media closed");
        var control = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ending = SessionTermination.Wait(control.Task, [Task.FromException(eof)], default);
        if (ending.IsCompleted) throw new Exception("Media EOF canceled pending control reader");
        checks++;
        control.SetException(new RemoteSessionEndedException(SessionEndReason.ReplacedByViewer));
        await Expect<RemoteSessionEndedException>(ending, e => e.Reason == SessionEndReason.ReplacedByViewer);

        await Expect<RemoteSessionEndedException>(SessionTermination.Wait(
            Task.FromException(new RemoteSessionEndedException(SessionEndReason.WindowsLocked)),
            [Task.FromException(eof)], default), e => e.Reason == SessionEndReason.WindowsLocked);
        var silent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Expect<EndOfStreamException>(SessionTermination.Wait(silent.Task,
            [Task.FromException(eof)], default, TimeSpan.FromMilliseconds(10)), e => ReferenceEquals(e, eof));
        var malformed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediaFirst = SessionTermination.Wait(malformed.Task, [Task.FromException(eof)], default);
        malformed.SetException(new InvalidDataException("malformed end notice"));
        await Expect<EndOfStreamException>(mediaFirst, e => ReferenceEquals(e, eof));
        await Expect<InvalidDataException>(SessionTermination.Wait(Task.FromException(new InvalidDataException("malformed end notice")),
            [silent.Task], default), _ => true);
        await Expect<InvalidDataException>(SessionTermination.Wait(silent.Task,
            [Task.FromException(new InvalidDataException("bad AVC"))], default), _ => true);
        using var stop = new CancellationTokenSource();
        var canceled = SessionTermination.Wait(silent.Task, [Task.FromException(eof)], stop.Token);
        stop.Cancel();
        await Expect<OperationCanceledException>(canceled, _ => true);
        return checks;
    }
}
