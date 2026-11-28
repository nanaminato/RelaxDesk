using RelaxDesk.Mobile.Media;

internal static class ServerTrustConfirmationTests
{
    internal static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool pass) { if (!pass) throw new Exception("Certificate trust regression"); checks++; }
        string? saved = null;
        string pin = new('A', 64);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirming = ServerTrustConfirmation.Confirm(pin, (_, _) => decision.Task, value => saved = value, default);
        Check(saved is null && !confirming.IsCompleted);
        decision.SetResult(true);
        Check(await confirming && saved == pin); // Already stored before any login/media attempt.
        Check(!await ServerTrustConfirmation.Confirm(new('B', 64), (_, _) => Task.FromResult(false), value => saved = value, default));
        Check(saved == pin); // A declined certificate cannot replace known trust.
        using var canceled = new CancellationTokenSource();
        try
        {
            await ServerTrustConfirmation.Confirm(new('C', 64), (_, _) =>
            { canceled.Cancel(); return Task.FromResult(true); }, value => saved = value, canceled.Token);
            throw new Exception("Canceled confirmation persisted");
        }
        catch (OperationCanceledException) { Check(saved == pin); }
        try
        {
            await ServerTrustConfirmation.Confirm(pin, (_, _) => Task.FromResult(true), _ => throw new IOException("store unavailable"), default);
            throw new Exception("Storage failure allowed login");
        }
        catch (IOException) { checks++; }
        return checks;
    }
}
