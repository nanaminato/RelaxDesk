using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

// Exercises real WPF layout and HWND ownership without decoder/GPU/scanout.
internal static class HwndLifecycleVerification
{
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct WindowPos { public nint Hwnd, After; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern nint SendMessage(nint hwnd, uint message, nint wParam, ref WindowPos position);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindow(nint hwnd);
    public static void Run(Application app)
    {
        var host = new NativeVideoHost();
        var window = new Window { Title = "RelaxDesk HWND lifecycle contracts", Width = 500, Height = 320, Content = host, ShowActivated = false };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                nint handle = host.Handle;
                if (handle == 0 || !GetClientRect(handle, out var before) || before.Right < 10) throw new InvalidOperationException("Missing laid-out child");
                var proposed = new WindowPos { Hwnd = handle, X = -900, Y = -900, Width = 1, Height = 1, Flags = 3 };
                SendMessage(handle, 0x46, 0, ref proposed); // real HwndHost base proc corrects proposal
                if (proposed.Width != before.Right || proposed.Height != before.Bottom || (proposed.Flags & 3) != 0 || proposed.X == -900 || proposed.Y == -900)
                    throw new InvalidOperationException("HwndHost WM_WINDOWPOSCHANGING layout contract bypassed");
                host.Visibility = Visibility.Collapsed; host.UpdateWindowPos(); await Task.Delay(30);
                if (IsWindowVisible(handle)) throw new InvalidOperationException("Child did not hide");
                host.Visibility = Visibility.Visible; window.Width += 80; window.Height += 60; window.UpdateLayout(); host.UpdateWindowPos(); await Task.Delay(30);
                if (!IsWindowVisible(handle) || !GetClientRect(handle, out var after) || after.Right <= before.Right || after.Bottom <= before.Bottom)
                    throw new InvalidOperationException("Child resize/restore failed");
                window.Content = null; host.Dispose();
                if (IsWindow(handle) || host.Handle != 0 || !GraphicsDiagnostics.Resources().Empty) throw new InvalidOperationException("Child ownership not released");
                Console.WriteLine("PASS 4 real WPF HWND contracts: layout proposal, hide, resize/restore, destruction; no GPU Present claim");
            }
            catch (Exception error) { Console.Error.WriteLine("FAIL HWND lifecycle: " + error); Environment.ExitCode = 1; }
            finally { host.Dispose(); window.Close(); }
        };
        app.Run(window);
    }
}
