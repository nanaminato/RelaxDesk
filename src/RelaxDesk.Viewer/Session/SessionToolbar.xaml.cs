using RelaxDesk.SharedUI;
using RelaxDesk.Core.Localization;
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
        string encoding = frame.Codec == MediaCodec.DeflateBgra ? Strings.Get("Ui.SessionToolbar.Text158") : frame.HardwareEncoded ? Strings.Get("Ui.SessionToolbar.Text393") : Strings.Get("Ui.SessionToolbar.Text469");
        mediaPath = encoding + " · " + (gpu ? Strings.Get("Viewer.DisplayModeGpu") : Strings.Get("Ui.SessionToolbar.Text022"));
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
    internal MenuItem DisplaysAction=>displays;
    internal MenuItem ZoomAction=>zoom;
    Action<int>? setZoom;
    public void SetViewport(ViewportOptions options, Action<int> set)
    {
        setZoom = set;
        LocalizedText.Bind(zoom,"Header",()=>options.Zoom == 0 ? Strings.Get("Ui.SessionToolbar.Text389") : Strings.Format("Ui.SessionToolbar.Text388",options.Zoom));
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
        displays.Items.Clear();displays.IsEnabled=state is not null;LocalizedText.Bind(displays,"Header",()=>state is null?Strings.Get("Ui.SessionToolbar.Text299"):state.Unavailable?(state.Allowed.Length>0?Strings.Get("Ui.SessionToolbar.Text300"):Strings.Get("Ui.SessionToolbar.Text301")):ready?Strings.Get("Ui.SessionToolbar.Text298"):Strings.Get("Ui.SessionToolbar.Text302"));
        if(state is null)return;
        foreach(string id in state.Allowed)
        {
            string label=id=="all"?Strings.Get("Ui.SessionToolbar.Text119"):state.Displays.Single(d=>d.Id==id).ToString();
            var item=new MenuItem{Header=label,IsCheckable=true,IsChecked=id==state.Selected,IsEnabled=ready,Tag=id};
            item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;select?.Invoke(id);};displays.Items.Add(item);
        }
    }
    internal MenuItem VideoSettingsAction=>videoSettings;
    public void SetVideoSettings(VideoSettings? state,bool ready,bool pending,bool h264,Action<int,int> change,bool automatic=false,Action<bool>? setAutomatic=null)
    {
        LocalizedText.Bind(videoSettings,"Header",()=>state is null?Strings.Get("Ui.SessionToolbar.Text445"):pending?Strings.Get("Ui.SessionToolbar.Text447"):state.Generation==1_000_000?Strings.Get("Ui.SessionToolbar.Text448"):ready?Strings.Format("Ui.SessionToolbar.Text443",(automatic?Strings.Get("Ui.SessionToolbar.Text430"):""),state.FramesPerSecond):Strings.Get("Ui.SessionToolbar.Text446"));
        videoSettings.IsEnabled=state is not null&&ready;videoSettings.Items.Clear();if(state is null)return;
        var fps=LocalizedText.Bind(new MenuItem{},"Header","Viewer.FrameRateLimit");foreach(int value in new[]{15,30,60,120,144})
        {var item=new MenuItem{Header=$"{value} FPS",IsCheckable=true,IsChecked=state.FramesPerSecond==value,IsEnabled=automatic||state.FramesPerSecond!=value};item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;change(value,state.Bitrate);};fps.Items.Add(item);}videoSettings.Items.Add(fps);
        var bitrate=LocalizedText.Bind(new MenuItem{Header=h264?Strings.Get("Ui.SessionToolbar.Text024"):Strings.Get("Ui.SessionToolbar.Text441"),IsEnabled=h264},"Header",()=>h264?Strings.Get("Ui.SessionToolbar.Text024"):Strings.Get("Ui.SessionToolbar.Text441"));foreach(int value in new[]{2,8,20,40,60,100})
        {var item=new MenuItem{Header=$"{value} Mbit/s",IsCheckable=true,IsChecked=state.Bitrate==value*1_000_000,IsEnabled=automatic||state.Bitrate!=value*1_000_000};item.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;change(state.FramesPerSecond,value*1_000_000);};bitrate.Items.Add(item);}videoSettings.Items.Add(bitrate);
        if(setAutomatic is not null)
        {
            var auto=new MenuItem{Header=RelaxDesk.Core.Localization.Strings.Get("Viewer.AutomaticQuality"),IsCheckable=true,IsChecked=automatic};
            auto.Click+=(_,_)=>{ShortcutMenu.IsOpen=false;setAutomatic(auto.IsChecked);};videoSettings.Items.Add(auto);
        }
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
    public void SetAudio(bool enabled,double volume,bool muted,Action<double,bool> adjust)
    {
        audio.IsEnabled=enabled;LocalizedText.Bind(audio,"Header",()=>!enabled?Strings.Get("Ui.SessionToolbar.Text165"):muted?Strings.Get("Ui.SessionToolbar.Text163"):Strings.Format("Ui.SessionToolbar.Text162",volume*100));audio.Items.Clear();if(!enabled)return;
        var mute=LocalizedText.Bind(new MenuItem{IsCheckable=true,IsChecked=muted},"Header","Ui.SessionToolbar.Text504");mute.Click+=(_,_)=>adjust(volume,!muted);audio.Items.Add(mute);
        foreach(int percent in new[]{0,25,50,75,100}){var item=LocalizedText.Bind(new MenuItem{IsCheckable=true,IsChecked=Math.Abs(volume-percent/100d)<0.001},"Header","Ui.SessionToolbar.Text505",percent);item.Click+=(_,_)=>adjust(percent/100d,muted);audio.Items.Add(item);}
    }
    internal MenuItem TextInputAction=>textInput;
    Action? openTextInput;
    public void SetTextInput(Action? open){openTextInput=open;textInput.IsEnabled=open is not null;}
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
        LocalizedText.Bind(full,"ToolTip","Ui.SessionToolbar.Text128");
        pin.Checked += (_, _) => { LocalizedText.Bind(pin,"ToolTip","Ui.SessionToolbar.Text147"); Reveal(); };
        pin.Unchecked += (_, _) => { LocalizedText.Bind(pin,"ToolTip","Ui.SessionToolbar.Text157"); Reveal(); };
        LocalizedText.Bind(pin,"ToolTip","Ui.SessionToolbar.Text157");
        pin.IsChecked = this.preferences.Pinned;
        pin.Checked += (_, _) => this.preferences.Pinned = true;
        pin.Unchecked += (_, _) => this.preferences.Pinned = false;
        minimize.Click += (_, _) => owner.WindowState = WindowState.Minimized;
        LocalizedText.Bind(minimize,"ToolTip","Ui.SessionToolbar.Text306");
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
            else LocalizedText.Bind(customZoom,"ToolTip","Ui.SessionToolbar.Text459");
        };
        ShortcutMenu.Opened += (_, _) => releaseInput?.Invoke();
        statistics.Click += (_, _) => { ShortcutMenu.IsOpen = false; openStatistics?.Invoke(); };
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
        lastMediaPath = null; mediaPath = Strings.Get("Ui.SessionToolbar.Text410"); ToolTip = null;
        SetEndpoint(address);
        SetFullScreen(fullScreen);
        timer.Start();
    }
    public void SetFullScreen(bool value)
    {
        isFullScreen = value;
        full.Content = value ? "\uE923" : "\uE922";
        LocalizedText.Bind(full,"ToolTip",()=>value ? Strings.Get("Ui.SessionToolbar.Text473") : Strings.Get("Ui.SessionToolbar.Text116"));
        System.Windows.Automation.AutomationProperties.SetName(full, value ? Strings.Get("Ui.SessionToolbar.Text473") : Strings.Get("Ui.SessionToolbar.Text116"));
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
