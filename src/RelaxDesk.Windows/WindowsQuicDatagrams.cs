using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public sealed class WindowsQuicDatagrams : IQuicDatagrams, IQuicPathStatistics
{
    const string Library = "RelaxDesk.Native.dll";
    const int Retry = unchecked((int)0x800704D5);
    const int Timeout = unchecked((int)0x800705B4);

    sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public Handle() : base(true) { }
        protected override bool ReleaseHandle() { CloseEndpoint(handle); return true; }
    }

    [DllImport(Library, EntryPoint = "rd_quic_open", CallingConvention = CallingConvention.Cdecl)]
    static extern int Open([MarshalAs(UnmanagedType.LPWStr)] string runtime,
        nint certificate, [MarshalAs(UnmanagedType.LPStr)] string address, ref ushort port, int server, out Handle endpoint);

    [DllImport(Library, EntryPoint = "rd_quic_open_pfx", CallingConvention = CallingConvention.Cdecl)]
    static extern int OpenPfx([MarshalAs(UnmanagedType.LPWStr)] string runtime, nint certificate,
        [MarshalAs(UnmanagedType.LPStr)] string address, ref ushort port, byte[] pfx, uint length,
        [MarshalAs(UnmanagedType.LPStr)] string password, out Handle endpoint);

    [DllImport(Library, EntryPoint = "rd_quic_ready", CallingConvention = CallingConvention.Cdecl)]
    static extern int Ready(Handle endpoint, out ushort budget);

    [DllImport(Library, EntryPoint = "rd_quic_send", CallingConvention = CallingConvention.Cdecl)]
    static extern int Send(Handle endpoint, byte[] data, ushort length);

    [DllImport(Library, EntryPoint = "rd_quic_receive", CallingConvention = CallingConvention.Cdecl)]
    static extern int Receive(Handle endpoint, [Out] byte[] data, ushort capacity, out ushort length, uint milliseconds);

    [DllImport(Library, EntryPoint = "rd_quic_close", CallingConvention = CallingConvention.Cdecl)]
    static extern void CloseEndpoint(nint endpoint);

    [StructLayout(LayoutKind.Sequential)]
    struct NativePathStatistics
    {
        public uint Rtt, Variation;
        public ulong Received, Reordered, QueueDrops, Sent, SuspectedLost, SpuriousLost;
    }

    [DllImport(Library, EntryPoint = "rd_quic_statistics_v2", CallingConvention = CallingConvention.Cdecl)]
    static extern int Statistics(Handle endpoint, out NativePathStatistics snapshot);

    public QuicPathStatistics? ReadPathStatistics()
    {
        if (handle.IsClosed) return null;
        try
        {
            return Statistics(handle, out var snapshot) == 0
                ? new(snapshot.Rtt, snapshot.Variation, snapshot.Received, snapshot.Reordered, snapshot.QueueDrops, QuicSendStatistics.FromCounters(snapshot.Sent, snapshot.SuspectedLost, snapshot.SpuriousLost))
                : null;
        }
        catch (Exception error) when (error is ObjectDisposedException or EntryPointNotFoundException) { return null; }
    }

    readonly Handle handle;
    readonly byte[] input = new byte[QuicMediaAttachment.MaxDatagramBytes];
    public int Port { get; }

    WindowsQuicDatagrams(IPAddress address, int port, X509Certificate2 certificate, bool server,
        byte[]? pfx = null, string? password = null)
    {
        ushort assigned = checked((ushort)port);
        string dll = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "msquic.dll");
        int status = pfx is null
            ? Open(dll, certificate.Handle, address.ToString(), ref assigned, server ? 1 : 0, out handle)
            : OpenPfx(dll, certificate.Handle, address.ToString(), ref assigned, pfx, (uint)pfx.Length, password!, out handle);
        GC.KeepAlive(certificate);
        if (status < 0) { handle.Dispose(); RequireSuccess(status, "open"); }
        Port = assigned;
    }

    public static WindowsQuicDatagrams Listen(IPAddress address, X509Certificate2 certificate) =>
        new(address, 0, certificate, true);

    public static WindowsQuicDatagrams ListenWithPkcs12(IPAddress address, X509Certificate2 certificate, byte[] pfx, string password) =>
        new(address, 0, certificate, true, pfx, password);

    public static WindowsQuicDatagrams Connect(IPEndPoint remote, X509Certificate2 pinnedCertificate) =>
        new(remote.Address, remote.Port, pinnedCertificate, false);

    public async Task<int> WaitForBudget(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int status = Ready(handle, out ushort budget);
            if (status != Retry)
            {
                RequireSuccess(status, "handshake");
                if (budget < QuicMediaAttachment.MinDatagramBytes) throw new IOException("QUIC datagram budget too small");
                return budget;
            }
            await Task.Delay(5, cancellation);
        }
    }

    // Send is single-producer. The native API owns a copy before returning.
    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        if (data.Length is <= 0 or > QuicMediaAttachment.MaxDatagramBytes) throw new ArgumentOutOfRangeException(nameof(data));
        byte[] bytes = MemoryMarshal.TryGetArray(data, out var segment) && segment.Offset == 0 ? segment.Array! : data.ToArray();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int status = Send(handle, bytes, (ushort)data.Length);
            if (status != Retry) { RequireSuccess(status, "send"); return; }
            await Task.Delay(2, cancellation);
        }
    }

    // The result remains valid until the next receive on this single-reader endpoint.
    public async ValueTask<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int status = Receive(handle, input, (ushort)input.Length, out ushort length, 0);
            if (status != Timeout) { RequireSuccess(status, "receive"); return input.AsMemory(0, length); }
            await Task.Delay(2, cancellation);
        }
    }

    static void RequireSuccess(int status, string operation)
    {
        if (status < 0) throw new IOException($"QUIC DATAGRAM {operation} failed (0x{status:X8})")
        {
            Data = { ["QuicStatus"] = status }
        };
    }

    public void Dispose() => handle.Dispose();
}
