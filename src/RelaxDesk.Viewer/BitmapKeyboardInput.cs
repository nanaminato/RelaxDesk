using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Runtime.InteropServices;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    [DllImport("imm32.dll")] static extern uint ImmGetVirtualKey(nint hwnd);
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
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
        if (handled || !IsActive || !image.IsVisible || !image.IsKeyboardFocusWithin || session is null || session.IsCancellationRequested || message.hwnd != new WindowInteropHelper(this).Handle) return;
        if (message.message is 0x100 or 0x101 or 0x104 or 0x105)
        {
            int code = (int)message.wParam;
            if (code == 0xE5) code = (int)ImmGetVirtualKey(message.hwnd);
            code = RemoteKeyboardKeys.Resolve(code, message.lParam);
            if (code is > 0 and < 256 && code != 0xE5) Send(code, message.message is 0x101 or 0x105);
            handled = true;
        }
        else if (message.message is 0x102 or 0x103 or 0x106 or 0x107 or 0x286) handled = true;
    }
    void ForwardBitmapKey(KeyEventArgs args, bool up)
    {
        if (!image.IsKeyboardFocusWithin || session is null || session.IsCancellationRequested) return;
        int code = KeyInterop.VirtualKeyFromKey(args.Key == Key.System ? args.SystemKey : args.Key);
        if (code is > 0 and < 256 && code != 0xE5) Send(code, up);
        args.Handled = true;
    }
}
