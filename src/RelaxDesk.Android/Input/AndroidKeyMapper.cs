using Android.Views;

namespace RelaxDesk.Mobile;

/// <summary>
/// Maps Android hardware keyboard Keycodes to Windows VirtualKey / ScanCode / Extended tuples
/// compatible with RelaxDesk input-v2.
/// </summary>
internal static class AndroidKeyMapper
{
    public static bool TryMap(Keycode keyCode, out int virtualKey, out int scanCode, out bool extended)
    {
        extended = false;
        scanCode = 0;

        if (keyCode >= Keycode.A && keyCode <= Keycode.Z)
        {
            virtualKey = 0x41 + ((int)keyCode - (int)Keycode.A);
            return true;
        }
        if (keyCode >= Keycode.Num0 && keyCode <= Keycode.Num9)
        {
            virtualKey = 0x30 + ((int)keyCode - (int)Keycode.Num0);
            return true;
        }
        if (keyCode >= Keycode.F1 && keyCode <= Keycode.F12)
        {
            virtualKey = 0x70 + ((int)keyCode - (int)Keycode.F1);
            return true;
        }

        (virtualKey, scanCode, extended) = keyCode switch
        {
            Keycode.Enter or Keycode.NumpadEnter => (0x0D, 0x1C, keyCode == Keycode.NumpadEnter),
            Keycode.Escape => (0x1B, 0x01, false),
            Keycode.Del => (0x08, 0x0E, false),          // Backspace
            Keycode.ForwardDel => (0x2E, 0x53, true),    // Delete
            Keycode.Tab => (0x09, 0x0F, false),
            Keycode.Space => (0x20, 0x39, false),
            Keycode.DpadLeft => (0x25, 0x4B, true),
            Keycode.DpadUp => (0x26, 0x48, true),
            Keycode.DpadRight => (0x27, 0x4D, true),
            Keycode.DpadDown => (0x28, 0x50, true),
            Keycode.MoveHome => (0x24, 0x47, true),
            Keycode.MoveEnd => (0x23, 0x4F, true),
            Keycode.PageUp => (0x21, 0x49, true),
            Keycode.PageDown => (0x22, 0x51, true),
            Keycode.Insert => (0x2D, 0x52, true),
            Keycode.ShiftLeft => (0xA0, 0x2A, false),
            Keycode.ShiftRight => (0xA1, 0x36, false),
            Keycode.CtrlLeft => (0xA2, 0x1D, false),
            Keycode.CtrlRight => (0xA3, 0x1D, true),
            Keycode.AltLeft => (0xA4, 0x38, false),
            Keycode.AltRight => (0xA5, 0x38, true),
            Keycode.MetaLeft => (0x5B, 0x5B, true),
            Keycode.MetaRight => (0x5C, 0x5C, true),
            Keycode.CapsLock => (0x14, 0x3A, false),
            Keycode.Minus => (0xBD, 0x0C, false),
            Keycode.Equals => (0xBB, 0x0D, false),
            Keycode.LeftBracket => (0xDB, 0x1A, false),
            Keycode.RightBracket => (0xDD, 0x1B, false),
            Keycode.Backslash => (0xDC, 0x2B, false),
            Keycode.Semicolon => (0xBA, 0x27, false),
            Keycode.Apostrophe => (0xDE, 0x28, false),
            Keycode.Grave => (0xC0, 0x29, false),
            Keycode.Comma => (0xBC, 0x33, false),
            Keycode.Period => (0xBE, 0x34, false),
            Keycode.Slash => (0xBF, 0x35, false),
            _ => (0, 0, false)
        };
        return virtualKey != 0;
    }
}
