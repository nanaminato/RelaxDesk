using System.Diagnostics;
using System.Threading.Channels;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public interface IAudioEndpoint:IDisposable
{
    bool IsDefaultDevice();AudioFrame? Read();int Write(ReadOnlySpan<byte> samples);void Flush();
}

// Owns only an optional audio stream. Never cancels the parent screen session.
public sealed class AudioStreamSession:IAsyncDisposable
{
    readonly Stream stream;readonly bool sending;readonly Func<IAudioEndpoint> factory;
    readonly CancellationTokenSource lifetime;readonly Action<string>? log;
    readonly Channel<AudioFrame> outgoing=Channel.CreateBounded<AudioFrame>(new BoundedChannelOptions(2){FullMode=BoundedChannelFullMode.DropOldest,SingleReader=true,SingleWriter=true});
    readonly AudioJitterBuffer incoming=new();
    readonly TaskCompletionSource deviceDone=new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Task transport;
    double volume=1;int muted,flush;
    readonly bool synchronizeVideo;readonly AudioPlaybackClock clock=new();
    public Task Completion{get;}
    public string? Failure{get;private set;}
    public long Packets{get;private set;}
    public long PayloadBytes{get;private set;}
    public long SuppressedSilentFrames => Interlocked.Read(ref suppressedSilent);
    long lateDrops,suppressedSilent;int deviceRestarts;
    int stopped,disposed;
    void Log(string value){try{log?.Invoke(value);}catch{/* Optional diagnostics cannot terminate the screen session. */}}
    public AudioStreamSession(Stream stream,bool sending,Func<IAudioEndpoint> factory,CancellationToken parent,Action<string>? log=null,bool synchronizeVideo=false)
    {
        this.stream=stream;this.sending=sending;this.factory=factory;this.log=log;this.synchronizeVideo=synchronizeVideo;lifetime=CancellationTokenSource.CreateLinkedTokenSource(parent);
        transport=Task.Run(Transport);
        var thread=new Thread(Device){IsBackground=true,Name=sending?"RelaxDesk audio capture":"RelaxDesk audio playback"};thread.SetApartmentState(ApartmentState.STA);thread.Start();
        Completion=Join();
    }
    public void SetVolume(double value,bool mute)
    {
        if(!double.IsFinite(value)||value is <0 or >1)throw new ArgumentOutOfRangeException(nameof(value));
        Volatile.Write(ref volume,value);if(Interlocked.Exchange(ref muted,mute?1:0)!=(mute?1:0))Interlocked.Increment(ref flush);
    }
    public void Stop()
    {
        if(Interlocked.Exchange(ref stopped,1)!=0)return;
        try{lifetime.Cancel();}catch(Exception e){Log($"audio_cancel_failed error_type={e.GetType().Name}");}
        try{stream.Dispose();}catch(Exception e){Log($"audio_close_failed error_type={e.GetType().Name}");}
    }
    public void VideoPresented(long timestamp100ns)=>clock.Presented(timestamp100ns,(long)(Stopwatch.GetTimestamp()*(10_000_000d/Stopwatch.Frequency)));
    void Fail(Exception error)
    {
        if(!lifetime.IsCancellationRequested){Failure=error.GetType().Name;Log($"audio_disabled stage={(sending?"capture-send":"receive-play")} error_type={Failure} hresult=0x{error.HResult:X8}");}
        Stop();
    }
    async Task Transport()
    {
        try
        {
            if(sending)
            {
                long lastSent=0;
                await foreach(var frame in outgoing.Reader.ReadAllAsync(lifetime.Token))
                {
                    using var deadline=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
                    await AudioProtocol.Write(stream,frame with{Discontinuity=frame.Discontinuity||frame.Sequence!=lastSent+1},deadline.Token);lastSent=frame.Sequence;Packets++;PayloadBytes+=frame.Data.Length;
                }
            }
            else while(!lifetime.IsCancellationRequested)
            {
                var frame=await AudioProtocol.Read(stream,lifetime.Token);incoming.Push(frame);
                Packets++;PayloadBytes+=frame.Data.Length;
            }
        }
        catch(Exception e){Fail(e);}
        finally{Stop();}
    }
    void Device()
    {
        IAudioEndpoint? endpoint=null;int retries=0,seenFlush=0,silentStreak=0;long sequence=0,lastTimestamp=0;
        AudioFrame? pending=null;int offset=0;long lastCheck=0,seenDiscontinuity=0;bool discontinuity=true,invalidated=false;
        try
        {
            lifetime.Token.ThrowIfCancellationRequested();endpoint=factory();
            while(!lifetime.IsCancellationRequested)
            {
                long now=Stopwatch.GetTimestamp();
                if((now-lastCheck)/(double)Stopwatch.Frequency>=0.25)
                {
                    lastCheck=now;
                    bool current;
                    try{current=!invalidated&&endpoint.IsDefaultDevice();}
                    catch(System.Runtime.InteropServices.COMException e)when(unchecked((uint)e.HResult) is 0x88890004 or 0x80070490){current=false;}
                    if(!current)
                    {
                        endpoint.Dispose();endpoint=null;
                        while(endpoint is null)
                        {
                            if(retries++>=3)throw new IOException("Audio device recovery exhausted");
                            deviceRestarts=retries;
                            lifetime.Token.ThrowIfCancellationRequested();if(lifetime.Token.WaitHandle.WaitOne(100*retries))lifetime.Token.ThrowIfCancellationRequested();
                            try{endpoint=factory();}
                            catch(System.Runtime.InteropServices.COMException e)when(unchecked((uint)e.HResult) is 0x88890004 or 0x80070490 or 0x88890010){Log($"audio_device_retry attempt={retries}");}
                        }
                        invalidated=false;discontinuity=true;silentStreak=0;pending=null;incoming.Flush();continue;
                    }
                }
                if(sending)
                {
                    AudioFrame? frame;
                    try{frame=endpoint.Read();}
                    catch(System.Runtime.InteropServices.COMException e)when(unchecked((uint)e.HResult)==0x88890004){invalidated=true;lastCheck=0;continue;}
                    if(frame is not null)
                    {
                        if(frame.Timestamp100ns>=lastTimestamp)
                        {
                            lastTimestamp=frame.Timestamp100ns;
                            if(AudioProtocol.IsSilent(frame.Data))
                            {
                                if(++silentStreak>2)
                                {
                                    Interlocked.Increment(ref suppressedSilent);
                                    discontinuity=true;
                                    continue;
                                }
                            }
                            else silentStreak=0;
                            outgoing.Writer.TryWrite(frame with{Sequence=++sequence,Discontinuity=discontinuity||frame.Discontinuity});discontinuity=false;
                        }
                        // Drain the capture backlog before polling again. One
                        // sleep per device packet falls behind small periods.
                        continue;
                    }
                }
                else
                {
                    long boundary=incoming.DiscontinuitySequence;
                    if(seenDiscontinuity!=boundary)
                    {
                        seenDiscontinuity=boundary;
                        // Push already removed older queued packets. Preserve the
                        // new packet and any newer samples while clearing stale
                        // partially submitted/device-buffered sound.
                        if(pending is not null&&pending.Sequence<boundary)pending=null;
                        try{endpoint.Flush();}catch(System.Runtime.InteropServices.COMException e)when(unchecked((uint)e.HResult)==0x88890004){invalidated=true;lastCheck=0;continue;}
                    }
                    int requestedFlush=Volatile.Read(ref flush);
                    if(seenFlush!=requestedFlush)
                    {
                        seenFlush=requestedFlush;incoming.Flush();pending=null;
                        try{endpoint.Flush();}catch(System.Runtime.InteropServices.COMException e)when(unchecked((uint)e.HResult)==0x88890004){invalidated=true;lastCheck=0;continue;}
                    }
                    if(Volatile.Read(ref muted)!=0){incoming.Flush();pending=null;}
                    else
                    {
                        if(pending is null&&incoming.TryTake(out pending))offset=0;
                        if(pending is not null)
                        {
                            var timing=synchronizeVideo?clock.Decide(pending.Timestamp100ns+(long)offset/4*10_000_000/AudioProtocol.Rate,(long)(now*(10_000_000d/Stopwatch.Frequency))):AudioTiming.Play;
                            if(timing==AudioTiming.Drop){lateDrops++;pending=null;continue;}
                            if(timing==AudioTiming.Wait){if(lifetime.Token.WaitHandle.WaitOne(5))break;continue;}
                            byte[] samples=AudioProtocol.Gain(pending.Data.AsSpan(offset),Volatile.Read(ref volume),false);
                            int written;
                            try{written=endpoint.Write(samples);}catch(System.Runtime.InteropServices.COMException e)when(unchecked((uint)e.HResult)==0x88890004){invalidated=true;lastCheck=0;continue;}
                            if(written<0||written>samples.Length||written%4!=0)throw new IOException("Invalid audio render progress");
                            offset+=written;if(offset==pending.Data.Length)pending=null;
                            // Refill available WASAPI capacity immediately, even
                            // across packet/partial-write boundaries. A 5ms wait
                            // can last a full Windows timer tick (~15.6ms), slower
                            // than the 10ms packets supplied by many endpoints.
                            if(written>0)continue;
                        }
                    }
                }
                if(lifetime.Token.WaitHandle.WaitOne(5))break;
            }
        }
        catch(Exception e){Fail(e);}
        finally
        {
            try{endpoint?.Dispose();}catch(Exception e){Fail(e);}
            outgoing.Writer.TryComplete();incoming.Flush();deviceDone.TrySetResult();
        }
    }
    async Task Join(){await Task.WhenAll(transport,deviceDone.Task);Log($"audio_session role={(sending?"capture":"playback")} packets={Packets} application_pcm_bytes={PayloadBytes} suppressed_silent_frames={SuppressedSilentFrames} device_retries={deviceRestarts} late_drops={lateDrops} buffered_frames={incoming.BufferedFrames}");}
    public async ValueTask DisposeAsync(){if(Interlocked.Exchange(ref disposed,1)!=0){await Completion;return;}Stop();await Completion;lifetime.Dispose();}
}
