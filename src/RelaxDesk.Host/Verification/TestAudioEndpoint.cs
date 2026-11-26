using System.Diagnostics;
using RelaxDesk.Core;
using RelaxDesk.Windows;
namespace RelaxDesk.Host;

internal sealed class TestAudioEndpoint:IAudioEndpoint
{
    readonly bool fail;
    public TestAudioEndpoint(bool fail){this.fail=fail;HostSessionLog.Write("test_audio_open");}
    long last,sequence;bool disposed;
    public bool IsDefaultDevice()=>!disposed;
    public AudioFrame? Read()
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(fail)throw new IOException("Injected audio source failure");
        long now=Stopwatch.GetTimestamp();if((now-last)/(double)Stopwatch.Frequency<0.02)return null;last=now;
        return new(++sequence,(long)(now*(10_000_000d/Stopwatch.Frequency)),sequence==1,Enumerable.Repeat((byte)16,3840).ToArray());
    }
    public int Write(ReadOnlySpan<byte> samples)=>throw new InvalidOperationException();
    public void Flush()=>throw new InvalidOperationException();
    public void Dispose(){disposed=true;HostSessionLog.Write("test_audio_disposed");}
}
