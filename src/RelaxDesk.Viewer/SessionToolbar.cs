using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

// A separate owned HWND is required: WPF overlays cannot cover HwndHost's
// native D3D surface. This window does not activate or reserve desktop pixels.
internal sealed class SessionToolbar : IDisposable
{
    readonly Window owner, window;
    readonly Action? releaseInput;
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
    internal Button FilesButton {get;}
    Action? openFiles;
    public void SetFiles(Action? open,int recoveryCount=0){openFiles=open;FilesButton.IsEnabled=open is not null;FilesButton.Content=recoveryCount>0?"文件续传":"文件";FilesButton.ToolTip=recoveryCount>0?$"有{recoveryCount}项中断任务，打开后确认继续":"上传、下载与任务队列（需Host授权）";endpoint.MaxWidth=recoveryCount>0?140:170;}
    internal Button ShortcutsButton { get; }
    internal ContextMenu ShortcutMenu { get; }
    readonly MenuItem displays=new(){Header="显示器（Host 未开放切换）",IsEnabled=false};
    internal MenuItem DisplaysAction=>displays;
    readonly MenuItem zoom=new(){Header="画面缩放"};
    internal MenuItem ZoomAction=>zoom;
    public void SetViewport(ViewportOptions options,Action<int> set)
    {
        zoom.Items.Clear();zoom.Header=options.Zoom==0?"画面缩放 · 适应窗口":$"画面缩放 · {options.Zoom}%";
        foreach(int percent in new[]{0,50,75,100,125,150,200,300,400})
        {
            var item=new MenuItem{Header=percent==0?"适应窗口（保持比例）":percent==100?"1:1 · 100%（物理像素）":$"{percent}%",IsCheckable=true,IsChecked=options.Zoom==percent,Tag=percent};
            item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;set(percent);};zoom.Items.Add(item);
        }
        var custom=new StackPanel{Orientation=Orientation.Horizontal};var value=new TextBox{Text=(options.Zoom==0?100:options.Zoom).ToString(),Width=60,MaxLength=3};
        var apply=new Button{Content="应用",Padding=new(8,2,8,2)};custom.Children.Add(new TextBlock{Text="自定义 25–400%  ",VerticalAlignment=VerticalAlignment.Center});custom.Children.Add(value);custom.Children.Add(apply);
        var entry=new MenuItem{Header=custom,StaysOpenOnClick=true};zoom.Items.Add(new Separator());zoom.Items.Add(entry);
        apply.Click+=(_,_)=>{if(int.TryParse(value.Text,out int percent)&&percent is >=25 and <=400){ShortcutMenu.IsOpen=false;set(percent);}else value.ToolTip="请输入 25–400 之间的整数百分比";};
    }
    public void SetRemoteInputReady(bool ready){foreach(var item in ShortcutMenu.Items.OfType<MenuItem>().Where(item=>item.Tag is RemoteShortcut))item.IsEnabled=ready;}
    public void SetDisplays(DisplaySessionInfo? state,bool ready,Action<string>? select)
    {
        displays.Items.Clear();displays.IsEnabled=state is not null;displays.Header=state is null?"显示器（Host 未开放切换）":state.Unavailable?(state.Allowed.Length>0?"显示器（屏幕断开，可重新选择）":"显示器（等待授权屏幕恢复）"):ready?"显示器":"显示器（等待新画面…）";
        if(state is null)return;
        foreach(string id in state.Allowed)
        {
            string label=id=="all"?"全部屏幕（兼容捕获）":state.Displays.Single(d=>d.Id==id).ToString();
            var item=new MenuItem{Header=label,IsCheckable=true,IsChecked=id==state.Selected,IsEnabled=ready,Tag=id};
            item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;select?.Invoke(id);};displays.Items.Add(item);
        }
    }
    readonly MenuItem clipboard = new() { Header = "剪贴板未授权 / 已关闭", IsEnabled = false };
    internal MenuItem ClipboardAction => clipboard;
    readonly MenuItem fileClipboard=new(){Header="文件剪贴板…",IsEnabled=false};
    Action? openFileClipboard;
    internal MenuItem FileClipboardAction=>fileClipboard;
    public void SetFileClipboard(Action? open,bool remoteOffer){openFileClipboard=open;fileClipboard.IsEnabled=open is not null;fileClipboard.Header=remoteOffer?"粘贴远端文件…":"文件剪贴板…";}
    readonly MenuItem textInput=new(){Header="本机输入法 → 发送已完成的文字",IsEnabled=false};
    internal MenuItem TextInputAction=>textInput;
    Action? openTextInput;
    public void SetTextInput(Action? open){openTextInput=open;textInput.IsEnabled=open is not null;}
    Action? disableClipboard;
    public void SetClipboard(bool enabled, Action? disable)
    { clipboard.Header = enabled ? "停止剪贴板同步（重新开启需重新授权）" : "剪贴板未授权 / 已关闭"; clipboard.IsEnabled = enabled; disableClipboard = disable; }
    public SessionToolbar(Window owner, Action toggle, Action disconnect, Action<RemoteShortcut>? shortcut = null,Action? releaseInput=null)
    {
        this.owner = owner;
        this.releaseInput=releaseInput;
        window = new Window { Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Transparent, AllowsTransparency = true, Width = 560, Height = 48, MinWidth = 0, MinHeight = 0, Focusable = false };
        RelaxDesk.Ui.ModernTheme.Apply(window);
        full = new Button { Content = "全屏", Focusable = false, Padding = new(14, 6, 14, 6), Margin = new(3) };
        DisconnectButton = new Button { Content = "断开", Focusable = false, Style = (Style)window.Resources["DangerButton"], Padding = new(14, 6, 14, 6), Margin = new(3) };
        full.Click += (_, _) => { toggle(); Reveal(); };
        DisconnectButton.Click += (_, _) => disconnect();
        FilesButton=new Button{Content="文件",Focusable=false,Padding=new(12,6,12,6),Margin=new(3),IsEnabled=false,ToolTip="上传、下载与任务队列（需Host授权）"};FilesButton.Click+=(_,_)=>openFiles?.Invoke();
        ShortcutsButton = new Button { Content = "快捷键", Focusable = false, Padding = new(12, 6, 12, 6), Margin = new(3), IsEnabled = shortcut is not null, ToolTip = "发送到远程电脑；仅对已授权会话生效" };
        ShortcutMenu = new ContextMenu();
        textInput.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;openTextInput?.Invoke();};
        foreach (var (label, action) in new[] {
            ("复制  Ctrl+C", RemoteShortcut.Copy), ("粘贴  Ctrl+V", RemoteShortcut.Paste),
            ("剪切  Ctrl+X", RemoteShortcut.Cut), ("全选  Ctrl+A", RemoteShortcut.SelectAll),
            ("撤销  Ctrl+Z", RemoteShortcut.Undo), ("重做  Ctrl+Y", RemoteShortcut.Redo),
            ("保存  Ctrl+S", RemoteShortcut.Save), ("查找  Ctrl+F", RemoteShortcut.Find),
            ("地址栏  Ctrl+L", RemoteShortcut.AddressBar), ("任务管理器  Ctrl+Shift+Esc", RemoteShortcut.TaskManager),
            ("关闭远程窗口  Alt+F4", RemoteShortcut.CloseWindow), ("切换远程窗口  Alt+Tab", RemoteShortcut.SwitchWindow),
            ("文件资源管理器  Win+E", RemoteShortcut.Explorer), ("显示桌面  Win+D", RemoteShortcut.Desktop),
            ("运行  Win+R", RemoteShortcut.Run) })
        {
            var item = new MenuItem { Header = label, Tag = action };
            item.Click += (_, _) => { ShortcutMenu.IsOpen = false; shortcut?.Invoke(action); Reveal(); };
            ShortcutMenu.Items.Add(item);
        }
        ShortcutMenu.Items.Add(new Separator());
        ShortcutMenu.Items.Add(new MenuItem { Header = "Ctrl+Alt+End / 安全注意序列：不支持", IsEnabled = false });
        ShortcutMenu.Items.Add(new Separator()); ShortcutMenu.Items.Add(clipboard);
        ShortcutMenu.Items.Add(textInput);
        ShortcutMenu.Items.Add(fileClipboard);fileClipboard.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;openFileClipboard?.Invoke();};
        ShortcutMenu.Items.Add(displays);
        ShortcutMenu.Items.Add(zoom);
        clipboard.Click += (_, _) => { ShortcutMenu.IsOpen = false; disableClipboard?.Invoke(); };
        ShortcutsButton.Click += (_, _) => { ShortcutMenu.PlacementTarget = ShortcutsButton; ShortcutMenu.IsOpen = true; Reveal(); };
        controls.Children.Add(endpoint); controls.Children.Add(FilesButton); controls.Children.Add(ShortcutsButton); controls.Children.Add(full); controls.Children.Add(DisconnectButton);
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
    void Reveal() { if(!expanded)releaseInput?.Invoke();expanded = true; keepUntil = DateTime.UtcNow.AddSeconds(1.5); UpdatePosition(); }
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
            else if (expanded && !ShortcutMenu.IsOpen && DateTime.UtcNow >= keepUntil) { expanded = false; UpdatePosition(); }
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
        width = (int)Math.Round(Math.Min(560, content.ActualWidth) * dpi.DpiScaleX);
        height = (int)Math.Round((expanded ? 48 : 5) * dpi.DpiScaleY);
        left = (int)Math.Round(origin.X + (content.ActualWidth * dpi.DpiScaleX - width) / 2); top = (int)Math.Round(origin.Y);
        controls.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        SetWindowPos(handle, 0, left, top, width, height, 0x14); // NOACTIVATE | NOZORDER
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); timer.Tick -= Tick;
        owner.LocationChanged -= OwnerChanged; owner.SizeChanged -= OwnerChanged; owner.StateChanged -= OwnerChanged; owner.Closed -= OwnerClosed;
        ShortcutMenu.IsOpen = false; window.Close();
    }
}
