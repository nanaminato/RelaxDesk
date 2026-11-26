namespace RelaxDesk.Core;

public static class PointerPosition
{
    public static bool TryNormalize(double x,double y,double clientWidth,double clientHeight,int videoWidth,int videoHeight,bool dragging,out int normalizedX,out int normalizedY)
    {
        normalizedX=normalizedY=0;
        if(!double.IsFinite(x)||!double.IsFinite(y)||!double.IsFinite(clientWidth)||!double.IsFinite(clientHeight)||clientWidth<=0||clientHeight<=0||videoWidth<=0||videoHeight<=0)return false;
        double scale=Math.Min(clientWidth/videoWidth,clientHeight/videoHeight);
        double left=(clientWidth-videoWidth*scale)/2,top=(clientHeight-videoHeight*scale)/2;
        double width=videoWidth*scale,height=videoHeight*scale;
        if(!dragging&&(x<left||x>=left+width||y<top||y>=top+height))return false;
        double px=(x-left)/Math.Max(1,width-1),py=(y-top)/Math.Max(1,height-1);
        if(!double.IsFinite(px)||!double.IsFinite(py))return false;
        normalizedX=(int)(Math.Clamp(px,0,1)*65535);normalizedY=(int)(Math.Clamp(py,0,1)*65535);return true;
    }
}
