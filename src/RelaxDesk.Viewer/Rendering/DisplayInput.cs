using RelaxDesk.SharedUI;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    DisplayViewerSession? displaySession;
    DispatcherTimer? displayDeadline;
    bool gpuViewportActive;
    void ApplyDisplayTopology(DisplaySessionInfo state,CpuBitmapRenderer cpu)
    {
        if(displaySession?.Generation!=state.Generation)return;
        ReleaseHeldInputs();CloseTextInput();ResetViewportPan();
        if(state.Unavailable)
        {
            displayDeadline?.Stop();cpu.Clear();EndViewport();if(nativeView is not null)nativeView.Visibility=Visibility.Collapsed;
            displayUnavailable.Visibility=Visibility.Visible;LocalizedText.Bind(status,"Text","Ui.DisplayInput.Text188");
        }
        else
        {
            displayUnavailable.Visibility=Visibility.Collapsed;if(gpuViewportActive&&nativeView is not null)nativeView.Visibility=Visibility.Visible;
            if(!displaySession.Ready)StartDisplayDeadline();else displayDeadline?.Stop();LocalizedText.Bind(status,"Text","Ui.DisplayInput.Text189");
            if(!outgoing.TryWrite(new("display-configured-v2",++sequence,DisplayGeneration:state.Generation)))session?.Cancel();
            if(!outgoing.TryWrite(new("keyframe-v2",++sequence)))session?.Cancel();
        }
        RefreshDisplayMenu();
    }
    void StartDisplaySession(SessionClient connection)
    {
        EndDisplaySession();displaySession=connection.DisplayState is{} state?new(state,connection.DisplayVersion==DisplayProtocol.TopologyVersion):null;
        if(displaySession is not null)StartDisplayDeadline();
    }
    void StartDisplayDeadline()
    {
        displayDeadline?.Stop();var current=session;
        displayDeadline=new(){Interval=TimeSpan.FromSeconds(15)};
        displayDeadline.Tick+=(_,_)=>{displayDeadline?.Stop();if(ReferenceEquals(session,current)){LocalizedText.Bind(status,"Text","Ui.DisplayInput.Text404");current?.Cancel();}};displayDeadline.Start();
    }
    void RefreshDisplayMenu()
    {
        sessionToolbar?.SetDisplays(displaySession?.Snapshot,displaySession?.CanSelect==true,SelectRemoteDisplay);
        bool inputReady=inputAuthorized&&viewportInputReady&&(displaySession is null||displaySession.Ready);
        sessionToolbar?.SetRemoteInputReady(inputReady);
        sessionToolbar?.SetTextInput(inputReady&&inputVersion==2?OpenTextInput:null);
    }
    void SelectRemoteDisplay(string id)
    {
        if(displaySession?.CanSelect!=true||session is null||session.IsCancellationRequested)return;
        if(id==displaySession.Snapshot.Selected){RefreshDisplayMenu();return;}
        ReleaseHeldInputs();CloseTextInput();ResetViewportPan();long generation=displaySession.Begin(id);
        if(!outgoing.TryWrite(new("display-select-v2",++sequence,DisplayId:id,DisplayGeneration:generation))){session.Cancel();return;}
        StartDisplayDeadline();RefreshDisplayMenu();LocalizedText.Bind(status,"Text","Ui.DisplayInput.Text349");
    }
    void DisplayPresented(MediaFrame frame)
    {
        if(displaySession?.Presented(frame.DisplayGeneration)!=true)return;
        if(!outgoing.TryWrite(new("display-ready-v2",++sequence,DisplayGeneration:frame.DisplayGeneration))){session?.Cancel();return;}
        displayDeadline?.Stop();RefreshDisplayMenu();
    }
    void EndDisplaySession(){displayDeadline?.Stop();displayDeadline=null;displaySession=null;displayUnavailable.Visibility=Visibility.Collapsed;sessionToolbar?.SetDisplays(null,false,null);sessionToolbar?.SetRemoteInputReady(false);sessionToolbar?.SetTextInput(null);}
}
