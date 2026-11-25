using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class ElevatedInputWorker
{
    // Call only after the local IPC endpoint has authenticated its peer.
    public static async Task Run(Stream channel, Guid grant, Func<WindowsDisplaySelection> display,
        CancellationToken revocation)
    {
        WindowsInputPrivilege.EnsureElevatedProcess();
        WindowsSessionAccess.EnsureAllowed();
        var selection = display();
        using var input = new WindowsInputSession(() => selection);
        await WhileSessionAvailable(revocation, async lifetime =>
        {
            using (var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime))
            {
                readyDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                await Wire.Write(channel, new(AdminInputChannel.Ready, Origin: grant.ToString("N")), readyDeadline.Token);
            }
            await AdminInputChannel.Run(channel, grant, input.Inject, input.ReleaseAll, lifetime, device =>
            {
                var next = selection.Select(device);
                next.EnsureCurrent();
                selection = next;
            });
        }, lifetime => WindowsSessionAccess.Watch(lifetime));
    }

    internal static async Task WhileSessionAvailable(CancellationToken revocation,
        Func<CancellationToken, Task> commands, Func<CancellationToken, Task> watch)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(revocation);
        Task eligibility = watch(lifetime.Token);
        if (eligibility.IsCompleted) { await eligibility; return; }
        Task input;
        try { input = commands(lifetime.Token); }
        catch
        {
            lifetime.Cancel();
            try { await eligibility; } catch (Exception) { }
            throw;
        }
        Task first = await Task.WhenAny(input, eligibility);
        try { await first; }
        finally
        {
            lifetime.Cancel();
            try { await (first == input ? eligibility : input); }
            catch (Exception) { /* The first completion determines the outcome. */ }
        }
    }
}
