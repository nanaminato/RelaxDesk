namespace RelaxDesk.Windows;

public static class KeyboardInjectionFlags
{
    public static uint ForVirtualKey(int key, bool up)
    {
        if (key is < 1 or > 255) throw new ArgumentOutOfRangeException(nameof(key));
        // SendInput requires the extended bit for the navigation cluster and
        // right Ctrl/Alt; omitting it makes remote shortcuts/layouts ambiguous.
        bool extended = key is >= 0x21 and <= 0x28 or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90 or 0xA3 or 0xA5;
        return (up ? 2u : 0u) | (extended ? 1u : 0u);
    }
}
