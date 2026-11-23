using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly CheckBox requestClipboardImage=new(){Content=new TextBlock{Text="请求双向图片剪贴板（8MiB / 1600万像素，需Host允许）",TextWrapping=System.Windows.TextWrapping.Wrap},IsChecked=false,Margin=new(0,2,0,4)};
    readonly CheckBox requestClipboardHtml=new(){Content=new TextBlock{Text="请求双向 HTML 剪贴板（512KiB，需Host允许）",TextWrapping=System.Windows.TextWrapping.Wrap},IsChecked=false,Margin=new(0,2,0,4)};
    ClipboardContentSession? richClipboardSession;
    void StartRichClipboard(SessionClient connection,AccessGate gate,CancellationToken ct)
    {
        if(connection.ClipboardData is null)return;
        if(smoke?.RichClipboardTest==true&&(!System.Net.IPAddress.TryParse(smoke.Host,out var address)||!System.Net.IPAddress.IsLoopback(address)))throw new System.IO.InvalidDataException("Clipboard test provider is loopback-only");
        var active=new ClipboardContentSession(connection.ClipboardData,()=>smoke?.RichClipboardTest==true?richClipboardTestProvider=new ViewerContentClipboard():new WindowsTextClipboard(),gate,ct,message=>ViewerSessionLog.Record(message));
        richClipboardSession=active;
        _=active.Completion.ContinueWith(_=>Dispatcher.BeginInvoke(()=>
        {
            if(!ReferenceEquals(richClipboardSession,active)||ct.IsCancellationRequested)return;
            DisableClipboard();
        }),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
    async Task EndRichClipboard(){var previous=richClipboardSession;richClipboardSession=null;if(previous is not null)await previous.DisposeAsync();}
}
