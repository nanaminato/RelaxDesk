using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

internal sealed class TextInputWindow : Window
{
    internal readonly TextBox Editor=new(){AcceptsReturn=true,AcceptsTab=true,TextWrapping=TextWrapping.Wrap,MinHeight=90,MaxLength=InputProtocol.MaximumTextUnits,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    internal readonly Button Submit=new(){Content="发送已完成的文字",IsEnabled=false,Margin=new(0,12,0,0)};
    readonly TextBlock hint=new(){Text="使用本机输入法完成文字后，点击发送。候选文字不会发送；Enter 只用于输入文字。若远程应用不接受文字，请关闭此窗口，在远程画面使用远端输入法。",TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,12)};
    readonly Func<string,bool> send;
    bool composing,closed;
    internal bool Composing=>composing;
    public TextInputWindow(Window owner,Func<string,bool> send)
    {
        this.send=send;Owner=owner;Title="发送文字到远程电脑";Width=480;Height=360;MinWidth=360;MinHeight=320;ShowInTaskbar=false;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        RelaxDesk.Ui.ModernTheme.Apply(this);
        var panel=new Grid{Margin=new(20)};panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});panel.RowDefinitions.Add(new(){Height=GridLength.Auto});
        Grid.SetRow(Editor,1);Grid.SetRow(Submit,2);panel.Children.Add(hint);panel.Children.Add(Editor);panel.Children.Add(Submit);Content=panel;
        InputMethod.SetIsInputMethodEnabled(Editor,true);
        TextCompositionManager.AddPreviewTextInputStartHandler(Editor,(_,_)=>{composing=true;Refresh();});
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Editor,(_,_)=>{composing=true;Refresh();});
        TextCompositionManager.AddPreviewTextInputHandler(Editor,(_,_)=>{composing=false;Dispatcher.BeginInvoke(Refresh);});
        Editor.TextChanged+=(_,_)=>Refresh();
        Submit.Click+=(_,_)=>
        {
            if(closed||composing||!InputProtocol.TextValid(Editor.Text))return;
            string committed=Editor.Text;Editor.Clear();Refresh();
            if(!send(committed))hint.Text="会话已结束或未获授权。文字未发送，请重新连接后输入。";
            Editor.Focus();
        };
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape&&!composing){Close();e.Handled=true;}};
        Loaded+=(_,_)=>Editor.Focus();
        Closed+=(_,_)=>{closed=true;Editor.Clear();};
    }
    void Refresh()=>Submit.IsEnabled=!closed&&!composing&&InputProtocol.TextValid(Editor.Text);
}

public sealed partial class ViewerWindow
{
    TextInputWindow? textInput;
    void OpenTextInput()
    {
        if(!inputAuthorized||inputVersion!=2||session is null||session.IsCancellationRequested)return;
        ReleaseHeldInputs();if(textInput is not null){textInput.Activate();return;}
        var current=session;
        var dialog=new TextInputWindow(this,text=>
        {
            if(!ReferenceEquals(session,current)||current.IsCancellationRequested||!inputAuthorized||inputVersion!=2)return false;
            SendRich(new("text",Text:text));return !current.IsCancellationRequested;
        });
        textInput=dialog;dialog.Closed+=(_,_)=>{if(ReferenceEquals(textInput,dialog))textInput=null;};dialog.Show();
    }
    void CloseTextInput(){var previous=textInput;textInput=null;previous?.Close();}
}
