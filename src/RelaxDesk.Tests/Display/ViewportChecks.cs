using RelaxDesk.Core;

internal static class ViewportChecks
{
    public static int Run()
    {
        int count=0;void Check(bool value){if(!value)throw new Exception($"Viewport check {count+1} failed");count++;}
        var fit=VideoViewport.Calculate(800,600,1920,1080,new());Check(fit.SourceWidth==1920&&fit.SourceHeight==1080&&fit.DestinationY==75&&fit.DestinationHeight==450);
        Check(!fit.TryNormalize(10,10,false,out _,out _));Check(fit.TryNormalize(0,75,false,out int x,out int y)&&x==0&&y==0);Check(fit.TryNormalize(799,524,false,out x,out y)&&x==65535&&y==65535);
        var one=VideoViewport.Calculate(800,600,1920,1080,new(100,0,1));Check(one.SourceX==0&&one.SourceY==480&&one.SourceWidth==800&&one.SourceHeight==600&&one.ScaleX==1&&one.ScaleY==1);
        Check(one.TryNormalize(799,599,false,out x,out y)&&x==(int)(799d/1919*65535)&&y==65535);Check(one.TryNormalize(-100,10000,true,out x,out y)&&x==0&&y==65535);
        var two=VideoViewport.Calculate(800,600,1920,1080,new(200,1,0));Check(two.SourceX==1520&&two.SourceY==0&&two.SourceWidth==400&&two.SourceHeight==300&&two.ScaleX==2&&two.ScaleY==2);
        Check(two.TryNormalize(0,0,false,out x,out y)&&x==(int)(1520d/1919*65535)&&y==0);
        foreach(double dpi in new[]{1d,1.25,1.5,2})
        {
            var view=VideoViewport.Calculate((int)(800*dpi),(int)(600*dpi),3840,2160,new(100));Check(view.ScaleX==1&&view.ScaleY==1);
            Check(view.TryNormalize(400*dpi,300*dpi,false,out x,out y)&&x is >32000 and <34000&&y is >32000 and <34000);
        }
        var random=new Random(4871);
        for(int i=0;i<300;i++)
        {
            int w=random.Next(1,8193),h=random.Next(1,8193),vw=random.Next(1,3841),vh=random.Next(1,2161);int zoom=i%8==0?0:random.Next(25,401);
            var layout=VideoViewport.Calculate(w,h,vw,vh,new(zoom,random.NextDouble(),random.NextDouble()));
            Check(layout.SourceX>=0&&layout.SourceY>=0&&layout.SourceX+layout.SourceWidth<=vw&&layout.SourceY+layout.SourceHeight<=vh&&layout.DestinationX>=0&&layout.DestinationY>=0&&layout.DestinationX+layout.DestinationWidth<=w&&layout.DestinationY+layout.DestinationHeight<=h);
            Check(layout.TryNormalize(layout.DestinationX,layout.DestinationY,false,out x,out y)&&x is >=0 and <=65535&&y is >=0 and <=65535);
        }
        foreach(var invalid in new[]{new ViewportOptions(24),new(401),new(100,double.NaN),new(100,0,double.PositiveInfinity),new(100,-1),new(100,0,2)})
        {try{VideoViewport.Calculate(800,600,1920,1080,invalid);throw new Exception("Invalid viewport accepted");}catch(InvalidDataException){count++;}}
        Check(!one.TryNormalize(double.NaN,0,true,out _,out _));Console.WriteLine($"PASS {count} viewport fit/physical-1:1/zoom/pan/DPI/clip/input/bounds checks; no Windows input or rendering");return count;
    }
}
