using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    int inputVersion;
    bool inputAuthorized;
    long droppedPointerMoves;
    readonly PressedRemoteInputs richHeld=new();
    void SendRich(RemoteInput input) => SendRich(input, discardOnBacklog: false);
    void SendPointerMove(int x, int y) => Send(0, false, x, y, discardOnBacklog: true);

    void SendRich(RemoteInput input, bool discardOnBacklog)
    {
        if(smoke is not null||!inputAuthorized||inputVersion!=InputProtocol.Version||session is null||session.IsCancellationRequested)return;
        if(displaySession is not null&&!displaySession.Ready)return;
        if(!viewportInputReady)return;
        if(!InputProtocol.Valid(input)){session.Cancel();return;}
        if(!outgoing.TryWrite(InputProtocol.Encode(input,++sequence,displaySession?.Generation??0)))
        {
            if (discardOnBacklog) Interlocked.Increment(ref droppedPointerMoves);
            else session.Cancel();
            return;
        }
        richHeld.Update(input);
    }
    void SendRawKey(int key,nint flags,bool up)
    {
        if(inputVersion!=InputProtocol.Version){Send(RemoteKeyboardKeys.Resolve(key,flags),up);return;}
        try{foreach(var input in InputProtocol.KeyMessage(key,(long)flags,up))SendRich(input);}
        catch(System.IO.InvalidDataException){session?.Cancel();}
    }
    bool BitmapPosition(Point point,bool dragging,bool standaloneMove=false)
    {
        var screen = videoPanel.PointToScreen(point);
        if (IsLocalPointer(screen.X, screen.Y))
        {
            ReleaseHeldInputs();
            return false;
        }
        var dpi=System.Windows.Media.VisualTreeHelper.GetDpi(videoPanel);
        if(!TryViewportLayout(out var layout)||!layout.TryNormalize(point.X*dpi.DpiScaleX,point.Y*dpi.DpiScaleY,dragging,out int x,out int y))return false;
        if (standaloneMove)
            SendPointerMove(x, y);
        else
            Send(0,false,x,y);
        return true;
    }
    void ConfigurePointerInput()
    {
        image.MouseMove+=(_,e)=>{if(IsActive)BitmapPosition(e.GetPosition(videoPanel),Mouse.Captured==image,standaloneMove:true);};
        image.MouseDown+=(_,e)=>
        {
            if(!IsActive||!BitmapPosition(e.GetPosition(videoPanel),false))return;
            image.Focus();Mouse.Capture(image);SendPointerButton(e.ChangedButton,false);e.Handled=true;
        };
        image.MouseUp+=(_,e)=>
        {
            if((Mouse.Captured==image||image.IsMouseOver) && BitmapPosition(e.GetPosition(videoPanel),Mouse.Captured==image))SendPointerButton(e.ChangedButton,true);
            if(e.LeftButton==MouseButtonState.Released&&e.RightButton==MouseButtonState.Released&&e.MiddleButton==MouseButtonState.Released&&e.XButton1==MouseButtonState.Released&&e.XButton2==MouseButtonState.Released && Mouse.Captured == image)Mouse.Capture(null);
            e.Handled=true;
        };
        image.LostMouseCapture+=(_,_)=>ReleaseMouseButtons();
        image.MouseWheel+=(_,e)=>
        {
            if (IsActive) SendBitmapWheel(e.GetPosition(videoPanel), e.Delta);
            e.Handled = true;
        };
    }
    void SendBitmapWheel(Point point, int delta)
    {
        if (delta == 0 || !BitmapPosition(point, dragging:false)) return;
        if (inputVersion == InputProtocol.Version) SendRich(new("wheel", WheelY:delta));
        else Send(delta > 0 ? 258 : 259, false);
    }
    void SendPointerButton(MouseButton button,bool up)
    {
        int number=button switch{MouseButton.Left=>1,MouseButton.Right=>2,MouseButton.Middle=>3,MouseButton.XButton1=>4,MouseButton.XButton2=>5,_=>0};
        if(number==0)return;if(inputVersion==2)SendRich(new("button",Button:number,Up:up));else if(number<=2)Send(number==1?256:257,up);
    }
    void ResetInputState()
    {inputAuthorized=false;inputVersion=0;richHeld.ReleaseAll();heldInputs.ReleaseAll();CloseTextInput();}
}
