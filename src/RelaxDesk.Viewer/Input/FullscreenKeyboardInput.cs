using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    const int LowLevelKeyboardHook = 13;
    const uint ExtendedKey = 0x01;
    const uint InjectedKey = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct KeyboardHookData
    {
        public int VirtualKey;
        public uint ScanCode, Flags, Time;
        public nuint ExtraInfo;
    }

    delegate nint KeyboardHookCallback(int code, nint message, nint data);
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    static extern nint SetKeyboardHook(int type, KeyboardHookCallback callback, nint module, uint thread);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    static extern nint GetCurrentModule(string? moduleName);

    KeyboardHookCallback? fullscreenKeyboardCallback;
    nint fullscreenKeyboardHook;
    nint fullscreenKeyboardOwner;
    readonly HashSet<(int Key, uint Scan, bool Extended)> capturedKeys = [];
    readonly HashSet<(int Key, uint Scan, bool Extended)> forwardingKeys = [];

    bool fullscreenShortcutPending;

    void InstallFullscreenKeyboard()
    {
        fullscreenKeyboardOwner = new WindowInteropHelper(this).Handle;
        fullscreenKeyboardCallback = RouteFullscreenKeyboard;
        fullscreenKeyboardHook = SetKeyboardHook(LowLevelKeyboardHook, fullscreenKeyboardCallback, GetCurrentModule(null), 0);
        if (fullscreenKeyboardHook == 0)
            System.Diagnostics.Trace.TraceWarning("Fullscreen keyboard redirection unavailable (Win32 {0}).", Marshal.GetLastWin32Error());
    }

    void RemoveFullscreenKeyboard()
    {
        if (fullscreenKeyboardHook != 0) UnhookWindowsHookEx(fullscreenKeyboardHook);
        fullscreenKeyboardHook = 0;
        fullscreenKeyboardCallback = null;
    }

    bool CanRedirectFullscreenKeyboard() =>
        fullScreen && !fullscreenShortcutPending && IsActive && WindowState != WindowState.Minimized &&
        GetForegroundWindow() == fullscreenKeyboardOwner &&
        smoke is null && inputAuthorized && viewportInputReady &&
        session is { IsCancellationRequested: false } &&
        (displaySession is null || displaySession.Ready) &&
        (nativeView is not null ? nativeView.OwnsKeyboard : image.IsVisible && image.IsKeyboardFocusWithin);

    nint RouteFullscreenKeyboard(int code, nint message, nint data)
    {
        if (code == 0 && (int)message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            var key = Marshal.PtrToStructure<KeyboardHookData>(data);
            if ((key.Flags & InjectedKey) == 0 && key.VirtualKey is > 0 and < 255 and not (0xE5 or 0xE7) &&
                RedirectFullscreenKey((int)message, key))
                return 1;
        }
        return CallNextHookEx(0, code, message, data);
    }

    bool RedirectFullscreenKey(int message, KeyboardHookData key)
    {
        bool up = message is 0x101 or 0x105;
        bool extended = (key.Flags & ExtendedKey) != 0;
        var identity = (key.VirtualKey, key.ScanCode, extended);
        bool captured = up ? capturedKeys.Remove(identity) : capturedKeys.Contains(identity);
        bool canRedirect = CanRedirectFullscreenKeyboard();

        if (!captured)
        {
            // A key pressed locally before capture must also be released locally.
            if (up || !canRedirect || (GetAsyncKeyState(key.VirtualKey) & 0x8000) != 0) return false;
            capturedKeys.Add(identity);
            forwardingKeys.Add(identity);
        }

        bool forward = up ? forwardingKeys.Remove(identity) : forwardingKeys.Contains(identity);
        // After focus changes, swallow the remainder of a captured key press
        // without replaying it into either desktop.
        if (!forward || fullscreenShortcutPending) return true;
        if (!canRedirect)
        {
            ReleaseHeldInputs();
            return true;
        }

        nint flags = (nint)(1L | ((key.ScanCode & 255L) << 16) |
            (extended ? 1L << 24 : 0) | (captured ? 1L << 30 : 0));
        if (!up && !captured && sessionToolbar is not null && IsConnectionShortcut(key.VirtualKey, CapturedModifiers()))
        {
            fullscreenShortcutPending = true;
            var shortcutSession = session;
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
            {
                fullscreenShortcutPending = false;
                if (session == shortcutSession && CanRedirectFullscreenKeyboard() && sessionToolbar is not null)
                    ExecuteConnectionShortcut(key.VirtualKey);
                else
                    ReleaseHeldInputs();
            });
            return true;
        }
        SendRawKey(key.VirtualKey, flags, up);
        return true;
    }

    ModifierKeys CapturedModifiers()
    {
        var modifiers = ModifierKeys.None;
        foreach (var held in forwardingKeys)
            modifiers |= held.Key switch
            {
                0x11 or 0xA2 or 0xA3 => ModifierKeys.Control,
                0x12 or 0xA4 or 0xA5 => ModifierKeys.Alt,
                0x10 or 0xA0 or 0xA1 => ModifierKeys.Shift,
                0x5B or 0x5C => ModifierKeys.Windows,
                _ => ModifierKeys.None
            };
        return modifiers;
    }
}
