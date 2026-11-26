using System.IO;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly ComboBox recentConnections=new(){MinHeight=30};
    readonly CheckBox rememberConnections = new() { Content = "记录连接地址", ToolTip = "仅记录已授权连接的 IP 和端口。", IsChecked = true };
    readonly Button clearConnections=new(){Content="清空历史"};
    readonly TextBlock historyNotice = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    readonly Expander connectionHistoryPanel = new() { Header = "最近连接", Margin = new(0,8,0,0) };
    ConnectionHistoryStore? historyStore;
    readonly CancellationTokenSource historyLifetime=new();
    int historyGeneration;
    internal Task HistoryOperation{get;private set;}=Task.CompletedTask;
    void AddConnectionHistory(Panel panel)
    {
        var content=new StackPanel();content.Children.Add(recentConnections);
        var actions=new WrapPanel();actions.Children.Add(rememberConnections);actions.Children.Add(clearConnections);content.Children.Add(actions);content.Children.Add(historyNotice);connectionHistoryPanel.Content=content;panel.Children.Add(connectionHistoryPanel);
        recentConnections.SelectionChanged+=(_,_)=>
        {
            if(connectionRun is not null||recentConnections.SelectedItem is not ConnectionEndpoint endpoint)return;
            host.Text=endpoint.Address;port.Text=endpoint.Port.ToString();token.Clear();fingerprint.Clear();invitation.Clear();
            historyNotice.Text = "";
        };
        Loaded+=(_,_)=>HistoryOperation=LoadConnectionHistory();
        clearConnections.Click+=(_,_)=>HistoryOperation=ClearConnectionHistory();
        Closed+=(_,_)=>{historyLifetime.Cancel();_=HistoryOperation.ContinueWith(_=>historyLifetime.Dispose(),TaskScheduler.Default);};
    }
    void SetHistory(ConnectionEndpoint[] entries){recentConnections.ItemsSource=entries;recentConnections.SelectedIndex=-1;recentConnections.IsEnabled=entries.Length>0;connectionHistoryPanel.Header=$"最近连接（{entries.Length}）";}
    async Task LoadConnectionHistory()
    {
        if(historyStore is null&&!App.VerificationMode)historyStore=new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RelaxDesk","preferences","connections.json"));
        if(historyStore is null){rememberConnections.IsChecked=false;clearConnections.IsEnabled=false;return;}
        int generation=historyGeneration;
        try{var entries=await historyStore.Load(historyLifetime.Token);if(generation==historyGeneration)SetHistory(entries);}
        catch(OperationCanceledException){}
        catch(Exception e){HistoryError(e);}
    }
    void QueueConnectionHistory(string address,int number)
    {
        if(historyStore is null||rememberConnections.IsChecked!=true)return;
        HistoryOperation=RememberConnection(address,number);
    }
    async Task RememberConnection(string address,int number)
    {
        int generation=historyGeneration;
        try{var entries=await historyStore!.Remember(address,number,historyLifetime.Token);if(generation==historyGeneration)SetHistory(entries);}
        catch(OperationCanceledException){}
        catch(Exception e){HistoryError(e);}
    }
    async Task ClearConnectionHistory()
    {
        if(historyStore is null)return;++historyGeneration;clearConnections.IsEnabled=false;
        try{await historyStore.Clear(historyLifetime.Token);SetHistory([]);historyNotice.Text="连接历史已清空。";}
        catch(OperationCanceledException){}
        catch(Exception e){HistoryError(e);}
        finally{clearConnections.IsEnabled=true;}
    }
    void HistoryError(Exception error){historyNotice.Text="无法读取历史。可清空历史后重试。";_=Task.Run(()=>ViewerSessionLog.Record("connection-history-unavailable",error));}
}
