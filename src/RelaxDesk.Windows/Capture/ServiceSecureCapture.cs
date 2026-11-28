using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RelaxDesk.Windows;

// Called only on the SYSTEM desktop thread after unlocked UAC authorization.
// DXGI preserves composed consent UI; GDI is an explicit compatibility fallback.
internal sealed class ServiceSecureCapture(WindowsDisplaySelection selection, Action<string>? diagnostic) : IDisposable
{
    readonly Dictionary<string,DxgiCaptureBackend> captures=[];
    bool useGdi,reported;
    public byte[] Capture()
    {
        if((long)selection.Bounds.Width*selection.Bounds.Height>RelaxDesk.Core.ServiceDesktopProtocol.MaximumPixels)
            throw new InvalidDataException("Secure output exceeds frame bound");
        if(useGdi)return Gdi();
        byte[] output=new byte[checked(selection.Bounds.Width*selection.Bounds.Height*4)];
        try
        {
            foreach(var display in selection.Topology.Where(d=>selection.Device=="all"||d.Id==selection.Device))
            {
                if(display.Rotation!=0)throw new NotSupportedException("Rotated secure desktop requires GDI compatibility");
                if(!captures.TryGetValue(display.Id,out var capture))captures.Add(display.Id,capture=new(display.Id));
                var frame=capture.Capture(150)??capture.CachedFrame();
                if(frame is null)throw new SecureFramePendingException();
                if(frame.Width!=display.Bounds.Width||frame.Height!=display.Bounds.Height)throw new IOException("Secure output geometry changed");
                byte[] pixels=capture.ReadPixels(frame);
                try
                {
                    PointerCompositor.Apply(pixels,frame.Width,frame.Height,frame.Metadata);
                    int x=display.Bounds.X-selection.Bounds.X,y=display.Bounds.Y-selection.Bounds.Y;
                    for(int row=0;row<frame.Height;row++)
                        Buffer.BlockCopy(pixels,row*frame.Width*4,output,((y+row)*selection.Bounds.Width+x)*4,frame.Width*4);
                }
                finally{CryptographicOperations.ZeroMemory(pixels);}
            }
            if(!reported){reported=true;diagnostic?.Invoke("secure_capture_backend=dxgi-readback");}
            return output;
        }
        catch(SecureFramePendingException)
        {
            CryptographicOperations.ZeroMemory(output);
            if(!reported){reported=true;diagnostic?.Invoke("secure_capture_backend=dxgi-waiting-for-first-frame");}
            throw;
        }
        catch(Exception error)when(error is ExternalException or IOException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            CryptographicOperations.ZeroMemory(output);
            foreach(var capture in captures.Values)capture.Dispose();captures.Clear();useGdi=true;
            diagnostic?.Invoke($"secure_capture_backend=gdi fallback_type={error.GetType().Name} hresult=0x{error.HResult:X8}");
            return Gdi();
        }
    }
    byte[] Gdi(){using var desktop=new Desktop(selection);return desktop.Capture(includePointer:true);}
    public void Dispose(){foreach(var capture in captures.Values)capture.Dispose();captures.Clear();}
}

internal sealed class SecureFramePendingException : Exception;
