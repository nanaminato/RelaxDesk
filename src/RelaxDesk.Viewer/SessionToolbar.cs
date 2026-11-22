using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace RelaxDesk.Viewer;

// A separate owned HWND is required: WPF overlays cannot cover HwndHost's
// native D3D surface. This window does not activate or reserve desktop pixels.
internal sealed class SessionToolbar : IDisposable
{
    readonly Window owner, window;
    readonly StackPanel controls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock endpoint = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, Margin = new(14, 0, 12, 0), MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly Button full;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    DateTime keepUntil;
    bool disposed, expanded;
    nint handle;
    int left, top, width, height;
    [StructLayout(LayoutKind.Sequential)] internal struct PixelPoint { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetStyle(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetStyle(nint hwnd, int index, nint value);
    public bool Expanded => expanded;
    internal bool IsDisposed => disposed;
    internal Func<PixelPoint?>? TestPointer;
    public Window Surface => window;
    internal Button FullScreenButton => full;
    internal Button DisconnectButton { get; }
    public SessionToolbar(Window owner, Action toggle, Action disconnect)
    {
        this.owner = owner;
        window = new Window { Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Transparent, AllowsTransparency = true, Width = 480, Height = 48, MinWidth = 0, MinHeight = 0, Focusable = false };
        RelaxDesk.Ui.ModernTheme.Apply(window);
        full = new Button { Content = "全屏", Focusable = false, Padding = new(14, 6, 14, 6), Margin = new(3) };
        DisconnectButton = new Button { Content = "断开", Focusable = false, Style = (Style)window.Resources["DangerButton"], Padding = new(14, 6, 14, 6), Margin = new(3) };
        full.Click += (_, _) => { toggle(); Reveal(); };
        DisconnectButton.Click += (_, _) => disconnect();
        controls.Children.Add(endpoint); controls.Children.Add(full); controls.Children.Add(DisconnectButton);
        window.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 36, 58)), CornerRadius = new(0, 0, 10, 10), Child = controls, BorderBrush = new SolidColorBrush(Color.FromRgb(86, 126, 184)), BorderThickness = new(1, 0, 1, 1), ClipToBounds = true };
        window.SourceInitialized += (_, _) => { handle = new WindowInteropHelper(window).Handle; SetStyle(handle, -20, GetStyle(handle, -20) | (nint)0x08000000); };
        owner.LocationChanged += OwnerChanged; owner.SizeChanged += OwnerChanged; owner.StateChanged += OwnerChanged; owner.Closed += OwnerClosed;
        timer.Tick += Tick;
    }
    public void SetSession(string address, bool fullScreen)
    {
        endpoint.Text = "RelaxDesk · " + address; SetFullScreen(fullScreen);
        if (!window.IsVisible) window.Show();
        Reveal(); timer.Start();
    }
    public void SetFullScreen(bool value) => full.Content = value ? "返回窗口" : "全屏";
    void OwnerChanged(object? sender, EventArgs e) => UpdatePosition();
    void OwnerClosed(object? sender, EventArgs e) => Dispose();
    void Reveal() { expanded = true; keepUntil = DateTime.UtcNow.AddSeconds(1.5); UpdatePosition(); }
    void Tick(object? sender, EventArgs e)
    {
        if (disposed) return;
        if (!owner.IsVisible || owner.WindowState == WindowState.Minimized || !owner.IsActive)
        { window.Hide(); return; }
        if (!window.IsVisible) window.Show();
        PixelPoint? pointer = TestPointer is null ? GetCursorPos(out var current) ? current : null : TestPointer();
        {
            bool inside = pointer is {} p && p.X >= left && p.X < left + width && p.Y >= top && p.Y < top + height;
            if (inside) { if (!expanded) Reveal(); keepUntil = DateTime.UtcNow.AddSeconds(1); }
            else if (expanded && DateTime.UtcNow >= keepUntil) { expanded = false; UpdatePosition(); }
        }
        UpdatePosition();
    }
    void UpdatePosition()
    {
        if (disposed || handle == 0 || !owner.IsLoaded || owner.WindowState == WindowState.Minimized) return;
        var content = (FrameworkElement)owner.Content;
        if (content.ActualWidth <= 0) return;
        Point origin = content.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(owner);
        width = (int)Math.Round(Math.Min(480, content.ActualWidth) * dpi.DpiScaleX);
        height = (int)Math.Round((expanded ? 48 : 5) * dpi.DpiScaleY);
        left = (int)Math.Round(origin.X + (content.ActualWidth * dpi.DpiScaleX - width) / 2); top = (int)Math.Round(origin.Y);
        controls.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        SetWindowPos(handle, 0, left, top, width, height, 0x14); // NOACTIVATE | NOZORDER
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); timer.Tick -= Tick;
        owner.LocationChanged -= OwnerChanged; owner.SizeChanged -= OwnerChanged; owner.StateChanged -= OwnerChanged; owner.Closed -= OwnerClosed;
        window.Close();
    }
}
