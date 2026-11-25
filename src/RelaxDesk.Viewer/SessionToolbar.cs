using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

// A separate owned HWND is required: WPF overlays cannot cover HwndHost's
// native D3D surface. This window does not activate or reserve desktop pixels.
internal sealed class SessionToolbar : IDisposable
{
    const double MaximumWidth = 720;
    readonly Window owner, window;
    readonly Action? releaseInput;
    readonly StackPanel controls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock endpoint = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, Margin = new(14, 0, 12, 0), MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock mediaPath = new() { FontSize = 11, Foreground = Brushes.LightGray, TextTrimming = TextTrimming.CharacterEllipsis };
    (MediaCodec Codec, bool Hardware, bool Gpu)? lastMediaPath;
    internal string MediaPathText => mediaPath.Text;
    public void SetMediaPath(MediaFrame frame, bool gpu)
    {
        var current = (frame.Codec, frame.HardwareEncoded, gpu);
        if (lastMediaPath == current) return;
        lastMediaPath = current;
        string encoding = frame.Codec == MediaCodec.DeflateBgra ? "图像兼容编码" : frame.HardwareEncoded ? "硬件编码" : "软件编码";
        mediaPath.Text = encoding + " · " + (gpu ? "GPU 显示" : "CPU 显示");
        mediaPath.ToolTip = "Host 帧报告：" + encoding + "；Viewer 实际提交：" + (gpu ? "GPU 显示" : "CPU 兼容显示") + "。显示方式不代表解码器使用硬件；性能以实测为准。";
    }
    readonly Button full;
    readonly ToggleButton pin = new() { Content = "固定", Focusable = false, Padding = new(10, 6, 10, 6), Margin = new(3) };
    readonly Button minimize = new() { Content = "最小化", Focusable = false, Padding = new(10, 6, 10, 6), Margin = new(3) };
    readonly MenuItem remoteKeys = new() { Header = "远程快捷键" };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    DateTime keepUntil;
    bool disposed, expanded;
    nint handle;
    int left, top, width, height;
    [StructLayout(LayoutKind.Sequential)] internal struct PixelPoint { public int X, Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out PixelPoint point);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern nint GetStyle(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern nint SetStyle(nint hwnd, int index, nint value);
    public bool Expanded => expanded;
    internal bool IsDisposed => disposed;
    internal Func<PixelPoint?>? TestPointer;
    public Window Surface => window;
    internal Button FullScreenButton => full;
    internal ToggleButton PinButton => pin;
    internal Button MinimizeButton => minimize;
    internal ItemCollection RemoteKeyItems => remoteKeys.Items;
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
    public void SetRemoteInputReady(bool ready) => SetRemoteShortcuts(ready);
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
    readonly MenuItem audio=new(){Header="声音未授权 / 已关闭",IsEnabled=false};
    readonly MenuItem statistics=new(){Header="连接统计（只读）",IsEnabled=false};
    readonly MenuItem videoSettings=new(){Header="视频设置（Host 未开放）",IsEnabled=false};
    internal MenuItem VideoSettingsAction=>videoSettings;
    public void SetVideoSettings(VideoSettings? state,bool ready,bool pending,bool h264,Action<int,int> change)
    {
        videoSettings.Header=state is null?"视频设置（Host 未开放）":pending?"视频设置（等待应用确认…）":state.Generation==1_000_000?"视频设置（请重新连接后调整）":ready?$"视频设置 · {state.FramesPerSecond} FPS 上限":"视频设置（等待画面/短暂冷却）";
        videoSettings.IsEnabled=state is not null&&ready;videoSettings.Items.Clear();if(state is null)return;
        var fps=new MenuItem{Header="帧率上限"};foreach(int value in new[]{15,30,60})
        {var item=new MenuItem{Header=$"{value} FPS",IsCheckable=true,IsChecked=state.FramesPerSecond==value,IsEnabled=state.FramesPerSecond!=value};item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;change(value,state.Bitrate);};fps.Items.Add(item);}videoSettings.Items.Add(fps);
        var bitrate=new MenuItem{Header=h264?"H.264 目标码率":"当前图像兼容编码，码率不适用",IsEnabled=h264};foreach(int value in new[]{2,8,20})
        {var item=new MenuItem{Header=$"{value} Mbit/s",IsCheckable=true,IsChecked=state.Bitrate==value*1_000_000,IsEnabled=state.Bitrate!=value*1_000_000};item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;change(state.FramesPerSecond,value*1_000_000);};bitrate.Items.Add(item);}videoSettings.Items.Add(bitrate);
        videoSettings.Items.Add(new Separator());videoSettings.Items.Add(new MenuItem{Header="实际提交帧率与媒体流量见连接统计",IsEnabled=false});videoSettings.Items.Add(new MenuItem{Header="码率是目标参数；更高目标通常增加带宽",IsEnabled=false});
    }
    internal MenuItem StatisticsAction=>statistics;
    readonly TextBlock mediaRate = new() { FontSize = 12, TextAlignment = TextAlignment.Center };
    readonly TextBlock connectionTiming = new() { FontSize = 11, TextAlignment = TextAlignment.Center };
    internal Button ConnectionInfoButton { get; }
    internal string MediaRateText => mediaRate.Text;
    internal string ConnectionTimingText => connectionTiming.Text;
    Action? openStatistics;
    Func<ToolbarStatistics>? statisticsSource;
    ToolbarStatistics? previousStatistics;
    public void SetStatistics(Action? open, Func<ToolbarStatistics>? source)
    {
        openStatistics = open;
        statistics.IsEnabled = open is not null;
        statisticsSource = source;
        previousStatistics = null;
        ConnectionInfoButton.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        RefreshConnectionInfo();
    }

    void RefreshConnectionInfo()
    {
        if (statisticsSource is null) return;
        var sample = statisticsSource();
        PipelineRate? rate = null;
        if (previousStatistics is { } previous)
        {
            if (sample.Counters.ElapsedSeconds - previous.Counters.ElapsedSeconds < 0.95) return;
            if (previous.Transport == sample.Transport && previous.TransportRevision == sample.TransportRevision)
                rate = PipelineRate.BetweenCounters(previous.Counters, sample.Counters);
        }
        previousStatistics = sample;
        mediaRate.Text = rate is null ? "— FPS   ·   — Mbit/s" : $"{rate.FramesPerSecond:F1} FPS   ·   {rate.ApplicationMegabitsPerSecond:F2} Mbit/s";
        string rtt = sample.ControlRtt < 0 ? "—" : $"{sample.ControlRtt:F1} ms";
        connectionTiming.Text = $"{sample.Transport}   ·   RTT {rtt}";
    }
    internal MenuItem AudioAction=>audio;
    public void SetAudio(bool enabled,double volume,bool muted,Action<double,bool> adjust,Action stop)
    {
        audio.IsEnabled=enabled;audio.Header=!enabled?"声音未授权 / 已关闭":muted?"声音 · 已静音":$"声音 · {volume*100:0}%";audio.Items.Clear();if(!enabled)return;
        var mute=new MenuItem{Header="静音",IsCheckable=true,IsChecked=muted};mute.Click+=(_,_)=>adjust(volume,!muted);audio.Items.Add(mute);
        foreach(int percent in new[]{0,25,50,75,100}){var item=new MenuItem{Header=$"音量 {percent}%",IsCheckable=true,IsChecked=Math.Abs(volume-percent/100d)<0.001};item.Click+=(_,_)=>adjust(percent/100d,muted);audio.Items.Add(item);}
        audio.Items.Add(new Separator());var end=new MenuItem{Header="停止声音传输（重新开启需重新授权）"};end.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;stop();};audio.Items.Add(end);
    }
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
        window = new Window { Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Transparent, AllowsTransparency = true, Width = MaximumWidth, Height = 48, MinWidth = 0, MinHeight = 0, Focusable = false };
        RelaxDesk.Ui.ModernTheme.Apply(window);
        full = new Button { Content = "全屏", Focusable = false, Padding = new(14, 6, 14, 6), Margin = new(3) };
        DisconnectButton = new Button { Content = "断开", Focusable = false, Style = (Style)window.Resources["DangerButton"], Padding = new(14, 6, 14, 6), Margin = new(3) };
        full.Click += (_, _) => { toggle(); Reveal(); };
        full.ToolTip = "切换全屏和窗口（Ctrl+Alt+Break）";
        pin.Checked += (_, _) => { pin.Content = "取消固定"; Reveal(); };
        pin.Unchecked += (_, _) => { pin.Content = "固定"; Reveal(); };
        pin.ToolTip = "固定连接栏，关闭自动收起";
        minimize.Click += (_, _) => owner.WindowState = WindowState.Minimized;
        minimize.ToolTip = "最小化连接端窗口，保持会话";
        DisconnectButton.Click += (_, _) => disconnect();
        FilesButton=new Button{Content="文件",Focusable=false,Padding=new(12,6,12,6),Margin=new(3),IsEnabled=false,ToolTip="上传、下载与任务队列（需Host授权）"};FilesButton.Click+=(_,_)=>openFiles?.Invoke();
        ShortcutsButton = new Button { Content = "会话菜单", Focusable = false, Padding = new(12, 6, 12, 6), Margin = new(3), ToolTip = "远程快捷键、显示和连接设置" };
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
            var item = new MenuItem { Header = label, Tag = action, IsEnabled = shortcut is not null };
            item.Click += (_, _) => { ShortcutMenu.IsOpen = false; shortcut?.Invoke(action); Reveal(); };
            remoteKeys.Items.Add(item);
        }
        remoteKeys.Items.Add(new Separator());
        remoteKeys.Items.Add(new MenuItem { Header = "Ctrl+Alt+End / 安全注意序列：不支持", IsEnabled = false });
        ShortcutMenu.Items.Add(remoteKeys);
        ShortcutMenu.Items.Add(new Separator()); ShortcutMenu.Items.Add(clipboard);
        ShortcutMenu.Items.Add(textInput);
        ShortcutMenu.Items.Add(fileClipboard);fileClipboard.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;openFileClipboard?.Invoke();};
        ShortcutMenu.Items.Add(displays);
        ShortcutMenu.Items.Add(zoom);
        ShortcutMenu.Items.Add(audio);
        ShortcutMenu.Items.Add(statistics);statistics.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;openStatistics?.Invoke();};
        ShortcutMenu.Items.Add(videoSettings);
        clipboard.Click += (_, _) => { ShortcutMenu.IsOpen = false; disableClipboard?.Invoke(); };
        ShortcutsButton.Click += (_, _) => OpenSessionMenu();
        ShortcutMenu.Closed += (_, _) => keepUntil = DateTime.UtcNow.AddSeconds(1);
        var readings = new StackPanel();
        readings.Children.Add(mediaRate);
        readings.Children.Add(connectionTiming);
        var connectionButtonContent = new DockPanel();
        var more = new TextBlock { Text = "更多", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 0, 0) };
        DockPanel.SetDock(more, Dock.Right);
        connectionButtonContent.Children.Add(more);
        connectionButtonContent.Children.Add(readings);
        ConnectionInfoButton = new Button
        {
            Content = connectionButtonContent, Focusable = false, Padding = new(8, 2, 8, 2), Margin = new(3),
            Width = 183, Visibility = Visibility.Collapsed,
            ToolTip = "画面提交帧率 · 视频应用数据接收速率 · 控制往返时间（含排队）\n点击查看连接统计"
        };
        System.Windows.Automation.AutomationProperties.SetName(ConnectionInfoButton, "连接状态和详细统计");
        ConnectionInfoButton.Click += (_, _) => openStatistics?.Invoke();
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 12, 0) };
        endpoint.Margin = new(0); identity.Children.Add(endpoint); identity.Children.Add(mediaPath);
        controls.Children.Add(ConnectionInfoButton); controls.Children.Add(FilesButton); controls.Children.Add(ShortcutsButton);
        controls.Children.Add(pin); controls.Children.Add(minimize); controls.Children.Add(full); controls.Children.Add(DisconnectButton);
        var bar = new DockPanel();
        DockPanel.SetDock(controls, Dock.Right);
        bar.Children.Add(controls);
        bar.Children.Add(identity);
        window.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(23, 36, 58)), CornerRadius = new(0, 0, 10, 10), Child = bar, BorderBrush = new SolidColorBrush(Color.FromRgb(86, 126, 184)), BorderThickness = new(1, 0, 1, 1), ClipToBounds = true };
        window.SourceInitialized += (_, _) => { handle = new WindowInteropHelper(window).Handle; SetStyle(handle, -20, GetStyle(handle, -20) | (nint)0x08000000); };
        owner.LocationChanged += OwnerChanged; owner.SizeChanged += OwnerChanged; owner.StateChanged += OwnerChanged; owner.Closed += OwnerClosed;
        timer.Tick += Tick;
    }
    public void SetRemoteShortcuts(bool enabled)
    {
        foreach (var item in remoteKeys.Items.OfType<MenuItem>())
            if (item.Tag is RemoteShortcut) item.IsEnabled = enabled;
        remoteKeys.IsEnabled = enabled;
    }
    public void SetEndpoint(string address)
    {
        endpoint.Text = address;
        endpoint.ToolTip = address;
    }

    internal string EndpointText => endpoint.Text;
    internal object EndpointToolTip => endpoint.ToolTip;

    public void SetSession(string address, bool fullScreen)
    {
        lastMediaPath = null; mediaPath.Text = "等待首帧"; mediaPath.ToolTip = null;
        SetEndpoint(address); SetFullScreen(fullScreen);
        if (!window.IsVisible) window.Show();
        Reveal(); timer.Start();
    }
    public void SetFullScreen(bool value)
    {
        full.Content = value ? "返回窗口" : "全屏";
        pin.Visibility = minimize.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        Reveal();
    }
    internal void OpenSessionMenu()
    {
        Reveal();
        ShortcutMenu.PlacementTarget = ShortcutsButton;
        ShortcutMenu.IsOpen = true;
    }
    void OwnerChanged(object? sender, EventArgs e) => UpdatePosition();
    void OwnerClosed(object? sender, EventArgs e) => Dispose();
    void Reveal() { if(!expanded)releaseInput?.Invoke();expanded = true; keepUntil = DateTime.UtcNow.AddSeconds(1.5); UpdatePosition(); }
    void Tick(object? sender, EventArgs e)
    {
        if (disposed) return;
        RefreshConnectionInfo();
        if (!owner.IsVisible || owner.WindowState == WindowState.Minimized || (!owner.IsActive && !MenuOwnsForeground()))
        {
            ShortcutMenu.IsOpen = false;
            window.Hide();
            return;
        }
        if (!window.IsVisible)
        {
            window.Show();
            UpdatePosition(force: true);
        }
        if (ShortcutMenu.IsOpen) return;
        PixelPoint? pointer = TestPointer is null ? GetCursorPos(out var current) ? current : null : TestPointer();
        {
            bool inside = pointer is {} p && p.X >= left && p.X < left + width && p.Y >= top && p.Y < top + height;
            if (inside) { if (!expanded) Reveal(); keepUntil = DateTime.UtcNow.AddSeconds(1); }
            else if (expanded && (pin.Visibility != Visibility.Visible || pin.IsChecked != true) &&
                DateTime.UtcNow >= keepUntil) expanded = false;
        }
        UpdatePosition();
    }
    bool MenuOwnsForeground()
    {
        if (!ShortcutMenu.IsOpen) return false;
        const uint RootOwner = 3;
        nint foreground = GetForegroundWindow();
        nint viewer = new WindowInteropHelper(owner).Handle;
        nint popup = (PresentationSource.FromVisual(ShortcutMenu) as HwndSource)?.Handle ?? 0;
        return foreground != 0 && (foreground == handle || foreground == popup ||
            (viewer != 0 && GetAncestor(foreground, RootOwner) == viewer));
    }
    void UpdatePosition(bool force = false)
    {
        if (disposed || handle == 0 || !owner.IsLoaded || owner.WindowState == WindowState.Minimized) return;
        var content = (FrameworkElement)owner.Content;
        if (content.ActualWidth <= 0) return;
        Point origin = content.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(owner);
        int nextWidth = (int)Math.Round(Math.Min(statisticsSource is null ? 560 : MaximumWidth, content.ActualWidth) * dpi.DpiScaleX);
        int nextHeight = (int)Math.Round((expanded ? 48 : 5) * dpi.DpiScaleY);
        int nextLeft = (int)Math.Round(origin.X + (content.ActualWidth * dpi.DpiScaleX - nextWidth) / 2);
        int nextTop = (int)Math.Round(origin.Y);
        if (!force && (left, top, width, height) == (nextLeft, nextTop, nextWidth, nextHeight)) return;
        ((Border)window.Content).Child.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        if (SetWindowPos(handle, 0, nextLeft, nextTop, nextWidth, nextHeight, 0x214)) // NOACTIVATE | NOZORDER | NOOWNERZORDER
            (left, top, width, height) = (nextLeft, nextTop, nextWidth, nextHeight);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); timer.Tick -= Tick;
        owner.LocationChanged -= OwnerChanged; owner.SizeChanged -= OwnerChanged; owner.StateChanged -= OwnerChanged; owner.Closed -= OwnerClosed;
        ShortcutMenu.IsOpen = false; window.Close();
    }
}
