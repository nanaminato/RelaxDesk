using System.Runtime.InteropServices;

namespace RelaxDesk.Windows;

public static class AudioDeviceContracts
{
    // Opens empty render/loopback streams, never reads captured content or emits sound.
    public static Task Run()
    {
        var done=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            try
            {
                void Reject<T>(Action action)where T:Exception{try{action();throw new IOException("Audio API accepted invalid ownership/mode/data");}catch(T){}}
                using(var render=new WindowsAudio(false))
                {
                    if(!render.IsDefaultDevice()||render.Write([])!=0)throw new IOException("Render setup contract");
                    Reject<InvalidOperationException>(()=>Task.Run(()=>render.Write([])).GetAwaiter().GetResult());
                    Reject<InvalidOperationException>(()=>Task.Run(render.Dispose).GetAwaiter().GetResult());
                    Reject<InvalidOperationException>(()=>render.Read());Reject<ArgumentException>(()=>render.Write([0,0]));
                }
                var capture=new WindowsAudio(true);
                using(capture){if(!capture.IsDefaultDevice())throw new IOException("Capture endpoint contract");Reject<ArgumentException>(()=>capture.Write([]));}
                Reject<ObjectDisposedException>(()=>capture.Read());
                Console.WriteLine("PASS WASAPI shared PCM16 render/loopback initialization, default endpoint identity, owner-apartment/mode/alignment/disposal guards and cleanup; no samples read/written, audibility/timing/device switching unverified");done.SetResult();
            }
            catch(Exception e)when(e is WindowsSessionUnavailableException||e is COMException&&unchecked((uint)e.HResult) is 0x80070490 or 0x80070005 or 0x88890004 or 0x88890010)
            {
                Console.WriteLine($"SKIP WASAPI endpoint initialization unavailable: {e.GetType().Name} hresult=0x{e.HResult:X8}; real capture/playback unverified");done.SetResult();
            }
            catch(Exception e){done.SetException(e);}
        }){IsBackground=true,Name="RelaxDesk audio API contracts"};thread.SetApartmentState(ApartmentState.STA);thread.Start();return done.Task;
    }
}
