namespace RelaxDesk.Core;

public static class FullControlProtocol
{
    public const string PasswordRequest = "password-login-v5";
    public const string PasswordRejected = "password-rejected-v5";
    public const string Request = "full-control-request-v5";
    public const string PinnedRequest = "full-control-hello-v5";
    public const string Approved = "full-control-approved-v5";
    public const string InputUnavailable = "full-control-input-unavailable-v5";
    public const string DeviceChallenge = "device-challenge-v5";
    public const string DeviceProof = "device-proof-v5";

    public static void RequireCurrent(Packet packet)
    {
        SessionCapabilities.RequireAll(packet.Features);
        if(packet.InputVersion!=InputProtocol.Version||packet.DisplayVersion!=DisplayProtocol.TopologyVersion||packet.VideoVersion!=VideoSettingsProtocol.Version)
            throw new InvalidDataException("Unsupported session profile; update both endpoints");
    }
    public static bool IsRequest(string kind) => kind is Request or PinnedRequest;
}
