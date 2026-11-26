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

internal sealed partial class SessionToolbar : Window, IDisposable
{
    const double MaximumWidth = 460;
    const double ExpandedHeight = 30;
    readonly Window owner;
    readonly Action? releaseInput;
    string mediaPath = "";
    (MediaCodec Codec, bool Hardware, bool Gpu)? lastMediaPath;
    internal string MediaPathText => mediaPath;
    public void SetMediaPath(MediaFrame frame, bool gpu)
    {
        var current = (frame.Codec, frame.HardwareEncoded, gpu);
        if (lastMediaPath == current) return;
        lastMediaPath = current;
        string encoding = frame.Codec == MediaCodec.DeflateBgra ? "图像兼容编码" : frame.HardwareEncoded ? "硬件编码" : "软件编码";
        mediaPath = encoding + " · " + (gpu ? "GPU 显示" : "CPU 显示");
        this.ToolTip = mediaPath;
    }
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    readonly ConnectionBarPreferences preferences;
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
    public Window Surface => this;
    internal bool BlocksPointer(double x, double y) => !disposed &&
        (ShortcutMenu.IsOpen || (this.IsVisible && x >= left && x < left + width && y >= top && y < top + height));
    internal Button FullScreenButton => full;
    internal ToggleButton PinButton => pin;
    internal Button MinimizeButton => minimize;
    internal ItemCollection RemoteKeyItems => remoteKeys.Items;
    Action? openFiles;
    public void SetFiles(Action? open, int recoveryCount = 0)
    {
        openFiles = open;
        FilesAction.IsEnabled = open is not null;
        FilesAction.Header = recoveryCount > 0 ? "文件续传…" : "文件传输…";
        FilesAction.ToolTip = recoveryCount > 0 ? $"有{recoveryCount}项中断任务，打开后确认继续" : "上传、下载与任务队列（需Host授权）";
    }

    internal MenuItem DisplaysAction=>displays;
    internal MenuItem ZoomAction=>zoom;
    Action<int>? setZoom;
    public void SetViewport(ViewportOptions options, Action<int> set)
    {
        setZoom = set;
        zoom.Header = options.Zoom == 0 ? "画面缩放 · 适应窗口" : $"画面缩放 · {options.Zoom}%";
        foreach (var item in zoom.Items.OfType<MenuItem>())
            if (item.Tag is int percent) item.IsChecked = options.Zoom == percent;
        customZoom.Text = (options.Zoom == 0 ? 100 : options.Zoom).ToString();
        customZoom.ToolTip = null;
    }

