using System.Globalization;
using System.Security;
using RelaxDesk.Core;

namespace RelaxDesk.Session;

public sealed record AdminInputLaunchRequest
{
    public AdminInputLaunchRequest(Guid grant, int coordinatorPid, string owner, string display)
    {
        if (grant == Guid.Empty || coordinatorPid <= 0 || !AdminInputChannel.ValidDisplay(display))
            throw new ArgumentException("Invalid administrator input launch request");
        Grant = grant;
        CoordinatorPid = coordinatorPid;
        Owner = LocalAgentPipe.CanonicalUser(owner);
        Display = display;
    }

    public Guid Grant { get; }
    public int CoordinatorPid { get; }
    public string Owner { get; }
    public string Display { get; }

    public string[] Arguments() => [Grant.ToString("N"), CoordinatorPid.ToString(CultureInfo.InvariantCulture), Owner, Display];

    public static AdminInputLaunchRequest? Parse(string[] args)
    {
        if (args is not [var grant, var pid, var owner, var display] ||
            !Guid.TryParseExact(grant, "N", out Guid parsedGrant) ||
            !int.TryParse(pid, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPid)) return null;
        try
        {
            var request = new AdminInputLaunchRequest(parsedGrant, parsedPid, owner, display);
            return request.Arguments().SequenceEqual(args, StringComparer.Ordinal) ? request : null;
        }
        catch (Exception error) when (error is ArgumentException or SecurityException) { return null; }
    }
}
