using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

// A separate owned HWND is required: WPF overlays cannot cover HwndHost's
// native D3D surface. This window does not activate or reserve desktop pixels.
internal sealed class ConnectionBarPreferences
{
    public bool Pinned = true;
    public double HorizontalPosition = .5;
}

internal sealed class SessionToolbar : IDisposable
{
    const double MaximumWidth = 460;
    const double ExpandedHeight = 30;
    readonly Window owner, window;
    readonly Action? releaseInput;
    readonly StackPanel controls = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock endpoint = new() { VerticalAlignment = VerticalAlignment.Center, Foreground = Brushes.White, Margin = new(14, 0, 12, 0), MaxWidth = 170, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
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
        window.ToolTip = mediaPath.Text;
        mediaPath.ToolTip = "Host 帧报告：" + encoding + "；Viewer 实际提交：" + (gpu ? "GPU 显示" : "CPU 兼容显示") + "。显示方式不代表解码器使用硬件；性能以实测为准。";
    }
    readonly Button full;
    readonly ToggleButton pin = new() { Content = "固定", Focusable = false, Padding = new(10, 6, 10, 6), Margin = new(3) };
    readonly Button minimize = new() { Content = "最小化", Focusable = false, Padding = new(10, 6, 10, 6), Margin = new(3) };
    readonly MenuItem remoteKeys = new() { Header = "远程快捷键" };
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly ConnectionBarPreferences preferences;
    readonly Border dragRegion = new() { Background = Brushes.Transparent, Cursor = Cursors.SizeWE };
    DateTime keepUntil;
    DateTime? edgeSince;
    bool isFullScreen, dragging;
    int dragStartX, dragStartLeft;
    int anchorLeft, anchorWidth;
    internal int ScreenLeft => left;
    internal bool IsDragging => dragging;
    bool disposed, expanded, pointerInside;
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
    internal bool BlocksPointer(double x, double y) => !disposed &&
        (ShortcutMenu.IsOpen || (window.IsVisible && x >= left && x < left + width && y >= top && y < top + height));
    internal Button FullScreenButton => full;
    internal ToggleButton PinButton => pin;
    internal Button MinimizeButton => minimize;
    internal ItemCollection RemoteKeyItems => remoteKeys.Items;
    internal Button DisconnectButton { get; }
    internal MenuItem FilesAction { get; } = new() { Header = "文件传输…", IsEnabled = false };
    Action? openFiles;
    public void SetFiles(Action? open, int recoveryCount = 0)
    {
        openFiles = open;
        FilesAction.IsEnabled = open is not null;
        FilesAction.Header = recoveryCount > 0 ? "文件续传…" : "文件传输…";
        FilesAction.ToolTip = recoveryCount > 0 ? $"有{recoveryCount}项中断任务，打开后确认继续" : "上传、下载与任务队列（需Host授权）";
    }

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
        statistics.ToolTip = mediaRate.Text + "\n" + connectionTiming.Text;
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
    public SessionToolbar(Window owner, Action toggle, Action disconnect, Action<RemoteShortcut>? shortcut = null,Action? releaseInput=null, ConnectionBarPreferences? preferences=null)
    {
        this.owner = owner;
        this.preferences = preferences ?? new();
        this.releaseInput=releaseInput;
        window = new Window { Owner = owner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false, Background = Brushes.Transparent, AllowsTransparency = true, Width = MaximumWidth, Height = ExpandedHeight, MinWidth = 0, MinHeight = 0, Focusable = false };
        window.Resources.MergedDictionaries.Add((ResourceDictionary)System.Windows.Markup.XamlReader.Parse("""
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
 <Style TargetType="Button"><Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="White"/><Setter Property="BorderThickness" Value="0"/><Setter Property="FontFamily" Value="Segoe MDL2 Assets"/><Setter Property="FontSize" Value="12"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Chrome" Background="{TemplateBinding Background}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#447EAE"/></Trigger><Trigger Property="IsPressed" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#164871"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Chrome" Property="BorderBrush" Value="White"/><Setter TargetName="Chrome" Property="BorderThickness" Value="1"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
 <Style TargetType="ToggleButton"><Setter Property="Background" Value="Transparent"/><Setter Property="Foreground" Value="White"/><Setter Property="BorderThickness" Value="0"/><Setter Property="FontFamily" Value="Segoe MDL2 Assets"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton"><Border x:Name="Chrome" Background="{TemplateBinding Background}"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#164871"/></Trigger><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Chrome" Property="Background" Value="#447EAE"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>
</ResourceDictionary>
"""));
        full = new Button { Content = "全屏", Focusable = false, Padding = new(14, 6, 14, 6), Margin = new(3) };
        DisconnectButton = new Button { Content = "断开", Focusable = false, Padding = new(14, 6, 14, 6), Margin = new(3) };
        SetToolbarIcon(full, "\uE922", "全屏");
        SetToolbarIcon(DisconnectButton, "\uE8BB", "断开连接");
        SetToolbarIcon(minimize, "\uE921", "最小化");
        pin.Content = "\uE718"; pin.Width = 30; pin.Height = 28; pin.Margin = new Thickness(0); pin.Padding = new Thickness(0);
        System.Windows.Automation.AutomationProperties.SetName(pin, "固定连接栏");
        full.Click += (_, _) => { toggle(); Reveal(); };
        full.ToolTip = "切换全屏和窗口（Ctrl+Alt+Break）";
        pin.Checked += (_, _) => { pin.ToolTip = "取消固定连接栏"; Reveal(); };
        pin.Unchecked += (_, _) => { pin.ToolTip = "固定连接栏"; Reveal(); };
        pin.ToolTip = "固定连接栏，关闭自动收起";
        pin.IsChecked = this.preferences.Pinned;
        pin.Checked += (_, _) => this.preferences.Pinned = true;
        pin.Unchecked += (_, _) => this.preferences.Pinned = false;
        minimize.Click += (_, _) => owner.WindowState = WindowState.Minimized;
        minimize.ToolTip = "最小化连接端窗口，保持会话";
        DisconnectButton.Click += (_, _) => disconnect();

        ShortcutsButton = new Button { Content = "会话菜单", Focusable = false, Padding = new(12, 6, 12, 6), Margin = new(3), ToolTip = "远程快捷键、显示和连接设置" };
        SetToolbarIcon(ShortcutsButton, "\uE712", "会话菜单");
        ShortcutMenu = new ContextMenu { FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), FontSize = 13 };
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
        FilesAction.Click += (_, _) => { ShortcutMenu.IsOpen = false; openFiles?.Invoke(); };
        ShortcutMenu.Opened += (_, _) => releaseInput?.Invoke();
        ShortcutMenu.Items.Add(FilesAction);
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
        var identity = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 8, 0) };
        endpoint.Margin = new(0); endpoint.MaxWidth = 280; identity.Children.Add(endpoint);
        controls.Children.Add(ShortcutsButton);
        controls.Children.Add(minimize); controls.Children.Add(full); controls.Children.Add(DisconnectButton);
        var bar = new DockPanel();
        DockPanel.SetDock(controls, Dock.Right);
        DockPanel.SetDock(pin, Dock.Left); bar.Children.Add(pin);
        bar.Children.Add(controls);
        dragRegion.Child = identity;
        dragRegion.ToolTip = "左右拖动连接栏";
        dragRegion.MouseLeftButtonDown += (_, e) =>
        {
            if (GetCursorPos(out var point)) BeginDrag(point.X);
            dragRegion.CaptureMouse(); e.Handled = true;
        };
        dragRegion.MouseMove += (_, _) => { if (dragging && GetCursorPos(out var point)) MoveDrag(point.X); };
        dragRegion.MouseLeftButtonUp += (_, e) => { EndDrag(); e.Handled = true; };
        dragRegion.LostMouseCapture += (_, _) => dragging = false;
        bar.Children.Add(dragRegion);
        window.Content = new Border { Background = new SolidColorBrush(Color.FromRgb(32, 91, 143)), CornerRadius = new(0, 0, 5, 5), Child = bar, BorderBrush = new SolidColorBrush(Color.FromRgb(86, 126, 184)), BorderThickness = new(1, 0, 1, 1), ClipToBounds = true };
        window.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(window).Handle;
            SetStyle(handle, -20, GetStyle(handle, -20) | (nint)0x08000000);
            ((HwndSource)PresentationSource.FromVisual(window)).AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message != 0x21) return 0;
                handled = true; return 3; // MA_NOACTIVATE: retain the first click, never MA_*ANDEAT.
            });
        };
        owner.LocationChanged += OwnerChanged; owner.SizeChanged += OwnerChanged; owner.StateChanged += OwnerChanged; owner.Closed += OwnerClosed;
        timer.Tick += Tick;
    }
    static void SetToolbarIcon(Button button, string glyph, string name)
    {
        button.Content = glyph; button.Width = 30; button.Height = 28;
        button.Margin = new Thickness(0); button.Padding = new Thickness(0); button.ToolTip = name;
        System.Windows.Automation.AutomationProperties.SetName(button, name);
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
        SetEndpoint(address);
        SetFullScreen(fullScreen);
        timer.Start();
    }
    public void SetFullScreen(bool value)
    {
        isFullScreen = value;
        full.Content = value ? "\uE923" : "\uE922";
        full.ToolTip = value ? "返回窗口" : "全屏";
        System.Windows.Automation.AutomationProperties.SetName(full, value ? "返回窗口" : "全屏");
        pin.Visibility = minimize.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        EndDrag(); edgeSince = null;
        if (value) { if (!window.IsVisible) window.Show(); Reveal(); }
        else { ShortcutMenu.IsOpen = false; expanded = false; window.Hide(); }
    }
    internal void OpenSessionMenu()
    {
        releaseInput?.Invoke();
        if (isFullScreen) Reveal();
        ShortcutMenu.PlacementTarget = isFullScreen ? ShortcutsButton : owner.Content as FrameworkElement;
        ShortcutMenu.Placement = isFullScreen ? PlacementMode.Bottom : PlacementMode.RelativePoint;
        ShortcutMenu.IsOpen = true;
    }
    void OwnerChanged(object? sender, EventArgs e) => UpdatePosition();
    void OwnerClosed(object? sender, EventArgs e) => Dispose();
    void Reveal()
    {
        if (!expanded) releaseInput?.Invoke();
        expanded = true; edgeSince = null; keepUntil = DateTime.UtcNow.AddSeconds(1);
        UpdatePosition();
    }
    internal void BeginDrag(int screenX)
    {
        if (!isFullScreen) return;
        releaseInput?.Invoke(); Reveal(); dragging = true;
        dragStartX = screenX; dragStartLeft = left;
    }
    internal void MoveDrag(int screenX)
    {
        if (!dragging) return;
        int available = Math.Max(0, anchorWidth - width);
        preferences.HorizontalPosition = available == 0 ? .5 : Math.Clamp((double)(dragStartLeft + screenX - dragStartX - anchorLeft) / available, 0, 1);
        UpdatePosition();
    }
    internal void EndDrag()
    {
        dragging = false;
        if (Mouse.Captured == dragRegion) dragRegion.ReleaseMouseCapture();
        keepUntil = DateTime.UtcNow.AddSeconds(1);
    }
    internal void Poll() => Tick(null, EventArgs.Empty);
    void Tick(object? sender, EventArgs e)
    {
        if (disposed) return;
        RefreshConnectionInfo();
        if (!owner.IsVisible || owner.WindowState == WindowState.Minimized || !ViewerOwnsForeground())
        {
            EndDrag(); ShortcutMenu.IsOpen = false; window.Hide(); edgeSince = null;
            return;
        }
        if (!isFullScreen) { window.Hide(); return; }
        if (!window.IsVisible) { window.Show(); UpdatePosition(force: true); }
        if (ShortcutMenu.IsOpen || dragging || window.IsMouseCaptureWithin)
        {
            keepUntil = DateTime.UtcNow.AddSeconds(1); return;
        }
        PixelPoint? pointer = TestPointer is null ? GetCursorPos(out var current) ? current : null : TestPointer();
        bool inside = pointer is {} p && p.X >= left && p.X < left + width && p.Y >= top && p.Y < top + height;
        var now = DateTime.UtcNow;
        if (inside)
        {
            if (!pointerInside) releaseInput?.Invoke();
            if (!expanded)
            {
                edgeSince ??= now;
                if (now - edgeSince.Value >= TimeSpan.FromMilliseconds(200)) Reveal();
            }
            else keepUntil = now.AddSeconds(1);
        }
        else
        {
            edgeSince = null;
            if (expanded && pin.IsChecked != true && now >= keepUntil) expanded = false;
        }
        pointerInside = inside;
        UpdatePosition();
    }
    bool ViewerOwnsForeground()
    {
        nint foreground = GetForegroundWindow();
        nint viewer = new WindowInteropHelper(owner).Handle;
        const uint RootOwner = 3;
        return owner.IsActive || (foreground != 0 &&
            (foreground == handle || (viewer != 0 && GetAncestor(foreground, RootOwner) == viewer)));
    }
    void UpdatePosition(bool force = false)
    {
        if (disposed || handle == 0 || !isFullScreen || !owner.IsLoaded || owner.WindowState == WindowState.Minimized) return;
        var content = (FrameworkElement)owner.Content;
        if (content.ActualWidth <= 0) return;
        Point origin = content.PointToScreen(new Point(0, 0));
        var dpi = VisualTreeHelper.GetDpi(owner);
        anchorLeft = (int)Math.Round(origin.X);
        anchorWidth = (int)Math.Round(content.ActualWidth * dpi.DpiScaleX);
        int nextWidth = Math.Min((int)Math.Round(MaximumWidth * dpi.DpiScaleX), anchorWidth);
        int nextHeight = expanded ? (int)Math.Round(ExpandedHeight * dpi.DpiScaleY) : 2;
        int nextLeft = anchorLeft + (int)Math.Round(Math.Max(0, anchorWidth - nextWidth) * preferences.HorizontalPosition);
        int nextTop = (int)Math.Round(origin.Y);
        if (!force && (left, top, width, height) == (nextLeft, nextTop, nextWidth, nextHeight)) return;
        ((Border)window.Content).Child.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        window.Width = nextWidth / dpi.DpiScaleX; window.Height = nextHeight / dpi.DpiScaleY;
        window.UpdateLayout();
        if (SetWindowPos(handle, 0, nextLeft, nextTop, nextWidth, nextHeight, 0x214))
            (left, top, width, height) = (nextLeft, nextTop, nextWidth, nextHeight);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); timer.Tick -= Tick;
        owner.LocationChanged -= OwnerChanged; owner.SizeChanged -= OwnerChanged; owner.StateChanged -= OwnerChanged; owner.Closed -= OwnerClosed;
        ShortcutMenu.IsOpen = false; window.Close();
    }
}
