using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class ViewportContracts
{
    [StructLayout(LayoutKind.Sequential)]struct NativeLayout{public int SourceX,SourceY,SourceWidth,SourceHeight,DestinationX,DestinationY,DestinationWidth,DestinationHeight;}
    [DllImport("RelaxDesk.Native.dll",CallingConvention=CallingConvention.Cdecl)]static extern int rd_viewport_calculate(int width,int height,int videoWidth,int videoHeight,int zoom,double x,double y,out NativeLayout layout);
    public static void Run()
    {
        int count=0;var random=new Random(3701);
        for(int i=0;i<500;i++)
        {
            int w=random.Next(1,8193),h=random.Next(1,8193),vw=random.Next(1,3841),vh=random.Next(1,2161),zoom=i%7==0?0:random.Next(25,401);double x=i%3==0?0:i%3==1?1:.5,y=i%4/3d;
            var expected=VideoViewport.Calculate(w,h,vw,vh,new(zoom,x,y));NativeMethods.Check(rd_viewport_calculate(w,h,vw,vh,zoom,x,y,out var actual));
            if(expected.SourceX!=actual.SourceX||expected.SourceY!=actual.SourceY||expected.SourceWidth!=actual.SourceWidth||expected.SourceHeight!=actual.SourceHeight||expected.DestinationX!=actual.DestinationX||expected.DestinationY!=actual.DestinationY||expected.DestinationWidth!=actual.DestinationWidth||expected.DestinationHeight!=actual.DestinationHeight)throw new IOException("Managed/native viewport mismatch");count++;
        }
        foreach(var (zoom,x,y) in new[]{(24,0d,0d),(401,0d,0d),(100,double.NaN,0d),(100,0d,-1d)})
        {if(rd_viewport_calculate(800,600,1920,1080,zoom,x,y,out _)>=0)throw new IOException("Native viewport accepted invalid options");count++;}
        Console.WriteLine($"PASS {count} managed/native viewport parity and rejection contracts; no desktop capture, GPU presentation or input");
    }
}
