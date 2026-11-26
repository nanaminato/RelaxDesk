namespace RelaxDesk.Core;

public enum RecoveryState { Healthy, Recovering, Recovered, Failed }

// Session-lifetime budget: successful frames do not replenish retries.
// Owned by the capture/encode worker; no UI or device dependencies.
public sealed class PipelineRecovery(int maximumAttempts = 3)
{
    readonly int maximumAttempts = maximumAttempts > 0 ? maximumAttempts : throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
    public RecoveryState State { get; private set; } = RecoveryState.Healthy;
    public int Attempts { get; private set; }
    public SessionFailure? First { get; private set; }
    public static bool IsDeviceLoss(Exception error) => error is System.Runtime.InteropServices.ExternalException &&
        error.HResult is unchecked((int)0x887A0026) or unchecked((int)0x887A0005) or unchecked((int)0x887A0007);
    public static bool IsTopologyInitializationRace(Exception error)=>error is System.Runtime.InteropServices.ExternalException&&error.HResult is unchecked((int)0x887A0002) or unchecked((int)0x887A0004);

    public bool TryBegin(string stage, long frames, Exception error)
    {
        First ??= new("capture-encode", stage, frames, error);
        if (State == RecoveryState.Failed || Attempts >= maximumAttempts) { State = RecoveryState.Failed; return false; }
        Attempts++;
        State = RecoveryState.Recovering;
        return true;
    }
    public bool FramePublished()
    {
        if (State != RecoveryState.Recovering) return false;
        State = RecoveryState.Recovered;
        return true;
    }
    public void Fail() => State = RecoveryState.Failed;
}
