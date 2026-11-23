using System.Windows.Threading;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    DisplayViewerSession? displaySession;
    DispatcherTimer? displayDeadline;
    void StartDisplaySession(SessionClient connection)
    {
        EndDisplaySession();displaySession=connection.DisplayState is{} state?new(state):null;
        if(displaySession is not null)StartDisplayDeadline();
    }
    void StartDisplayDeadline()
    {
        displayDeadline?.Stop();var current=session;
        displayDeadline=new(){Interval=TimeSpan.FromSeconds(15)};
        displayDeadline.Tick+=(_,_)=>{displayDeadline?.Stop();if(ReferenceEquals(session,current)){status.Text="等待新屏幕画面超时，正在结束本次连接。";current?.Cancel();}};displayDeadline.Start();
    }
    void RefreshDisplayMenu()
    {
        sessionToolbar?.SetDisplays(displaySession?.Snapshot,displaySession?.Ready==true,SelectRemoteDisplay);
        bool inputReady=inputAuthorized&&viewportInputReady&&(displaySession is null||displaySession.Ready);
        sessionToolbar?.SetRemoteInputReady(inputReady);
        sessionToolbar?.SetTextInput(inputReady&&inputVersion==2?OpenTextInput:null);
    }
    void SelectRemoteDisplay(string id)
    {
        if(displaySession?.Ready!=true||session is null||session.IsCancellationRequested)return;
        if(id==displaySession.Snapshot.Selected){RefreshDisplayMenu();return;}
        ReleaseHeldInputs();CloseTextInput();ResetViewportPan();long generation=displaySession.Begin(id);
        if(!outgoing.TryWrite(new("display-select-v2",++sequence,DisplayId:id,DisplayGeneration:generation))){session.Cancel();return;}
        StartDisplayDeadline();RefreshDisplayMenu();status.Text="正在切换屏幕，等待新画面后恢复输入…";
    }
    void DisplayPresented(MediaFrame frame)
    {
        if(displaySession?.Presented(frame.DisplayGeneration)!=true)return;
        if(!outgoing.TryWrite(new("display-ready-v2",++sequence,DisplayGeneration:frame.DisplayGeneration))){session?.Cancel();return;}
        displayDeadline?.Stop();RefreshDisplayMenu();
    }
    void EndDisplaySession(){displayDeadline?.Stop();displayDeadline=null;displaySession=null;sessionToolbar?.SetDisplays(null,false,null);sessionToolbar?.SetRemoteInputReady(false);sessionToolbar?.SetTextInput(null);}
}
