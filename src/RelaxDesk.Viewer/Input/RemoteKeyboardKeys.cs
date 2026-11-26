namespace RelaxDesk.Viewer;

internal static class RemoteKeyboardKeys
{
    public static int Resolve(int key, nint flags)
    {
        bool extended = ((long)flags & (1L << 24)) != 0;
        int scan = (int)(((long)flags >> 16) & 255);
        return key switch
        {
            0x10 => scan == 0x36 ? 0xA1 : 0xA0,
            0x11 => extended ? 0xA3 : 0xA2,
            0x12 => extended ? 0xA5 : 0xA4,
            _ => key
        };
    }
}
