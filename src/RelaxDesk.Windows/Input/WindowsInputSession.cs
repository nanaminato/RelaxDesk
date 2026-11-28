using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

[StructLayout(LayoutKind.Sequential)] internal struct NativeMouseInput{public int X,Y;public uint Data,Flags,Time;public nuint Extra;}
[StructLayout(LayoutKind.Sequential)] internal struct NativeKeyInput{public ushort Key,Scan;public uint Flags,Time;public nuint Extra;}
[StructLayout(LayoutKind.Explicit)] internal struct NativeInputUnion{[FieldOffset(0)]public NativeMouseInput Mouse;[FieldOffset(0)]public NativeKeyInput Keyboard;}
[StructLayout(LayoutKind.Sequential)] internal struct NativeInput{public uint Type;public NativeInputUnion Value;}
public readonly record struct InputRectangle(int X,int Y,int Width,int Height);
public readonly record struct InputGeometry(InputRectangle Surface,InputRectangle VirtualDesktop);

internal static class WindowsInputPlan
{
    static NativeInput Key(ushort key,ushort scan,uint flags)=>new(){Type=1,Value=new(){Keyboard=new(){Key=key,Scan=scan,Flags=flags}}};
    static NativeInput Mouse(uint flags,uint data=0,int x=0,int y=0)=>new(){Type=0,Value=new(){Mouse=new(){Flags=flags,Data=data,X=x,Y=y}}};
    public static NativeInput[] Build(RemoteInput input,InputGeometry geometry)
    {
        if(!InputProtocol.Valid(input))throw new InvalidDataException("Invalid input plan");
        switch(input.Kind)
        {
            case "key":
                bool scan=input.ScanCode!=0&&input.VirtualKey!=0x13;
                var key=Key(scan?(ushort)0:(ushort)input.VirtualKey,scan?(ushort)input.ScanCode:(ushort)0,
                    scan?8u|(input.Extended?1u:0)|(input.Up?2u:0):KeyboardInjectionFlags.ForVirtualKey(input.VirtualKey,input.Up)|(input.Extended?1u:0));
                return Enumerable.Repeat(key,input.RepeatCount).ToArray();
            case "move":
                var s=geometry.Surface;var v=geometry.VirtualDesktop;
                if(s.Width is <1 or >65536||s.Height is <1 or >65536||v.Width is <1 or >65536||v.Height is <1 or >65536||s.X<v.X||s.Y<v.Y||(long)s.X+s.Width>(long)v.X+v.Width||(long)s.Y+s.Height>(long)v.Y+v.Height)throw new IOException("Invalid input display geometry");
                long px=s.X+(input.X*(long)(s.Width-1)+32767)/65535,py=s.Y+(input.Y*(long)(s.Height-1)+32767)/65535;
                int x=(int)Math.Clamp(((px-v.X)*65536+32768)/v.Width,0,65535),y=(int)Math.Clamp(((py-v.Y)*65536+32768)/v.Height,0,65535);
                return [Mouse(0xC001,x:x,y:y)]; // Absolute pixel centers across the virtual desktop.
            case "button":
                return [Mouse(input.Button switch{1=>input.Up?4u:2u,2=>input.Up?16u:8u,3=>input.Up?64u:32u,_=>input.Up?256u:128u},input.Button>=4?(uint)(input.Button-3):0)];
            case "wheel":
                var wheels=new List<NativeInput>();if(input.WheelY!=0)wheels.Add(Mouse(0x800,unchecked((uint)input.WheelY)));if(input.WheelX!=0)wheels.Add(Mouse(0x1000,unchecked((uint)input.WheelX)));return wheels.ToArray();
            case "text":
                return input.Text!.SelectMany(unit=>new[]{Key(0,unit,4),Key(0,unit,6)}).ToArray();
            case "release":return [];
            default:throw new InvalidDataException("Unsupported input plan");
        }
    }
    public static RemoteInput Legacy(int code,bool up,int x,int y)=>code switch
    {
        0=>new("move",X:x,Y:y),256=>new("button",Button:1,Up:up),257=>new("button",Button:2,Up:up),258=>new("wheel",WheelY:120),259=>new("wheel",WheelY:-120),
        >0 and <255=>new("key",VirtualKey:code,Up:up),_=>throw new InvalidDataException("Unsupported legacy input")
    };
    public static long Identity(NativeInput input)
    {
        if(input.Type==1){var k=input.Value.Keyboard;return (1L<<48)|((long)(k.Flags&13)<<32)|((long)k.Scan<<16)|k.Key;}
        uint f=input.Value.Mouse.Flags;int button=(f&6)!=0?1:(f&24)!=0?2:(f&96)!=0?3:(f&384)!=0?3+(int)input.Value.Mouse.Data:0;
        return button==0?0:(2L<<48)|(uint)button;
    }
    public static bool IsUp(NativeInput input)=>input.Type==1?(input.Value.Keyboard.Flags&2)!=0:(input.Value.Mouse.Flags&(4|16|64|256))!=0;
    public static NativeInput Release(NativeInput input)
    {
        if(input.Type==1)input.Value.Keyboard.Flags|=2;
        else input.Value.Mouse.Flags=input.Value.Mouse.Flags switch{2=>4,8=>16,32=>64,128=>256,_=>input.Value.Mouse.Flags};
        return input;
    }
}

