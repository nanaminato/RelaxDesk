using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using RelaxDesk.Core;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    ViewportOptions viewport=new(100,0,0);
    bool viewportInputReady,updatingViewport;
    int viewportVideoWidth,viewportVideoHeight;
    ViewportOptions CurrentViewport=>Volatile.Read(ref viewport);
    void ConfigureViewport()
    {
        videoPanel.SizeChanged+=(_,_)=>ViewportBoundsChanged();DpiChanged+=(_,_)=>ViewportBoundsChanged();
        horizontalPan.ValueChanged+=(_,_)=>PanViewport();verticalPan.ValueChanged+=(_,_)=>PanViewport();
    }
    void SetViewportZoom(int zoom)
    {
        var next=new ViewportOptions(zoom);if(!next.Valid)return;
        ReleaseHeldInputs();viewportInputReady=false;Volatile.Write(ref viewport,next);ApplyViewport();
        sessionToolbar?.SetViewport(next,SetViewportZoom);RefreshDisplayMenu();
        SavePreferences();
    }
    void PanViewport()
    {
        if(updatingViewport)return;var current=CurrentViewport;
        var next=current with{PanX=horizontalPan.Maximum>0?horizontalPan.Value/horizontalPan.Maximum:.5,PanY=verticalPan.Maximum>0?verticalPan.Value/verticalPan.Maximum:.5};
        ReleaseHeldInputs();viewportInputReady=false;Volatile.Write(ref viewport,next);ApplyViewport();RefreshDisplayMenu();
    }
    void ResetViewportPan()
    {viewportInputReady=false;Volatile.Write(ref viewport,CurrentViewport with{PanX=0,PanY=0});ApplyViewport();}
    void ViewportBoundsChanged()
    {
        if(updatingViewport)return;ReleaseHeldInputs();viewportInputReady=false;
        Volatile.Write(ref viewport,CurrentViewport with{});ApplyViewport();RefreshDisplayMenu();
    }
    void SetViewportVideoSize(int width,int height)
    {
        if(viewportVideoWidth==width&&viewportVideoHeight==height)return;
        viewportVideoWidth=width;viewportVideoHeight=height;ResetViewportPan();
    }
    bool TryViewportLayout(out ViewportLayout layout)
    {
        layout=default;var dpi=VisualTreeHelper.GetDpi(videoPanel);int width=(int)Math.Round(videoPanel.ActualWidth*dpi.DpiScaleX),height=(int)Math.Round(videoPanel.ActualHeight*dpi.DpiScaleY);
        if(width is <1 or >8192||height is <1 or >8192||viewportVideoWidth<=0||viewportVideoHeight<=0)return false;
        layout=VideoViewport.Calculate(width,height,viewportVideoWidth,viewportVideoHeight,CurrentViewport);return true;
    }
    void ApplyViewport()
    {
        if(updatingViewport)return;updatingViewport=true;
        try
        {
            if(!TryViewportLayout(out var view))return;
            var dpi=VisualTreeHelper.GetDpi(videoPanel);var options=CurrentViewport;
            bool horizontal=false,vertical=false;
            if(options.Zoom!=0)
                for(int i=0;i<3;i++)
                {
                    horizontal=viewportVideoWidth*options.Zoom/100d>(renderPanel.ActualWidth-(vertical?verticalPan.Width:0))*dpi.DpiScaleX;
                    vertical=viewportVideoHeight*options.Zoom/100d>(renderPanel.ActualHeight-(horizontal?horizontalPan.Height:0))*dpi.DpiScaleY;
                }
            horizontalPan.Visibility=horizontal?Visibility.Visible:Visibility.Collapsed;
            verticalPan.Visibility=vertical?Visibility.Visible:Visibility.Collapsed;
            void Bar(ScrollBar bar,int source,int count,int total){bar.Maximum=total-count;bar.ViewportSize=count;bar.SmallChange=1;bar.LargeChange=Math.Max(1,count*.9);bar.Value=source;}
            Bar(horizontalPan,view.SourceX,view.SourceWidth,viewportVideoWidth);Bar(verticalPan,view.SourceY,view.SourceHeight,viewportVideoHeight);
            double originX=options.Zoom==100?Math.Round(view.OriginX):view.OriginX;
            double originY=options.Zoom==100?Math.Round(view.OriginY):view.OriginY;
            image.Width=viewportVideoWidth*view.ScaleX/dpi.DpiScaleX;image.Height=viewportVideoHeight*view.ScaleY/dpi.DpiScaleY;
            image.RenderTransform=new TranslateTransform(originX/dpi.DpiScaleX,originY/dpi.DpiScaleY);
            image.Clip=new RectangleGeometry(new Rect(view.SourceX*view.ScaleX/dpi.DpiScaleX,view.SourceY*view.ScaleY/dpi.DpiScaleY,view.DestinationWidth/dpi.DpiScaleX,view.DestinationHeight/dpi.DpiScaleY));
            RenderOptions.SetBitmapScalingMode(image,options.Zoom==100?BitmapScalingMode.NearestNeighbor:BitmapScalingMode.HighQuality);
            nativeView?.SetViewport(options);
            if((nativeView is null||nativeView.Visibility!=Visibility.Visible)&&image.Source is System.Windows.Media.Imaging.BitmapSource bitmap&&bitmap.PixelWidth==viewportVideoWidth&&bitmap.PixelHeight==viewportVideoHeight)
                _=Dispatcher.BeginInvoke(()=>ViewportDrawn(options),DispatcherPriority.Render);
        }
        finally{updatingViewport=false;}
    }
    void ViewportDrawn(ViewportOptions options)
    {
        if(viewportVideoWidth<=0||viewportVideoHeight<=0||!ReferenceEquals(CurrentViewport,options))return;
        viewportInputReady=true;RefreshDisplayMenu();
    }
    void EndViewport()
    {
        viewportInputReady=false;viewportVideoWidth=viewportVideoHeight=0;
        updatingViewport=true;horizontalPan.Visibility=verticalPan.Visibility=Visibility.Collapsed;updatingViewport=false;
    }
}
