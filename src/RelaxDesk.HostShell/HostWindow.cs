using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RelaxDesk.Core;
namespace RelaxDesk.HostShell;

internal sealed partial class HostWindow:Window
{
    internal readonly HostProcess Host=new();
    readonly TextBox ip=new(),port=new(){Text="48200"};
    readonly TextBlock state=new(){Text="尚未共享"},detail=new(){Text="选择本机地址，开始等待连接请求。",TextWrapping=TextWrapping.Wrap},peer=new(),color=new(){Text="HDR 自动转为 SDR · 全程加密传输",Foreground=Brushes.SlateGray};
    readonly Button start=new(){Content="开始共享"},stop=new(){Content="停止共享",IsEnabled=false},allow=new(){Content="允许本次连接"},deny=new(){Content="拒绝"};
    readonly Border request=new(){Visibility=Visibility.Collapsed};
    readonly CheckBox clipboardPermission=new(){Content="允许双向文本剪贴板（可读取两端新复制的文本，最大64KiB）",IsChecked=false,Visibility=Visibility.Collapsed,Margin=new(0,10,0,0)};
    readonly Button revokeClipboard=new(){Content="停止剪贴板同步",Visibility=Visibility.Collapsed};
    readonly TextBox logs=new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=180,FontFamily=new("Consolas"),FontSize=11};
    readonly string application;
    bool closing;
    public HostWindow(string? hostApplication=null)
    {
        application=hostApplication??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","Host","RelaxDesk.Host.exe"));
        Title="RelaxDesk Host "+BuildIdentity.Display;Width=1040;Height=780;MinWidth=840;MinHeight=650;FontSize=14;FontFamily=new("Segoe UI, Microsoft YaHei UI");Background=Brush("#F1F5FA");
        RelaxDesk.Ui.ModernTheme.Apply(this);
        var root=new Grid{Background=Background};root.ColumnDefinitions.Add(new(){Width=new(220)});root.ColumnDefinitions.Add(new());
        var nav=new StackPanel{Margin=new(24,32,20,24)};nav.Children.Add(Text("RelaxDesk",26,true,Brushes.White));nav.Children.Add(Text("HOST  /  共享端",12,false,Brush("#8DA2C0")));nav.Children.Add(new Border{Height=1,Background=Brush("#2D405B"),Margin=new(0,26,0,24)});nav.Children.Add(Text("共享这台电脑",15,true,Brushes.White));nav.Children.Add(Text("Windows 局域网远程协助",12,false,Brush("#8DA2C0")));nav.Children.Add(new TextBlock{Text="只有你批准的连接\n才能查看屏幕与操作输入。",Foreground=Brush("#A8B8D0"),Margin=new(0,46,0,0),TextWrapping=TextWrapping.Wrap,LineHeight=23});
        var sidebar=new Border{Background=Brush("#142238"),Child=nav};root.Children.Add(sidebar);
        var content=new StackPanel{Margin=new(32,26,32,24)};Grid.SetColumn(content,1);
        content.Children.Add(Text("共享这台电脑",28,true));content.Children.Add(new TextBlock{Text=BuildIdentity.Display,FontSize=11,Foreground=Brushes.SlateGray,Margin=new(0,5,0,18),TextWrapping=TextWrapping.Wrap});
        var statusContent=new StackPanel();state.FontSize=19;state.FontWeight=FontWeights.SemiBold;state.Foreground=Brush("#17243A");statusContent.Children.Add(state);detail.Margin=new(0,8,0,0);detail.Foreground=Brushes.SlateGray;statusContent.Children.Add(detail);content.Children.Add(Card(statusContent));
        var settings=new StackPanel();settings.Children.Add(Text("连接信息",17,true));settings.Children.Add(Text("Viewer 只需填写下面的 IP 和端口",12,false,Brushes.SlateGray));
        var fields=new Grid{Margin=new(0,12,0,0)};fields.ColumnDefinitions.Add(new());fields.ColumnDefinitions.Add(new(){Width=new(135)});
        var ipField=new StackPanel{Margin=new(0,0,16,0)};ipField.Children.Add(Text("本机局域网 IP",12));ipField.Children.Add(ip);fields.Children.Add(ipField);
        var portField=new StackPanel();portField.Children.Add(Text("端口",12));portField.Children.Add(port);Grid.SetColumn(portField,1);fields.Children.Add(portField);settings.Children.Add(fields);
        var addresses=Addresses();ip.Text=addresses.FirstOrDefault()??"127.0.0.1";
        if(addresses.Length>1){var options=new WrapPanel();foreach(string address in addresses){var pick=new Button{Content=address,Padding=new(10,5,10,5),FontSize=12};pick.Click+=(_,_)=>{if(!Host.Running)ip.Text=address;};options.Children.Add(pick);}settings.Children.Add(options);}
        AddFileSettings(settings);
        var actions=new WrapPanel();start.Style=(Style)Resources["PrimaryButton"];stop.Style=(Style)Resources["DangerButton"];
        actions.Children.Add(start);actions.Children.Add(stop);actions.Children.Add(revokeClipboard);revokeClipboard.Click+=async(_,_)=>await StopClipboard();var copy=new Button{Content="复制地址"};copy.Click+=(_,_)=>Clipboard.SetText(ip.Text+":"+port.Text);actions.Children.Add(copy);settings.Children.Add(actions);color.Margin=new(4,12,0,0);color.TextWrapping=TextWrapping.Wrap;settings.Children.Add(color);var settingsCard=Card(settings);
        var consent=new StackPanel();consent.Children.Add(Text("有人请求连接",19,true));peer.FontSize=16;peer.Margin=new(0,10,0,8);consent.Children.Add(peer);consent.Children.Add(new TextBlock{Text="允许后，对方可以查看桌面并操作键盘与鼠标。请确认是你期望的连接。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray});var decision=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,14,0,0)};allow.Style=(Style)Resources["PrimaryButton"];deny.Style=(Style)Resources["DangerButton"];decision.Children.Add(allow);decision.Children.Add(deny);consent.Children.Add(decision);request.Child=consent;request.Background=Brush("#EAF2FF");request.CornerRadius=new(12);request.Padding=new(22);request.Margin=new(0,0,0,16);content.Children.Add(request);content.Children.Add(settingsCard);
        consent.Children.Insert(3,clipboardPermission);consent.Children.Insert(4,filesPermission);consent.Children.Insert(5,filesScopeLabel);actions.Children.Add(revokeFiles);
        content.Children.Add(new Expander{Header="诊断与日志",Content=logs,Margin=new(0,4,0,0)});
        var scroll=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetColumn(scroll,1);root.Children.Add(scroll);Content=root;
        start.Click+=async(_,_)=>await StartSharing();stop.Click+=async(_,_)=>await StopSharing();allow.Click+=async(_,_)=>await Decide(true);deny.Click+=async(_,_)=>await Decide(false);
        Host.Changed+=(kind,value)=>Dispatcher.BeginInvoke(()=>Changed(kind,value));Host.Diagnostic+=line=>Dispatcher.BeginInvoke(()=>{logs.Text=string.Join(Environment.NewLine,Host.Snapshot());if(line.StartsWith("producer_error")||line.StartsWith("Session ended:")||line.StartsWith("clipboard_")||line.StartsWith("file_transfer_disabled"))detail.Text=line;if(line.StartsWith("clipboard_disabled"))revokeClipboard.Visibility=Visibility.Collapsed;if(line.StartsWith("file_transfer_disabled"))revokeFiles.Visibility=Visibility.Collapsed;});
        Closing+=async(_,e)=>{if(!Host.Running)return;e.Cancel=true;if(closing)return;closing=true;await StopSharing();Close();};
    }
    static SolidColorBrush Brush(string value)=>new((Color)ColorConverter.ConvertFromString(value));
    static TextBlock Text(string value,double size=14,bool bold=false,Brush? foreground=null)=>new(){Text=value,FontSize=size,FontWeight=bold?FontWeights.SemiBold:FontWeights.Normal,Foreground=foreground??Brush("#17243A"),TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,4)};
    static Border Card(UIElement child)=>new(){Child=child,Background=Brushes.White,CornerRadius=new(12),Padding=new(22),Margin=new(0,0,0,16)};
    static string[] Addresses()=>NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up&&n.NetworkInterfaceType!=NetworkInterfaceType.Loopback).OrderByDescending(n=>n.GetIPProperties().GatewayAddresses.Count>0).SelectMany(n=>n.GetIPProperties().UnicastAddresses).Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>a.Address.ToString()).Distinct().ToArray();
    internal bool RequestVisible=>request.Visibility==Visibility.Visible;
    internal bool ClipboardChoiceVisible => clipboardPermission.Visibility == Visibility.Visible;
    internal bool ClipboardAllowed { get => clipboardPermission.IsChecked == true; set => clipboardPermission.IsChecked = value; }
    internal async Task StopClipboard(){await Host.RevokeClipboard();revokeClipboard.Visibility=Visibility.Collapsed;detail.Text="已停止文本剪贴板同步；重新开启需要新的连接授权。";}
    internal string Status=>state.Text;
    internal async Task StartSharing(string? address=null,int? number=null,string backend="dxgi")
    {
        try{if(address is not null)ip.Text=address;if(number.HasValue)port.Text=number.Value.ToString();if(!int.TryParse(port.Text,out int selected))throw new ArgumentException("端口应为 1–65535");ip.IsEnabled=port.IsEnabled=start.IsEnabled=false;SetFileConfigurationEnabled(false);stop.IsEnabled=true;state.Text="正在启动";await Host.Start(application,ip.Text.Trim(),selected,backend,selectedFileRoot);}
        catch(Exception e){state.Text="无法开始共享";detail.Text=e.Message;ip.IsEnabled=port.IsEnabled=start.IsEnabled=true;SetFileConfigurationEnabled(true);stop.IsEnabled=false;}
    }
    internal async Task Decide(bool accepted){allow.IsEnabled=deny.IsEnabled=false;bool clipboard=accepted&&clipboardPermission.IsChecked==true;bool files=accepted&&filesPermission.IsChecked==true&&selectedFileRoot is not null;await Host.Decide(accepted,clipboard,files);revokeClipboard.Visibility=clipboard?Visibility.Visible:Visibility.Collapsed;revokeFiles.Visibility=files?Visibility.Visible:Visibility.Collapsed;request.Visibility=Visibility.Collapsed;}
    internal async Task StopSharing(){stop.IsEnabled=false;try{await Host.Stop();}catch(Exception e){detail.Text=e.Message;}}
    void Changed(string kind,string? value)
    {
        switch(kind){
            case "listening":state.Text="等待连接";detail.Text=$"Viewer 可连接 {ip.Text}:{port.Text}，收到请求后由你确认。";break;
            case "approval-request":peer.Text="请求来源  "+value;request.Visibility=Visibility.Visible;allow.IsEnabled=deny.IsEnabled=true;clipboardPermission.IsChecked=false;clipboardPermission.Visibility=Host.ClipboardRequested?Visibility.Visible:Visibility.Collapsed;ShowFileRequest();state.Text="等待你的授权";detail.Text="还未共享桌面或开放输入。";break;
            case "approved":state.Text="正在建立共享";detail.Text="已允许本次连接，正在准备屏幕。";request.Visibility=Visibility.Collapsed;break;
            case "denied":state.Text="已拒绝连接";detail.Text="没有共享桌面。";break;
            case "streaming":state.Text="正在共享";detail.Text="对方可查看并操作当前桌面。你可以随时停止共享。";break;
            case "color":
                try
                {
                    using var d=JsonDocument.Parse(value!); var info=d.RootElement;
                    string transform=info.GetProperty("transform").GetString()??"SDR";
                    color.Text=transform=="SDR"&&info.GetProperty("hdr_active").GetBoolean()
                        ? "HDR 已开启，但当前是系统转换的 8 位图像，亮部精度可能受限"
                        : (transform=="SDR"?"SDR 桌面":"已启用 HDR → SDR · "+transform)+"  ·  SDR 白位 "+info.GetProperty("sdr_white_nits").GetSingle().ToString("F0")+" nits";
                    if(!info.GetProperty("white_level_known").GetBoolean()) color.Text+="（系统未提供白位，使用默认值）";
                }
                catch(JsonException){}break;
            case "ended":case "stopped":if(state.Text!="已拒绝连接")state.Text=kind=="stopped"?"已停止共享":"会话已结束";request.Visibility=Visibility.Collapsed;revokeClipboard.Visibility=Visibility.Collapsed;revokeFiles.Visibility=Visibility.Collapsed;ip.IsEnabled=port.IsEnabled=start.IsEnabled=true;SetFileConfigurationEnabled(true);stop.IsEnabled=false;break;
        }
    }
}
