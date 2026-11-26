using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RelaxDesk.Windows;
using RelaxDesk.Core;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly ComboBox networkMode = new() { ItemsSource = new[] { "Auto", "TCP", "UDP" }, SelectedIndex = 0 };
    readonly ComboBox fileRateLimit = new() { ItemsSource = new[] { "20 MiB/s（默认）", "4 MiB/s", "1 MiB/s", "256 KiB/s" }, SelectedIndex = 0 };
    readonly TextBox loginName = new() { MaxLength = PasswordVerifier.MaximumUserNameLength };
    string SessionEndpoint => host.Text;

    readonly Button optionsButton = new() { Content = "显示选项 ▾", MinWidth = 96 };
    readonly TabControl connectionOptions = new() { Visibility = Visibility.Collapsed };
    readonly CheckBox startFullScreen = new() { Content = "全屏启动", IsChecked = false };
    readonly Border basicConnection = new();
    readonly TabItem generalTab = new() { Header = "常规" };
    StackPanel signInForm = null!;
    bool optionsExpanded, desktopView;
    Rect connectionBounds;
    WindowState connectionWindowState;
    readonly ConnectionBarPreferences connectionBarPreferences = new();

    void BuildInterface()
    {
        Title = "RelaxDesk 远程桌面连接"; Width = 560; Height = 470; MinWidth = 540; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"); FontSize = 13;
        Background = new SolidColorBrush(Color.FromRgb(240, 240, 240));
        ViewerTheme.Apply(this);
        var root = new DockPanel();
        var banner = new Grid { Height = 86, Background = Brushes.White };
        banner.ColumnDefinitions.Add(new() { Width = new GridLength(76) }); banner.ColumnDefinitions.Add(new());
        var monitor = new Viewbox { Width = 42, Height = 38, Child = DesktopSymbol(), VerticalAlignment = VerticalAlignment.Center };
        banner.Children.Add(monitor);
        var heading = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        heading.Children.Add(new TextBlock { Text = "RelaxDesk", FontSize = 24, Foreground = new SolidColorBrush(Color.FromRgb(20, 62, 105)), ToolTip = BuildIdentity.Display });
        Grid.SetColumn(heading, 1); banner.Children.Add(heading);
        windowToolbar = banner; DockPanel.SetDock(banner, Dock.Top); root.Children.Add(banner);

        var bottom = new StackPanel();
        var commands = new DockPanel { Margin = new Thickness(14, 9, 14, 10) };
        optionsButton.Click += (_, _) => SetOptionsExpanded(!optionsExpanded);
        commands.Children.Add(optionsButton);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        connect.Content = "连接(_C)"; connect.IsDefault = true; connect.MinWidth = 88;
        var cancel = new Button { Content = "取消", MinWidth = 82 };
        cancel.Click += (_, _) => { if (connectionRun is not null) StopConnection(); else Close(); };
        disconnect.Content = "取消连接"; disconnect.Visibility = Visibility.Collapsed;
        // Keep cancellation available while the settings panel is disabled.
        disconnect.IsEnabledChanged += (_, _) => disconnect.Visibility = disconnect.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
        actions.Children.Add(disconnect); actions.Children.Add(connect); actions.Children.Add(cancel); commands.Children.Add(actions);
        bottom.Children.Add(new Separator()); bottom.Children.Add(commands);
        status.Text = ""; status.TextWrapping = TextWrapping.Wrap; status.Foreground = Brushes.DimGray; status.FontSize = 11;
        var notices = new StackPanel { Margin = new Thickness(18, 0, 18, 10) }; notices.Children.Add(status); BuildErrorDetails(notices); bottom.Children.Add(notices);
        connectionFooter = bottom; DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);

        image.Focusable = true; BuildViewport();
        signInForm = new StackPanel { Margin = new Thickness(20, 14, 20, 8) };
        var address = new Grid(); address.ColumnDefinitions.Add(new()); address.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); address.ColumnDefinitions.Add(new() { Width = new GridLength(68) });
        address.Children.Add(host); var portLabel = new TextBlock { Text = "端口", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 7, 0) }; Grid.SetColumn(portLabel, 1); address.Children.Add(portLabel); Grid.SetColumn(port, 2); address.Children.Add(port);
        signInForm.Children.Add(ConnectionField("计算机(_C):", address, host));
        signInForm.Children.Add(ConnectionField("用户名(_U):", loginName, loginName));
        signInForm.Children.Add(ConnectionField("密码(_P):", token, token));
        rememberCredentials.Margin = new Thickness(88, 4, 0, 7); signInForm.Children.Add(rememberCredentials);
        signInForm.Children.Add(new TextBlock { Text = "首次连接需核对指纹并在受控端批准。", Foreground = Brushes.DimGray, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) });
        var history = new StackPanel(); AddConnectionHistory(history); signInForm.Children.Add(history);
        basicConnection.Child = signInForm;
        connectionOptions.Items.Add(generalTab);

        var display = OptionsPanel();
        display.Children.Add(startFullScreen);
        gpuRendering.Content = "GPU 显示";
        gpuRendering.ToolTip = "GPU 显示不可用时自动使用兼容模式。";
        display.Children.Add(gpuRendering);
        display.Children.Add(requestTopology);
        connectionOptions.Items.Add(new TabItem { Header = "显示", Content = display });
        var resources = OptionsPanel();
        resources.Children.Add(requestClipboard); resources.Children.Add(requestClipboardImage); resources.Children.Add(requestClipboardHtml); resources.Children.Add(requestFileClipboard); resources.Children.Add(requestAudio);
        connectionOptions.Items.Add(new TabItem { Header = "本地资源", Content = resources });
        var advanced = OptionsPanel();
        advanced.Children.Add(ConnectionField("媒体传输:", networkMode, networkMode));
        advanced.Children.Add(ConnectionField("文件限速:", fileRateLimit, fileRateLimit));
        var resetPin = new Button { Content = "清除受控端证书记录…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
        resetPin.Click += (_, _) =>
        {
            if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out var address) || !int.TryParse(port.Text, out int number) || number is < 1 or > 65535) return;
            if (MessageBox.Show(this, "清除后，下次登录前需要重新核对受控端证书指纹。", "清除证书记录", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try { PasswordLoginSettings.SaveServerPin(new System.Net.IPEndPoint(address, number).ToString(), null); }
            catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException) { status.Text = "无法清除证书记录。"; }
        };
        advanced.Children.Add(resetPin); connectionOptions.Items.Add(new TabItem { Header = "高级", Content = advanced });
        var settings = new Grid(); settings.Children.Add(basicConnection); settings.Children.Add(connectionOptions);
        connectionCard.Child = settings; connectionCard.Margin = new Thickness(10, 8, 10, 0);
        var scroll = new ScrollViewer { Content = connectionCard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        connectionOverlay = scroll; renderPanel.Children.Add(scroll); root.Children.Add(renderPanel); Content = root;
        ConfigureSavedCredentials();
    }

    static FrameworkElement DesktopSymbol()
    {
        var symbol = new Grid { Width = 44, Height = 38 };
        symbol.Children.Add(new Border { Height = 28, VerticalAlignment = VerticalAlignment.Top, Background = new SolidColorBrush(Color.FromRgb(27, 104, 170)), BorderBrush = new SolidColorBrush(Color.FromRgb(49, 68, 86)), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(2), Child = new TextBlock { Text = "↗", Foreground = Brushes.White, FontSize = 23, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        symbol.Children.Add(new Border { Width = 3, Height = 7, Background = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3) });
        symbol.Children.Add(new Border { Width = 24, Height = 3, Background = Brushes.SlateGray, VerticalAlignment = VerticalAlignment.Bottom });
        return symbol;
    }

    static Grid ConnectionField(string text, FrameworkElement control, UIElement target)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(88) }); row.ColumnDefinitions.Add(new());
        row.Children.Add(new Label { Content = text, Target = target, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1); row.Children.Add(control); return row;
    }
    static StackPanel OptionsPanel() => new() { Margin = new Thickness(18) };
    internal void SetOptionsExpanded(bool expanded)
    {
        if (expanded == optionsExpanded) return;
        if (expanded) { basicConnection.Child = null; generalTab.Content = signInForm; }
        else { generalTab.Content = null; basicConnection.Child = signInForm; }
        optionsExpanded = expanded;
        connectionOptions.SelectedIndex = 0;
        connectionOptions.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        basicConnection.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        optionsButton.Content = expanded ? "隐藏选项 ▴" : "显示选项 ▾";
        Height = expanded ? 610 : 470;
    }
    FrameworkElement connectionOverlay = null!;
    FrameworkElement windowToolbar = null!, connectionFooter = null!;
    SessionToolbar? sessionToolbar;
    internal SessionToolbar? Toolbar => sessionToolbar;
    internal bool DesktopFillsClient => windowToolbar.Visibility == Visibility.Collapsed && connectionFooter.Visibility == Visibility.Collapsed;
    internal void AddToolbarTestSurface(NativeVideoHost surface) => renderPanel.Children.Add(surface);
    internal bool ConnectionPanelVisible => connectionOverlay.Visibility == Visibility.Visible;
    internal bool BasicConnectionReady => host.IsVisible && port.IsVisible && pinnedConnection.IsChecked != true;
    internal void SetConnectionText(string text) => invitation.Text = text;
    Task<bool> ConfirmPasswordServer(string pin, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        bool accepted = MessageBox.Show(this,
            $"请将下方指纹与受控端「登录设置」中的 TLS 指纹核对。\n\n{pin}\n\n仅在完全一致时继续，密码将在确认后发送。",
            "核对受控端身份", MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(accepted);
    }
    void ApplyInvitation()
    {
        if (string.IsNullOrWhiteSpace(invitation.Text)) return;
        var code = RelaxDesk.Core.ConnectionInvitation.Parse(invitation.Text);
        if (smoke is not null && (code.Host != smoke.Host || code.Port != smoke.Port)) throw new InvalidOperationException("Smoke invitation endpoint mismatch");
        host.Text = code.Host; port.Text = code.Port.ToString(); fingerprint.Text = code.Fingerprint; token.Password = code.Token;
        pinnedConnection.IsChecked = true;
    }
    internal void ShowConnectedView(bool connected)
    {
        if (connected && !desktopView)
        {
            connectionBounds = new Rect(Left, Top, Width, Height);
            connectionWindowState = WindowState;
            MinWidth = 680; MinHeight = 480;
            if (WindowState == WindowState.Normal) { Width = 1100; Height = 750; }
        }
        connectionOverlay.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        windowToolbar.Visibility = connectionFooter.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (connected)
        {
            sessionToolbar ??= new SessionToolbar(this, ToggleFullScreen, () => disconnect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)),
                session is null ? null : SendShortcut,ReleaseHeldInputs, connectionBarPreferences);
            sessionToolbar.FilesAction.Visibility = smoke?.FileTransfer == true || smoke?.ReconnectTest == true ? Visibility.Visible : Visibility.Collapsed;
            sessionToolbar.FileClipboardAction.Visibility = fileClipboardVerificationFactory is not null ? Visibility.Visible : Visibility.Collapsed;
            sessionToolbar.SetSession(SessionEndpoint, fullScreen);
            sessionToolbar.SetClipboard(sessionPermissions is{} clipboardGate&&ClipboardFormats.Granted(clipboardGate).Any, DisableClipboard);
            sessionToolbar.SetRemoteShortcuts(inputAuthorized);
            sessionToolbar.SetTextInput(inputAuthorized&&inputVersion==2?OpenTextInput:null);
            RefreshFileToolbar();RefreshFileClipboardToolbar();RefreshDisplayMenu();RefreshAudioToolbar();RefreshStatisticsToolbar();RefreshVideoSettingsMenu();sessionToolbar.SetViewport(CurrentViewport,SetViewportZoom);
        }
        else
        {
            localConnectionKeys.Clear();
            sessionToolbar?.Dispose(); sessionToolbar = null;
            if (fullScreen) ToggleFullScreen();
            if (desktopView)
            {
                MinWidth = 540; MinHeight = 450;
                WindowState = connectionWindowState;
                if (WindowState == WindowState.Normal) { Width = connectionBounds.Width; Height = connectionBounds.Height; Left = connectionBounds.Left; Top = connectionBounds.Top; }
            }
        }
        desktopView = connected;
        renderPanel.Background = connected ? Brushes.Black : Background;
        UpdateViewTitle();
        if (connected) { invitation.Clear(); token.Clear(); }
    }
    void UpdateViewTitle()
    {
        Title = desktopView ? previewing ? "RelaxDesk — 本机预览" : $"{host.Text} - RelaxDesk 远程桌面" : "RelaxDesk 远程桌面连接";
        if (desktopView && !previewing && fileClipboardStatus.Length > 0)
            Title += " · " + fileClipboardStatus;
    }
    internal void StartLocalPreview()
    {
        if (session is not null) return;
        StopLocalPreview(); previewing = true; disconnect.IsEnabled = true; ShowConnectedView(true);
        int frame = 0;
        void Draw()
        {
            image.Source = BitmapSource.Create(960, 540, 96, 96, PixelFormats.Bgr32, null, CodecVerification.Pattern(960, 540, frame++ % 200), 960 * 4);
            SetViewportVideoSize(960,540);
            status.Text = "本机预览 · 无网络连接 · 输入不会发往其他电脑";
        }
        Draw(); previewTimer = new() { Interval = TimeSpan.FromMilliseconds(100) }; previewTimer.Tick += (_, _) => Draw(); previewTimer.Start();
    }
    internal void StopLocalPreview()
    {
        if (!previewing) return;
        previewTimer?.Stop(); previewTimer = null; previewing = false;EndViewport(); image.Source = null; disconnect.IsEnabled = false; ShowConnectedView(false); status.Text = "预览已结束，准备连接";
    }
    bool IsLocalPointer(double x, double y) => sessionToolbar?.BlocksPointer(x, y) == true;
    internal void ToggleFullScreen()
    {
        ReleaseHeldInputs();
        SetFullScreenPresentation(!fullScreen);
        sessionToolbar?.SetFullScreen(fullScreen);
    }
}