// Native IO is isolated from protocol/state. Verification uses the exact plan
// and accepted-prefix accounting with a non-injecting sender, never SendInput.
public sealed class WindowsInputSession : IDisposable
{
    [DllImport("user32.dll")]static extern uint SendInput(uint count,NativeInput[] input,int size);
    [DllImport("user32.dll")]static extern int GetSystemMetrics(int index);
    static readonly object deferredSync=new();
    static readonly Dictionary<long,NativeInput> deferred=[];
    readonly Dictionary<long,NativeInput> held=[];
    readonly Func<NativeInput[],uint> send;
    readonly Action ensure;
    readonly Func<InputGeometry> geometry;
    readonly bool production;
    bool disposed;
    bool releaseBeforeNextInput;
    sealed class InputRejectedException(string message) : IOException(message);
    public int HeldCount=>held.Count;
    static uint SendNativeInput(NativeInput[] batch)
    {
        WindowsDesktopSwitch.SyncThreadToInputDesktop();
        return SendInput((uint)batch.Length, batch, Marshal.SizeOf<NativeInput>());
    }
    public WindowsInputSession():this(SendNativeInput,Desktop.EnsureInteractiveDesktop,
        ()=>new(new(0,0,GetSystemMetrics(0),GetSystemMetrics(1)),new(GetSystemMetrics(76),GetSystemMetrics(77),GetSystemMetrics(78),GetSystemMetrics(79))),true){}
    public WindowsInputSession(WindowsDisplaySelection selection):this(SendNativeInput,Desktop.EnsureInteractiveDesktop,()=>{selection.EnsureCurrent();return selection.InputGeometry;},true){}
    public WindowsInputSession(Func<WindowsDisplaySelection> selection):this(SendNativeInput,Desktop.EnsureInteractiveDesktop,()=>{var current=selection();current.EnsureCurrent();return current.InputGeometry;},true){}
    internal WindowsInputSession(Func<NativeInput[],uint> send,Action ensure,Func<InputGeometry> geometry,bool production=false)
    {this.send=send;this.ensure=ensure;this.geometry=geometry;this.production=production;}
    void Record(NativeInput input)
    {long id=WindowsInputPlan.Identity(input);if(id==0)return;if(WindowsInputPlan.IsUp(input))held.Remove(id);else held[id]=input;}
    void Apply(NativeInput[] batch)
    {
        if(batch.Length==0)return;
        uint accepted=send(batch);if(accepted>batch.Length)throw new IOException("Invalid native input result");
        for(int i=0;i<accepted;i++)Record(batch[i]);
        if(accepted!=batch.Length)throw new InputRejectedException("Input unavailable or partial (UIPI/desktop restriction)");
    }
    void FlushDeferred()
    {
        if(!production)return;
        lock(deferredSync)
        {
            var releases=deferred.ToArray();if(releases.Length==0)return;var batch=releases.Select(pair=>pair.Value).ToArray();uint count=send(batch);
            if(count>batch.Length)throw new IOException("Invalid native cleanup result");
            for(int i=0;i<Math.Min(count,batch.Length);i++)deferred.Remove(releases[i].Key);
            if(count!=batch.Length)throw new InputRejectedException("Deferred input cleanup unavailable");
        }
    }
    readonly object inputSync = new();

    public void InjectLegacy(int code,bool up,int x,int y)=>Inject(WindowsInputPlan.Legacy(code,up,x,y));
    public bool TryInjectOrdinaryLegacy(int code, bool up, int x, int y) => TryInjectOrdinary(WindowsInputPlan.Legacy(code, up, x, y));

    public bool TryInjectOrdinary(RemoteInput input)
    {
        lock (inputSync)
        {
            if (releaseBeforeNextInput)
            {
                try { ReleaseAll(); }
                catch (InputRejectedException)
                {
                    // A blocked key-up must not suppress pointer motion forever. Let the
                    // user move away from a higher-integrity foreground target, but do not
                    // send another key/button down until outstanding releases succeed.
                    if (input.Kind != "move") return false;
                    try { Inject(input); return true; }
                    catch (InputRejectedException) { return false; }
                }
                releaseBeforeNextInput = false;
            }
            try
            {
                Inject(input);
                return true;
            }
            catch (InputRejectedException)
            {
                // Never replay a partially accepted command. Only retry releases.
                try { ReleaseAll(); }
                catch (InputRejectedException) { releaseBeforeNextInput = true; }
                return false;
            }
        }
    }

    public void Inject(RemoteInput input)
    {
        lock (inputSync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var batch = WindowsInputPlan.Build(input, geometry());
            ensure();
            FlushDeferred();
            if (input.Kind == "release") ReleaseAll(); else Apply(batch);
        }
    }
    public void ReleaseAll()
    {
        lock (inputSync)
        {
            if (held.Count == 0) return;
            ensure();
            Apply(held.Values.Reverse().Select(WindowsInputPlan.Release).ToArray());
        }
    }
    public void Dispose()
    {
        lock (inputSync)
        {
            if (disposed) return;
            try { ReleaseAll(); } catch (IOException) { }
            if (production && held.Count != 0)
                lock (deferredSync)
                    foreach (var pair in held) deferred[pair.Key] = WindowsInputPlan.Release(pair.Value);
            held.Clear();
            disposed = true;
        }
    }
}
