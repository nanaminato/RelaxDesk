using System.Diagnostics;
using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Apartment-owned WASAPI shared streams. Create/use/dispose on one dedicated STA.
// Host authorization and transport belong to the caller; construction opens a device.
public sealed class WindowsAudio:IAudioEndpoint
{
    readonly int owner=Environment.CurrentManagedThreadId;
    readonly bool capture;
    object? enumeratorObject,clientObject,service;
    IMMDevice? device;IAudioClient? client;uint capacity;bool started,disposed;
    long sequence;
    public string DeviceId{get;private set;}="";
    public WindowsAudio(bool capture)
    {
        if(Thread.CurrentThread.GetApartmentState()!=ApartmentState.STA)throw new InvalidOperationException("Audio requires an owned STA thread");
        WindowsSessionAccess.EnsureAllowed();this.capture=capture;
        try
        {
            enumeratorObject=Activator.CreateInstance(Type.GetTypeFromCLSID(new("BCDE0395-E52F-467C-8E3D-C4579291692E"))!)!;
            var enumerator=(IMMDeviceEnumerator)enumeratorObject;Check(enumerator.GetDefaultAudioEndpoint(0,0,out device));
            Check(device.GetId(out string id));DeviceId=id;
            var iid=typeof(IAudioClient).GUID;Check(device.Activate(ref iid,23,0,out clientObject));client=(IAudioClient)clientObject;
            var format=new WaveFormat{Tag=1,Channels=2,Rate=48000,AverageBytes=192000,Align=4,Bits=16};
            // Shared engine conversion, never exclusive mode or endpoint-volume mutation.
            Check(client.Initialize(0,0x88000000u|(capture?0x00020000u:0),1_000_000,0,ref format,0));
            Check(client.GetBufferSize(out capacity));if(capacity is 0 or >48000)throw new IOException("Unbounded audio endpoint buffer");
            iid=capture?typeof(IAudioCaptureClient).GUID:typeof(IAudioRenderClient).GUID;Check(client.GetService(ref iid,out service));
            Check(client.Start());started=true;
        }
        catch{Dispose();throw;}
    }
    void Guard(){ObjectDisposedException.ThrowIf(disposed,this);if(owner!=Environment.CurrentManagedThreadId)throw new InvalidOperationException("Audio apartment changed");WindowsSessionAccess.EnsureAllowed();}
    static void Check(int result){Marshal.ThrowExceptionForHR(result);}
    public bool IsDefaultDevice()
    {
        Guard();var enumerator=(IMMDeviceEnumerator)enumeratorObject!;Check(enumerator.GetDefaultAudioEndpoint(0,0,out var current));
        try{Check(current.GetId(out string id));return id==DeviceId;}finally{Marshal.ReleaseComObject(current);}
    }
    public AudioFrame? Read()
    {
        try { Guard(); }
        catch (WindowsSessionUnavailableException error) when (error.Reason is "input-desktop-unavailable" or "input-desktop-unsupported")
        { return null; }
        if(!capture)throw new InvalidOperationException("Not a capture stream");
        var source=(IAudioCaptureClient)service!;Check(source.GetNextPacketSize(out uint next));if(next==0)return null;
        Check(source.GetBuffer(out nint data,out uint frames,out uint flags,out _,out ulong clock));
        try
        {
            if(frames is 0 or >AudioProtocol.MaximumFrames)throw new IOException("Audio capture packet exceeds 100ms limit");
            byte[] bytes=new byte[checked((int)frames*AudioProtocol.FrameBytes)];
            if((flags&2)==0){if(data==0)throw new IOException("Audio sample missing");Marshal.Copy(data,bytes,0,bytes.Length);}
            // WASAPI QPC position is already converted to 100ns, like media timestamps.
            long timestamp=(flags&4)==0&&clock<=long.MaxValue?(long)clock:checked((long)(Stopwatch.GetTimestamp()*(10_000_000d/Stopwatch.Frequency)));
            return new(++sequence,timestamp,(flags&1)!=0||sequence==1,bytes);
        }
        finally{Check(source.ReleaseBuffer(frames));}
    }
    public int Write(ReadOnlySpan<byte> samples)
    {
        Guard();if(capture||samples.Length%AudioProtocol.FrameBytes!=0||samples.Length>AudioProtocol.MaximumFrames*AudioProtocol.FrameBytes)throw new ArgumentException("Invalid render data");
        Check(client!.GetCurrentPadding(out uint padding));if(padding>capacity)throw new IOException("Audio padding exceeds capacity");
        uint targetPadding=Math.Min(capacity,(uint)(AudioProtocol.Rate/25));
        uint frames=Math.Min(padding>=targetPadding?0:targetPadding-padding,(uint)(samples.Length/AudioProtocol.FrameBytes));if(frames==0)return 0;
        var sink=(IAudioRenderClient)service!;Check(sink.GetBuffer(frames,out nint target));
        bool copied=false;
        try{byte[] copy=samples[..checked((int)frames*AudioProtocol.FrameBytes)].ToArray();Marshal.Copy(copy,0,target,copy.Length);copied=true;}
        finally{Check(sink.ReleaseBuffer(frames,copied?0u:2u));}
        return checked((int)frames*AudioProtocol.FrameBytes);
    }
    public void Flush(){Guard();if(capture)throw new InvalidOperationException("Only playback can flush");Check(client!.Stop());Check(client.Reset());Check(client.Start());}
    public void Dispose()
    {
        if(disposed)return;if(owner!=Environment.CurrentManagedThreadId)throw new InvalidOperationException("Dispose audio on owner apartment");disposed=true;
        try{if(started)client!.Stop();}
        finally{if(service is not null)Marshal.ReleaseComObject(service);if(clientObject is not null)Marshal.ReleaseComObject(clientObject);if(device is not null)Marshal.ReleaseComObject(device);if(enumeratorObject is not null)Marshal.ReleaseComObject(enumeratorObject);service=clientObject=enumeratorObject=null;client=null;device=null;}
    }
    [StructLayout(LayoutKind.Sequential,Pack=2)] struct WaveFormat{public ushort Tag,Channels;public uint Rate,AverageBytes;public ushort Align,Bits,Extra;}
    [ComImport,Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig]int EnumAudioEndpoints(int flow,uint mask,out nint collection);
        [PreserveSig]int GetDefaultAudioEndpoint(int flow,int role,out IMMDevice endpoint);
        [PreserveSig]int GetDevice([MarshalAs(UnmanagedType.LPWStr)]string id,out IMMDevice endpoint);
        [PreserveSig]int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig]int UnregisterEndpointNotificationCallback(nint callback);
    }
    [ComImport,Guid("D666063F-1587-4E43-81F1-B948E807363F"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig]int Activate(ref Guid iid,uint context,nint parameters,[MarshalAs(UnmanagedType.IUnknown)]out object instance);
        [PreserveSig]int OpenPropertyStore(uint access,out nint store);
        [PreserveSig]int GetId([MarshalAs(UnmanagedType.LPWStr)]out string id);
        [PreserveSig]int GetState(out uint state);
    }
    [ComImport,Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        [PreserveSig]int Initialize(int mode,uint flags,long duration,long periodicity,ref WaveFormat format,nint session);
        [PreserveSig]int GetBufferSize(out uint frames);
        [PreserveSig]int GetStreamLatency(out long latency);
        [PreserveSig]int GetCurrentPadding(out uint frames);
        [PreserveSig]int IsFormatSupported(int mode,nint format,out nint closest);
        [PreserveSig]int GetMixFormat(out nint format);
        [PreserveSig]int GetDevicePeriod(out long normal,out long minimum);
        [PreserveSig]int Start();[PreserveSig]int Stop();[PreserveSig]int Reset();
        [PreserveSig]int SetEventHandle(nint handle);
        [PreserveSig]int GetService(ref Guid iid,[MarshalAs(UnmanagedType.IUnknown)]out object instance);
    }
    [ComImport,Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        [PreserveSig]int GetBuffer(out nint data,out uint frames,out uint flags,out ulong position,out ulong clock);
        [PreserveSig]int ReleaseBuffer(uint frames);
        [PreserveSig]int GetNextPacketSize(out uint frames);
    }
    [ComImport,Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioRenderClient
    {
        [PreserveSig]int GetBuffer(uint frames,out nint data);
        [PreserveSig]int ReleaseBuffer(uint frames,uint flags);
    }
}
