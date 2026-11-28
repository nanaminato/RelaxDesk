using RelaxDesk.Core;
using RelaxDesk.Mobile.Media;

internal static class AndroidVideoSettingsTests
{
    sealed class Clock : TimeProvider
    {
        public long Seconds;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => Seconds;
    }
    public static int Run()
    {
        int checks = 0;
        void Check(bool pass) { if (!pass) throw new Exception("Android video settings contract " + checks); checks++; }
        void Reject(Action action) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception("Invalid settings accepted"); }
        var clock = new Clock();
        List<Packet> sent = [];
        var settings = new AndroidVideoSettings(VideoSettingsProtocol.Default, new(60,12_000_000), sent.Add, clock);
        MediaFrame Frame(int generation) => new(MediaCodec.H264,generation,0,640,360,true,false,0,0,[],[],1,generation);
        void Ack(int fps,int bitrate,int generation) => settings.Receive(new("video-settings-state-v2",VideoVersion:1,VideoSettings:new(fps,bitrate,generation)));
        settings.Pump(); Check(sent.Count==0);
        settings.Presented(Frame(1));settings.Pump();
        Check(sent.Count==1 && sent[^1].VideoSettings==new VideoSettings(60,12_000_000,1));
        settings.Configure(new(15,2_000_000));settings.Configure(new(24,4_000_000));
        settings.Presented(Frame(2));settings.Pump();Check(sent.Count==1);
        Ack(60,12_000_000,2);settings.Pump();Check(sent.Count==1);
        clock.Seconds++;settings.Pump();Check(sent.Count==2 && sent[^1].VideoSettings==new VideoSettings(24,4_000_000,2));
        Check(!settings.Accept(Frame(1)));
        Ack(24,4_000_000,3);clock.Seconds++;settings.Pump();Check(settings.Pending);
        settings.Presented(Frame(3));settings.Pump();Check(!settings.Pending);
        Reject(()=>Ack(24,4_000_000,3));Reject(()=>settings.Accept(Frame(8)));
        Reject(()=>settings.Configure(new(0,8_000_000)));Reject(()=>settings.Configure(new(30,100_000_001)));
        sent.Clear();clock.Seconds=0;
        settings=new(VideoSettingsProtocol.Default,new(60,16_000_000,true),sent.Add,clock);
        settings.Presented(Frame(1));settings.Pump();Check(sent.Count==0);
        void Sample(int second,double rtt)=>settings.Observe(new(second,rtt,second*30,second*1_000_000L,0,0));
        Sample(0,10);Sample(5,100);Sample(10,100);
        Check(sent.Count==1 && sent[^1].VideoSettings==new VideoSettings(22,6_000_000,1));
        Ack(22,6_000_000,2);settings.Presented(Frame(2));clock.Seconds=11;
        for(int second=15;second<=40;second+=5) Sample(second,10);
        Check(sent.Count==2 && sent[^1].VideoSettings==new VideoSettings(27,7_500_000,2));
        sent.Clear();settings=new(VideoSettingsProtocol.Default,new(),sent.Add,clock);
        settings.Presented(Frame(1));settings.Pump();Sample(0,10);Sample(5,2000);Sample(10,2000);Check(sent.Count==0);
        settings=new(VideoSettingsProtocol.Default,new(60,16_000_000,true),sent.Add,clock);
        settings.Presented(Frame(1));settings.Pump();
        for(int second=0;second<=60;second+=5)settings.Observe(new(second,10,1,1000,0,0));
        Check(sent.Count==0);
        // Enabling automatic mode honors lower user ceilings immediately.
        settings.Configure(new(15,2_000_000,true));
        Check(sent.Count==1 && sent[^1].VideoSettings==new VideoSettings(15,2_000_000,1));
        // Switching back to manual while that request is in flight waits safely.
        settings.Configure(new(60,12_000_000,false));
        Check(sent.Count==1);
        Ack(15,2_000_000,2);settings.Presented(Frame(2));clock.Seconds++;
        settings.Pump();Check(sent.Count==2 && sent[^1].VideoSettings==new VideoSettings(60,12_000_000,2));
        Reject(()=>settings.Receive(new("video-settings-state-v2",VideoVersion:1,
            VideoSettings:new(60,12_000_000,3),Text:"unexpected")));
        return checks;
    }
}
