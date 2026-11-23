using RelaxDesk.Core;
namespace RelaxDesk.Windows;

public static class CaptureRefreshContracts
{
    sealed class FakeCapture:ICaptureBackend
    {
        public string Name=>"isolated-capture";public bool Fresh,Fail;public int CachedReads;
        readonly CapturedFrame frame=new(1,1,[1,2,3,4],new(0,1,[],[],0,0,false,0,0,0,0,0,0,[]));
        public CapturedFrame? Capture(int timeoutMilliseconds=100){if(Fail)throw new IOException("Injected capture access loss");return Fresh?frame:null;}
        public CapturedFrame? CachedFrame(){CachedReads++;return frame;}
        public byte[] ReadPixels(CapturedFrame frame)=>frame.Bgra!;public void Dispose(){}
    }
    public static void Run()
    {
        using var capture=new FakeCapture();int count=0;void Check(bool value){if(!value)throw new IOException("Capture refresh contract");count++;}
        Check(CaptureFrameRefresh.Read(capture,false) is null&&capture.CachedReads==0);
        Check(CaptureFrameRefresh.Read(capture,true)?.Bgra is [1,2,3,4]&&capture.CachedReads==1);
        capture.Fresh=true;Check(CaptureFrameRefresh.Read(capture,true) is not null&&capture.CachedReads==1);
        capture.Fail=true;bool failed=false;try{CaptureFrameRefresh.Read(capture,true);}catch(IOException){failed=true;}Check(failed&&capture.CachedReads==1);
        Console.WriteLine($"PASS {count} capture refresh contracts: normal idle stays idle, explicit refresh uses owned cached image, fresh frames win and access errors never fall back; fake capture, no physical desktop claim");
    }
}
