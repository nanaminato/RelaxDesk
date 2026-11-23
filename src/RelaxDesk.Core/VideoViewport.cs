namespace RelaxDesk.Core;

// Zoom=0 fits the complete desktop. Otherwise Zoom is physical output pixels
// per remote pixel in percent, independent of either computer's logical DPI.
public sealed record ViewportOptions(int Zoom=0,double PanX=.5,double PanY=.5)
{
    public bool Valid=>(Zoom==0||Zoom is >=25 and <=400)&&double.IsFinite(PanX)&&double.IsFinite(PanY)&&PanX is >=0 and <=1&&PanY is >=0 and <=1;
}
public readonly record struct ViewportLayout(int SourceX,int SourceY,int SourceWidth,int SourceHeight,int DestinationX,int DestinationY,int DestinationWidth,int DestinationHeight)
{
    public double ScaleX=>(double)DestinationWidth/SourceWidth;
    public double ScaleY=>(double)DestinationHeight/SourceHeight;
    public double OriginX=>DestinationX-SourceX*ScaleX;
    public double OriginY=>DestinationY-SourceY*ScaleY;
    public bool TryNormalize(double x,double y,bool dragging,out int normalizedX,out int normalizedY)
    {
        normalizedX=normalizedY=0;
        if(!double.IsFinite(x)||!double.IsFinite(y)||SourceWidth<=0||SourceHeight<=0||DestinationWidth<=0||DestinationHeight<=0)return false;
        if(!dragging&&(x<DestinationX||y<DestinationY||x>=DestinationX+DestinationWidth||y>=DestinationY+DestinationHeight))return false;
        // Pixel-center mapping covers the first and last visible source pixels.
        double px=SourceX+Math.Clamp((x-DestinationX)/Math.Max(1,DestinationWidth-1),0,1)*(SourceWidth-1);
        double py=SourceY+Math.Clamp((y-DestinationY)/Math.Max(1,DestinationHeight-1),0,1)*(SourceHeight-1);
        normalizedX=(int)(px/Math.Max(1,VideoWidth-1)*65535);normalizedY=(int)(py/Math.Max(1,VideoHeight-1)*65535);return true;
    }
    public int VideoWidth{get;init;}
    public int VideoHeight{get;init;}
}
public static class VideoViewport
{
    static int Round(double value)=>(int)Math.Round(value,MidpointRounding.AwayFromZero);
    static (int Source,int Count,int Destination,int Size) Axis(int client,int video,double scale,double pan)
    {
        if(video*scale<=client){int size=Math.Clamp(Round(video*scale),1,client);return(0,video,(client-size)/2,size);}
        int count=Math.Clamp(Round(client/scale),1,video);return(Round((video-count)*pan),count,0,client);
    }
    public static ViewportLayout Calculate(int clientWidth,int clientHeight,int videoWidth,int videoHeight,ViewportOptions options)
    {
        if(!options.Valid||clientWidth is <1 or >8192||clientHeight is <1 or >8192||videoWidth is <1 or >65536||videoHeight is <1 or >65536||(long)videoWidth*videoHeight>8_294_400)throw new InvalidDataException("Invalid video viewport");
        double scale=options.Zoom==0?Math.Min((double)clientWidth/videoWidth,(double)clientHeight/videoHeight):options.Zoom/100d;
        var x=Axis(clientWidth,videoWidth,scale,options.PanX);var y=Axis(clientHeight,videoHeight,scale,options.PanY);
        return new(x.Source,y.Source,x.Count,y.Count,x.Destination,y.Destination,x.Size,y.Size){VideoWidth=videoWidth,VideoHeight=videoHeight};
    }
}
