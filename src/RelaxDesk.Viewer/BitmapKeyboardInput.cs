using RelaxDesk.Core;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    [DllImport("imm32.dll")] static extern uint ImmGetVirtualKey(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    readonly HashSet<int> localConnectionKeys = [];
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        InitializeFullscreenPresentation();
        // The bitmap fallback must also use the remote machine's IME and
        // consume commands before WPF focus navigation/command bindings.
        InputMethod.SetIsInputMethodEnabled(image, false);
        ComponentDispatcher.ThreadFilterMessage += FilterBitmapKeyboard;
        image.PreviewKeyDown += (_, args) => ForwardBitmapKey(args, false);
        image.PreviewKeyUp += (_, args) => ForwardBitmapKey(args, true);
    }
    protected override void OnClosed(EventArgs e)
    {
        ComponentDispatcher.ThreadFilterMessage -= FilterBitmapKeyboard;
        base.OnClosed(e);
    }
    void FilterBitmapKeyboard(ref MSG message, ref bool handled)
    {
        if (!handled && HandleConnectionKeys(message, Keyboard.Modifiers)) { handled = true; return; }
        if (handled || !IsActive || !image.IsVisible || !image.IsKeyboardFocusWithin || session is null || session.IsCancellationRequested || message.hwnd != new WindowInteropHelper(this).Handle) return;
        if(message.message==0x20E&&inputVersion==2){int delta=(short)((long)message.wParam>>16);if(delta!=0)SendRich(new("wheel",WheelX:delta));handled=true;return;}
        if (message.message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            int code = (int)message.wParam;
            if (code == 0xE5) code = (int)ImmGetVirtualKey(message.hwnd);
            if (code is > 0 and < 255 && code != 0xE5) SendRawKey(code,message.lParam,message.message is 0x101 or 0x105);
            handled = true;
        }
        else if (message.message is 0x102 or 0x103 or 0x106 or 0x107 or 0x286) handled = true;
    }

    internal bool HandleConnectionKeys(MSG message, ModifierKeys modifiers)
    {
        if (sessionToolbar is null || !IsActive || message.message is not (0x100 or 0x101 or 0x104 or 0x105)) return false;
        nint owner = new WindowInteropHelper(this).Handle;
        if (message.hwnd != owner && GetAncestor(message.hwnd, 2) != owner) return false;
        int key = (int)message.wParam;
        if (message.message is 0x101 or 0x105) return localConnectionKeys.Remove(key);
        bool repeated = ((long)message.lParam & (1L << 30)) != 0;
        if (repeated && localConnectionKeys.Contains(key)) return true;
        if (!repeated) localConnectionKeys.Remove(key);
        if (modifiers != (ModifierKeys.Control | ModifierKeys.Alt) ||
            key is not (0x03 or 0x13 or 0x24)) return false;
        localConnectionKeys.Add(key);
        if (key == 0x24)
        {
            ReleaseHeldInputs();
            sessionToolbar.OpenSessionMenu();
        }
        else ToggleFullScreen();
        return true;
    }
    void ForwardBitmapKey(KeyEventArgs args, bool up)
    {
        if (!image.IsKeyboardFocusWithin || session is null || session.IsCancellationRequested) return;
        int code = KeyInterop.VirtualKeyFromKey(args.Key == Key.System ? args.SystemKey : args.Key);
        if (code is > 0 and < 256 && code != 0xE5) Send(code, up);
        args.Handled = true;
    }
}
