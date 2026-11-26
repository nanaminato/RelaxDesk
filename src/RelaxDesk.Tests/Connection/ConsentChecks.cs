using RelaxDesk.Core;

internal static class ConsentChecks
{
    public static async Task<int> Run()
    {
        int count = 0;
        void Check(bool value) { if (!value) throw new InvalidDataException($"Consent check {count + 1}"); count++; }
        using var first = new ConsentRequest();
        using var second = new ConsentRequest();
        Check(first.Id.Length == 32 && first.Id != second.Id);
        Check(!first.TryDecide(second.Id, ConsentDecision.All));
        Check(!first.Decision.IsCompleted);
        Check(!first.TryDecide(first.Id, (ConsentDecision)99));
        Check(first.TryDecide(first.Id, ConsentDecision.ScreenAndInput));
        Check(await first.Decision == ConsentDecision.ScreenAndInput);
        Check(!first.TryDecide(first.Id, ConsentDecision.All));
        Check(!second.TryDecide(first.Id, ConsentDecision.All));
        Check(!second.Decision.IsCompleted);
        second.Dispose();
        Check(second.Decision.IsCanceled && !second.TryDecide(second.Id, ConsentDecision.All));
        using var denied = new ConsentRequest();
        Check(denied.TryDecide(denied.Id, ConsentDecision.Deny));
        Check(await denied.Decision == ConsentDecision.Deny);
        using var raced = new ConsentRequest();
        int accepted = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() => { if (raced.TryDecide(raced.Id, i % 2 == 0 ? ConsentDecision.All : ConsentDecision.Deny)) Interlocked.Increment(ref accepted); })));
        Check(accepted == 1 && raced.Decision.IsCompletedSuccessfully);
        return count;
    }
}
