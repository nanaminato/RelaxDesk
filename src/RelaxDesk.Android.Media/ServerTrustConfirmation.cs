namespace RelaxDesk.Mobile.Media;

public static class ServerTrustConfirmation
{
    // Certificate trust is independent of later password/approval/media success.
    // Persist before returning true, so a storage failure cannot start login.
    public static async Task<bool> Confirm(string pin, Func<string, CancellationToken, Task<bool>> ask,
        Action<string>? remember, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await ask(pin, ct).ConfigureAwait(false)) return false;
        ct.ThrowIfCancellationRequested();
        remember?.Invoke(pin);
        return true;
    }
}
