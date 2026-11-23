namespace RelaxDesk.Core;

// A failed query is not evidence that no screen exists. It may pause output
// briefly; persistent failure must end the attempt rather than fabricate data.
public sealed class DisplayMetadataRecovery
{
    TimeSpan? firstFailure;
    public bool Failure(TimeSpan elapsed)
    {firstFailure??=elapsed;if(elapsed<firstFailure.Value)throw new InvalidDataException("Metadata clock moved backwards");return elapsed-firstFailure.Value<=TimeSpan.FromSeconds(3);}
    public void Success()=>firstFailure=null;
}
