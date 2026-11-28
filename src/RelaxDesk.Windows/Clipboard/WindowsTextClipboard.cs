using System.Runtime.InteropServices;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// The legacy interface uses CF_UNICODETEXT; the rich interface is in the
// WindowsContentClipboard partial. No delayed rendering or OLE/UI dependency.
// All Win32 calls stay on the synchronizer thread; ownership uses a real HWND.
public sealed partial class WindowsTextClipboard : ITextClipboard
{
    const uint UnicodeText = 13;
    static int activeWorker;
    readonly int thread = Environment.CurrentManagedThreadId;
    nint window;
    ClipboardSnapshot? cached;
    [StructLayout(LayoutKind.Sequential)] struct Message { public nint Window; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X, Y; public uint Private; }
    [DllImport("user32.dll")] static extern bool PeekMessage(out Message message, nint hwnd, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] static extern nint DispatchMessage(ref Message message);
    static void PumpMessages()
    {
        // A clipboard owner must service WM_DESTROYCLIPBOARD from other apps.
        // No WPF Dispatcher and no UI-thread callbacks are involved.
        for (int i = 0; i < 64 && PeekMessage(out var message, 0, 0, 0, 1); i++)
        { TranslateMessage(ref message); DispatchMessage(ref message); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern int CountClipboardFormats();
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint data);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
    public WindowsTextClipboard(WindowsFileRoot? fileRoot=null,Action<ClipboardFiles?>? receiveFileOffer=null,
        ClipboardFileSources? fileSources=null, Action<LocalClipboardFiles>? localFilesChanged=null,
        Action<ClipboardFiles, long>? remoteFilesReceived=null)
    {
        clipboardFileRoot=fileRoot;fileOffer=receiveFileOffer;
        this.fileSources=fileSources;this.localFilesChanged=localFilesChanged;this.remoteFilesReceived=remoteFilesReceived;
        // A stalled delayed-rendering owner cannot accumulate one native thread
        // per reconnect. New clipboard sessions fail closed until it returns.
        if (Interlocked.CompareExchange(ref activeWorker, 1, 0) != 0) throw new IOException("Previous clipboard worker is still releasing native resources");
        try
        {
            window = CreateWindowEx(0, "STATIC", "RelaxDesk clipboard worker", 0, 0, 0, 0, 0, -3, 0, 0, 0);
            if (window == 0) throw new IOException("Clipboard worker window unavailable");
        }
        catch { Interlocked.Exchange(ref activeWorker, 0); throw; }
    }
    void Verify()
    {
        if (thread != Environment.CurrentManagedThreadId) throw new InvalidOperationException("Clipboard adapter is thread-affine");
        ObjectDisposedException.ThrowIf(window == 0, this);
    }
    public unsafe bool TryRead(out ClipboardSnapshot snapshot)
    {
        Verify(); PumpMessages(); Desktop.EnsureInteractiveDesktop(); uint sequence = GetClipboardSequenceNumber(); snapshot = new(sequence, null);
        if (sequence == 0) return false;
        if (cached?.Version == sequence) { snapshot = cached; return true; }
        if (!TryOpenClipboard()) return false;
        try
        {
            string? text = null;
            if (!IsClipboardFormatAvailable(UnicodeText)) { if (CountClipboardFormats() == 0) text = ""; }
            else
            {
                nint memory = GetClipboardData(UnicodeText); if (memory == 0) return false;
                nuint bytes = GlobalSize(memory);
                if (bytes is >= 2 and <= ClipboardTextProtocol.MaximumBytes + 2 && bytes % 2 == 0)
                {
                    nint pointer = GlobalLock(memory); if (pointer == 0) return false;
                    try
                    {
                        var chars = new ReadOnlySpan<char>((void*)pointer, checked((int)bytes / 2));
                        int end = chars.IndexOf('\0');
                        if (end >= 0) { string value = new(chars[..end]); if (ClipboardTextProtocol.ValidText(value)) text = value; }
                    }
                    finally { GlobalUnlock(memory); }
                }
            }
            snapshot = cached = new(GetClipboardSequenceNumber(), text); return true;
        }
        finally { CloseClipboard(); }
    }
    public bool TryWrite(string text, long expectedVersion, out long writtenVersion)
    {
        Verify(); Desktop.EnsureInteractiveDesktop(); writtenVersion = expectedVersion;
        if (!ClipboardTextProtocol.ValidText(text)) return false;
        nint memory = GlobalAlloc(0x42, checked((nuint)(text.Length + 1) * 2)); if (memory == 0) return false;
        try
        {
            nint pointer = GlobalLock(memory); if (pointer == 0) return false;
            try { Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length); }
            finally { GlobalUnlock(memory); }
            if (!TryOpenClipboard()) return false;
            try
            {
                // Do not overwrite a copy made by another app since our read.
                if (GetClipboardSequenceNumber() != expectedVersion || !EmptyClipboard() || SetClipboardData(UnicodeText, memory) == 0) return false;
                memory = 0; // Ownership transferred to Windows.
            }
            finally { CloseClipboard(); }
            // The stable sequence number exists only after CloseClipboard.
            writtenVersion = GetClipboardSequenceNumber(); cached = new(writtenVersion, text); return true;
        }
        finally { if (memory != 0) GlobalFree(memory); }
    }
    public void Dispose()
    {
        if (window == 0) return; Verify();
        ClearPublishedFiles();
        fileSources?.Clear();
        if (!DestroyWindow(window)) throw new IOException("Clipboard worker window destruction failed");
        window = 0; cached = null;richCached=null;CancelFileCommand(); Interlocked.Exchange(ref activeWorker, 0);
    }
}
