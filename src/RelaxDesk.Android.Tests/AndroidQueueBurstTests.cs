using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;

internal static class AndroidQueueBurstTests
{
    sealed class Clock : TimeProvider
    {
        public long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
    }
    public static async Task<int> Run()
    {
        int checks = 0;
        void Check(bool pass, string why) { if (!pass) throw new Exception(why); checks++; }
        byte[] key = [0,0,0,1,0x67,0x42,0,0,1,0x68,0xce,0,0,0,1,0x65,0x88];
        MediaFrame Frame(int n, bool idr=false) => new(MediaCodec.H264,n,n*166666L,640,360,idr,true,1000,2000,[],idr?key:[0,0,1,0x41,0x9a],1,1);
        var clock = new Clock(); int requests = 0;
        var telemetry = new AndroidPipelineTelemetry();
        var queue = new BoundedAvcQueue(6,()=>requests++,telemetry.RecordDrop,clock:clock);
        // Six compressed frames arriving in one burst remain a continuous GOP.
        for(int n=1;n<=6;n++) Check(queue.TryEnqueue(Frame(n,n==1),out _),"A bounded burst must not destroy continuity");
        for(int n=1;n<=6;n++) Check(queue.TryDequeue(out var item)&&item.Frame.Sequence==n,"Decode burst in order without skipping dependencies");
        Check(requests==0 && queue.TotalDropped==0,"No IDR storm for a short burst");
        queue.TryEnqueue(Frame(7,true),out _);queue.TryEnqueue(Frame(8),out _);
        clock.Milliseconds=50;
        Check(!queue.TryDequeue(out _),"Never submit a dependency chain resident for 50 ms");
        Check(requests==1 && queue.DroppedExpired==2 && telemetry.Sample().DroppedExpired==2,"Expire and account for the whole chain");
        Check(!queue.TryEnqueue(Frame(9),out var reason)&&reason==VideoDropReason.AwaitingKeyFrame,"Expiry cannot resume at a dependent frame");
        Check(queue.TryEnqueue(Frame(10,true),out _) && queue.TryDequeue(out var recovered) && recovered.Frame.Sequence==10,"Fresh IDR resumes decoding");
        queue.TryEnqueue(Frame(11,true),out _);clock.Milliseconds=101;
        Check(!queue.TryEnqueue(Frame(12),out _) && requests==2,"Expiry discovered by the receiver requests recovery");
        queue.TryEnqueue(Frame(13,true),out _);clock.Milliseconds=151;
        using var stop = new CancellationTokenSource();
        var pending = queue.DequeueAsync(stop.Token).AsTask();
        Check(!pending.IsCompleted && requests==3,"Async consumer also refuses expired frames");
        queue.TryEnqueue(Frame(14,true),out _);
        Check((await pending.WaitAsync(TimeSpan.FromSeconds(2)))?.Frame.Sequence==14,"Async consumer wakes for replacement IDR");
        queue.TryEnqueue(Frame(15,true),out _);clock.Milliseconds=201;
        Check(queue.TryEnqueue(Frame(16,true),out _) && requests==3,"An incoming IDR itself satisfies expiry recovery");
        clock.Milliseconds=251;
        Check(!queue.TryEnqueue(Frame(16,true),out _) && requests==4,"Expiry still requests an IDR when the next received frame is stale");
        Check(queue.TotalDropped==telemetry.Sample().TotalDropped,"Drop totals include every expiry and dependency discard");
        queue.Complete();
        Check(await queue.DequeueAsync(default) is null,"Completion remains bounded after expired queue signals");

        var diagnostics = new AndroidPipelineDiagnostics(clock);
        diagnostics.Received(Frame(1,true),1);clock.Milliseconds+=20;diagnostics.Received(Frame(2),2);
        clock.Milliseconds++;diagnostics.Received(Frame(3),3);
        diagnostics.Submitted();diagnostics.Output();
        for(int n=0;n<2048;n++)diagnostics.Add("test_stage",n);
        diagnostics.Add("test_stage",double.NaN);
        var sample=diagnostics.Snapshot();
        Check(sample.Received==3 && sample.Submitted==1 && sample.Output==1 && sample.PeakQueueDepth==3 && sample.BurstArrivals==1,"Separate receiver, decoder and burst counters");
        var stage=sample.Stages.Single(s=>s.Name=="test_stage");
        Check(stage.Samples==1024 && stage.P50Milliseconds==1535 && stage.P95Milliseconds==1996,"Bounded recent stage percentiles reject invalid values");
        Check(sample.Stages.Single(s=>s.Name=="arrival_excess").P95Milliseconds>3,"Arrival excess compares intervals without synchronizing clock origins");
        return checks;
    }
}
