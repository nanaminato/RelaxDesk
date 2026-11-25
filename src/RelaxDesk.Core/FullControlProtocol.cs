namespace RelaxDesk.Core;

public static class FullControlProtocol
{
    public const string Request = "full-control-request-v3";
    public const string PinnedRequest = "full-control-hello-v3";
    public const string Approved = "full-control-approved-v3";
    public const string DeviceChallenge = "device-challenge-v3";
    public const string DeviceProof = "device-proof-v3";
    public const string UpdateRequired = "client-update-required";

    public static bool IsRequest(string kind) => kind is Request or PinnedRequest;
}
