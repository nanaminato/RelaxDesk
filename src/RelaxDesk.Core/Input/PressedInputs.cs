namespace RelaxDesk.Core;

public sealed class PressedInputs
{
    readonly List<int> held = [];
    public void Update(int code, bool up)
    {
        if (code is <= 0 or >= 258) return;
        if (up) held.Remove(code); else if (!held.Contains(code)) held.Add(code);
    }
    public int[] ReleaseAll()
    {
        int[] result = held.AsEnumerable().Reverse().ToArray(); held.Clear(); return result;
    }
    public int[] ReleaseButtons()
    {
        int[] result = held.Where(code => code is 256 or 257).Reverse().ToArray();
        foreach (int code in result) held.Remove(code); return result;
    }
}