    void ChooseZoom(int percent)
    {
        ShortcutMenu.IsOpen = false;
        setZoom?.Invoke(percent);
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
    internal MenuItem VideoSettingsAction=>videoSettings;
    public void SetVideoSettings(VideoSettings? state,bool ready,bool pending,bool h264,Action<int,int> change)
    {
        videoSettings.Header=state is null?"视频设置（Host 未开放）":pending?"视频设置（等待应用确认…）":state.Generation==1_000_000?"视频设置（请重新连接后调整）":ready?$"视频设置 · {state.FramesPerSecond} FPS 上限":"视频设置（等待画面/短暂冷却）";
        videoSettings.IsEnabled=state is not null&&ready;videoSettings.Items.Clear();if(state is null)return;
        var fps=new MenuItem{Header="帧率上限"};foreach(int value in new[]{15,30,60,120,144})
        {var item=new MenuItem{Header=$"{value} FPS",IsCheckable=true,IsChecked=state.FramesPerSecond==value,IsEnabled=state.FramesPerSecond!=value};item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;change(value,state.Bitrate);};fps.Items.Add(item);}videoSettings.Items.Add(fps);
        var bitrate=new MenuItem{Header=h264?"H.264 目标码率":"当前图像兼容编码，码率不适用",IsEnabled=h264};foreach(int value in new[]{2,8,20,40,60,100})
        {var item=new MenuItem{Header=$"{value} Mbit/s",IsCheckable=true,IsChecked=state.Bitrate==value*1_000_000,IsEnabled=state.Bitrate!=value*1_000_000};item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;change(state.FramesPerSecond,value*1_000_000);};bitrate.Items.Add(item);}videoSettings.Items.Add(bitrate);
        videoSettings.Items.Add(new Separator());videoSettings.Items.Add(new MenuItem{Header="实际提交帧率与媒体流量见连接统计",IsEnabled=false});videoSettings.Items.Add(new MenuItem{Header="码率是目标参数；更高目标通常增加带宽",IsEnabled=false});
    }
    internal MenuItem StatisticsAction=>statistics;
    string mediaRate = "";
    string connectionTiming = "";
    internal string MediaRateText => mediaRate;
    internal string ConnectionTimingText => connectionTiming;
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
        mediaRate = rate is null ? "— FPS   ·   — Mbit/s" : $"{rate.FramesPerSecond:F1} FPS   ·   {rate.ApplicationMegabitsPerSecond:F2} Mbit/s";
        string rtt = sample.ControlRtt < 0 ? "—" : $"{sample.ControlRtt:F1} ms";
        connectionTiming = $"{sample.Transport}   ·   RTT {rtt}";
        statistics.ToolTip = mediaRate + "\n" + connectionTiming;
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
    Action? openFileClipboard;
    internal MenuItem FileClipboardAction=>fileClipboard;
    public void SetFileClipboard(Action? open,bool remoteOffer){openFileClipboard=open;fileClipboard.IsEnabled=open is not null;fileClipboard.Header=remoteOffer?"粘贴远端文件…":"文件剪贴板…";}
    internal MenuItem TextInputAction=>textInput;
    Action? openTextInput;
    public void SetTextInput(Action? open){openTextInput=open;textInput.IsEnabled=open is not null;}
    Action? disableClipboard;
    public void SetClipboard(bool enabled, Action? disable)
    {
        clipboard.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        clipboard.IsEnabled = enabled;
        disableClipboard = disable;
    }
    public SessionToolbar(Window owner, Action toggle, Action disconnect, Action<RemoteShortcut>? shortcut = null,Action? releaseInput=null, ConnectionBarPreferences? preferences=null)
    {
        this.owner = owner;
        this.preferences = preferences ?? new();
        this.releaseInput=releaseInput;
        InitializeComponent();
        Owner = owner;
        Width = MaximumWidth;
        Height = ExpandedHeight;
        full.Click += (_, _) => { toggle(); Reveal(); };
        full.ToolTip = "切换全屏和窗口（Ctrl+Alt+Break）";
        pin.Checked += (_, _) => { pin.ToolTip = "取消固定连接栏"; Reveal(); };
        pin.Unchecked += (_, _) => { pin.ToolTip = "固定连接栏"; Reveal(); };
        pin.ToolTip = "固定连接栏";
        pin.IsChecked = this.preferences.Pinned;
        pin.Checked += (_, _) => this.preferences.Pinned = true;
        pin.Unchecked += (_, _) => this.preferences.Pinned = false;
        minimize.Click += (_, _) => owner.WindowState = WindowState.Minimized;
        minimize.ToolTip = "最小化";
        DisconnectButton.Click += (_, _) => disconnect();

        textInput.Click += (_, _) => { ShortcutMenu.IsOpen = false; openTextInput?.Invoke(); };
        foreach (var item in remoteKeys.Items.OfType<MenuItem>())
        {
            var action = (RemoteShortcut)item.Tag;
            item.IsEnabled = shortcut is not null;
            item.Click += (_, _) => { ShortcutMenu.IsOpen = false; shortcut?.Invoke(action); Reveal(); };
        }
        foreach (var item in zoom.Items.OfType<MenuItem>())
        {
            if (item.Tag is not string value || !int.TryParse(value, out int percent)) continue;
            item.Tag = percent;
            item.Click += (_, _) => ChooseZoom(percent);
        }
        applyCustomZoom.Click += (_, _) =>
        {
            if (int.TryParse(customZoom.Text, out int percent) && percent is >= 25 and <= 400) ChooseZoom(percent);
            else customZoom.ToolTip = "请输入 25–400 之间的整数百分比";
        };
        FilesAction.Click += (_, _) => { ShortcutMenu.IsOpen = false; openFiles?.Invoke(); };
        ShortcutMenu.Opened += (_, _) => releaseInput?.Invoke();
        fileClipboard.Click += (_, _) => { ShortcutMenu.IsOpen = false; openFileClipboard?.Invoke(); };
        statistics.Click += (_, _) => { ShortcutMenu.IsOpen = false; openStatistics?.Invoke(); };
        clipboard.Click += (_, _) => { ShortcutMenu.IsOpen = false; disableClipboard?.Invoke(); };
        ShortcutsButton.Click += (_, _) => OpenSessionMenu();
        ShortcutMenu.Closed += (_, _) => keepUntil = DateTime.UtcNow.AddSeconds(1);
        dragRegion.MouseLeftButtonDown += (_, e) =>
        {
            if (GetCursorPos(out var point)) BeginDrag(point.X);
            dragRegion.CaptureMouse(); e.Handled = true;
        };
        dragRegion.MouseMove += (_, _) => { if (dragging && GetCursorPos(out var point)) MoveDrag(point.X); };
        dragRegion.MouseLeftButtonUp += (_, e) => { EndDrag(); e.Handled = true; };
        dragRegion.LostMouseCapture += (_, _) => dragging = false;
        this.SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle;
            SetStyle(handle, -20, GetStyle(handle, -20) | (nint)0x08000000);
            ((HwndSource)PresentationSource.FromVisual(this)).AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (message != 0x21) return 0;
                handled = true; return 3; // MA_NOACTIVATE: retain the first click, never MA_*ANDEAT.
            });
        };
        owner.LocationChanged += OwnerChanged; owner.SizeChanged += OwnerChanged; owner.StateChanged += OwnerChanged; owner.Closed += OwnerClosed;
        timer.Tick += Tick;
        MouseEnter += (_, _) => PointerRegionChanged?.Invoke();
        MouseLeave += (_, _) => PointerRegionChanged?.Invoke();
        ShortcutMenu.Opened += (_, _) => PointerRegionChanged?.Invoke();
        ShortcutMenu.Closed += (_, _) => PointerRegionChanged?.Invoke();
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
        lastMediaPath = null; mediaPath = "等待首帧"; ToolTip = null;
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
        if (value) { if (!this.IsVisible) this.Show(); Reveal(); }
        else { ShortcutMenu.IsOpen = false; expanded = false; this.Hide(); }
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
    internal Action? PointerRegionChanged;
    void Tick(object? sender, EventArgs e)
    {
        try { UpdatePointerRegion(); }
        finally { if (!disposed) PointerRegionChanged?.Invoke(); }
    }
    void UpdatePointerRegion()
    {
        if (disposed) return;
        RefreshConnectionInfo();
        if (!owner.IsVisible || owner.WindowState == WindowState.Minimized || !ViewerOwnsForeground())
        {
            EndDrag(); ShortcutMenu.IsOpen = false; this.Hide(); edgeSince = null;
            return;
        }
        if (!isFullScreen) { this.Hide(); return; }
        if (!this.IsVisible) { this.Show(); UpdatePosition(force: true); }
        if (ShortcutMenu.IsOpen || dragging || this.IsMouseCaptureWithin)
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
        bar.Visibility = expanded ? Visibility.Visible : Visibility.Hidden;
        this.Width = nextWidth / dpi.DpiScaleX; this.Height = nextHeight / dpi.DpiScaleY;
        this.UpdateLayout();
        if (SetWindowPos(handle, 0, nextLeft, nextTop, nextWidth, nextHeight, 0x214))
            (left, top, width, height) = (nextLeft, nextTop, nextWidth, nextHeight);
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; timer.Stop(); timer.Tick -= Tick;
        owner.LocationChanged -= OwnerChanged; owner.SizeChanged -= OwnerChanged; owner.StateChanged -= OwnerChanged; owner.Closed -= OwnerClosed;
        ShortcutMenu.IsOpen = false; this.Close();
    }
}
