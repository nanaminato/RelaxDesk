namespace RelaxDesk.Core;

public enum SessionMode { Guest = 1, Work = 2, Admin = 3 }

public sealed record SessionAccess(SessionMode Mode, bool InputAllowed)
{
    public static bool Available(SessionMode? mode) => mode is null or SessionMode.Guest or SessionMode.Work;

    public static SessionAccess Approve(SessionMode? requested, bool guestInput)
    {
        if (!Available(requested)) throw new InvalidDataException("Session mode is unavailable");
        var mode = requested ?? SessionMode.Work;
        return new(mode, mode == SessionMode.Work || guestInput);
    }
}
