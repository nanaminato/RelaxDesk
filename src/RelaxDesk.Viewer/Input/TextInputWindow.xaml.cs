using RelaxDesk.SharedUI;
using System.Windows;
using System.Windows.Input;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

internal sealed partial class TextInputWindow : Window
{
    readonly Func<string,bool> send;
    bool composing,closed;
    internal bool Composing=>composing;
    public TextInputWindow(Window owner,Func<string,bool> send)
    {
        this.send = send;
        InitializeComponent();
        Owner = owner;
        InputMethod.SetIsInputMethodEnabled(Editor,true);
        TextCompositionManager.AddPreviewTextInputStartHandler(Editor,(_,_)=>{composing=true;Refresh();});
        TextCompositionManager.AddPreviewTextInputUpdateHandler(Editor,(_,_)=>{composing=true;Refresh();});
        TextCompositionManager.AddPreviewTextInputHandler(Editor,(_,_)=>{composing=false;Dispatcher.BeginInvoke(Refresh);});
        Editor.TextChanged+=(_,_)=>Refresh();
        Submit.Click+=(_,_)=>
        {
            if(closed||composing||!InputProtocol.TextValid(Editor.Text))return;
            string committed=Editor.Text;Editor.Clear();Refresh();
            if(!send(committed))LocalizedText.Bind(hint,"Text","Ui.TextInputWindow.Text090");
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
        if(displaySession is not null&&!displaySession.Ready)return;
        if(!viewportInputReady||!inputAuthorized||inputVersion!=2||session is null||session.IsCancellationRequested)return;
        ReleaseHeldInputs();if(textInput is not null){textInput.Activate();return;}
        var current=session;
        var dialog=new TextInputWindow(this,text=>
        {
            if(!ReferenceEquals(session,current)||current.IsCancellationRequested||!viewportInputReady||!inputAuthorized||inputVersion!=2||displaySession is not null&&!displaySession.Ready)return false;
            SendRich(new("text",Text:text));return !current.IsCancellationRequested;
        });
        textInput=dialog;dialog.Closed+=(_,_)=>{if(ReferenceEquals(textInput,dialog))textInput=null;};dialog.Show();
    }
    void CloseTextInput(){var previous=textInput;textInput=null;previous?.Close();}
}
