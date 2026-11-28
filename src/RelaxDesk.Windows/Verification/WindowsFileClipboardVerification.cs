using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

// Real Windows clipboard integration. It mutates the shared interactive
// clipboard, so it is a dedicated smoke entry and refuses to run unless the
// caller explicitly allows replacing existing content.
public static class WindowsFileClipboardVerification
{
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterClipboardFormat(string name);
    const uint CfUnicodeText = 13, CfHDrop = 15;
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint memory);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint memory);

    static nint Allocate(byte[] bytes)
    {
        nint memory = GlobalAlloc(0x42, (nuint)bytes.Length);
        if (memory == 0) throw new IOException("Window clipboard test allocation failed");
        nint pointer = GlobalLock(memory);
        if (pointer == 0) { GlobalFree(memory); throw new IOException("Window clipboard test lock failed"); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); return memory; }
        finally { GlobalUnlock(memory); }
    }

    static void PublishRaw(uint format, byte[] bytes)
    {
        nint memory = Allocate(bytes);
        try
        {
            if (!OpenClipboard(0)) throw new IOException("Window clipboard test open failed");
            try
            {
                if (!EmptyClipboard() || SetClipboardData(format, memory) == 0) throw new IOException("Window clipboard test publish failed");
                memory = 0;
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != 0) GlobalFree(memory); }
    }

    static void PublishRawText(string text) => PublishRaw(CfUnicodeText, Encoding.Unicode.GetBytes(text + "\0"));
    static void PublishRawFiles(string[] paths) => PublishRaw(CfHDrop, ClipboardFileFormat.Encode(paths));

    static unsafe string? ReadRawText()
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
            if (!OpenClipboard(0)) { Thread.Sleep(10); continue; }
            try
            {
                nint memory = GetClipboardData(CfUnicodeText);
                if (memory == 0) return null;
                nuint bytes = GlobalSize(memory);
                if (bytes is < 2 or > 32 * 1024 * 1024 || bytes % 2 != 0) return null;
                nint pointer = GlobalLock(memory);
                if (pointer == 0) return null;
                try
                {
                    var chars = new ReadOnlySpan<char>((void*)pointer, checked((int)bytes / 2));
                    int end = chars.IndexOf('\0');
                    return new string(chars[..(end < 0 ? chars.Length : end)]);
                }
                finally { GlobalUnlock(memory); }
            }
            finally { CloseClipboard(); }
        }
        return null;
    }

    static void RestoreText(string? text)
    {
        if (text is null) { try { if (OpenClipboard(0)) { try { EmptyClipboard(); } finally { CloseClipboard(); } } } catch { } return; }
        try { PublishRawText(text); } catch { }
    }

    static async Task Until(Func<bool> condition, string operation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            if (timeout.IsCancellationRequested) throw new IOException("Timed out: " + operation);
            await Task.Delay(20, timeout.Token);
        }
    }

    static byte[] Noise(int width, int height)
    {
        var pixels = new byte[checked(width * height * 4)];
        var random = new Random(1337);
        random.NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        return pixels;
    }

    public static void Run(bool replaceClipboard)
    {
        var formats = new ClipboardFormats(Text: true, Image: true, Html: true, Files: true);
        string fixture = Path.Combine(Path.GetTempPath(), "RelaxDesk-Clipboard-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string? savedText = ReadRawText();
        bool replaceStarted=false;
        try
        {
            string file = Path.Combine(fixture, "中文-日本語.txt");
            string empty = Path.Combine(fixture, "空");
            File.WriteAllText(file, "Clipboard integration fixture");
            Directory.CreateDirectory(empty);
            var localCopies = new ConcurrentQueue<LocalClipboardFiles>();
            long publishedVersion;
            using (var clipboard = new WindowsTextClipboard(localFilesChanged: localCopies.Enqueue))
            {
                Check(clipboard.TryRead(formats, out var baseline), "read native clipboard");
                if (!replaceClipboard && baseline.Content is not { Clear: true })
                {
                    Console.WriteLine("SKIP native Windows clipboard: current clipboard contains data; pass --replace-current-clipboard on a test account");
                    Environment.ExitCode = 2;
                    return;
                }

                replaceStarted=true;
                // A write must report the final sequence number, not the value
                // observed while the clipboard is still open.
                Check(clipboard.TryWrite(new ClipboardContent(Text: "RelaxDesk clipboard test", Html: "<b>test</b>"),
                    GetClipboardSequenceNumber(), out long textVersion), "publish native text and HTML");
                Check(textVersion == GetClipboardSequenceNumber(), "text write reports final clipboard sequence");
                Check(clipboard.TryRead(formats, out var echoedText) && echoedText.Echo &&
                    echoedText.Content is { Text: "RelaxDesk clipboard test", Html: "<b>test</b>" },
                    "own text write is reported as an echo");
                Check(!Execute(clipboard, clipboard.PublishFiles([file], textVersion - 1, default)), "reject stale publication");

                // The old 64 KiB text cap rejected ordinary large code/source
                // copies. A 300 KB UTF-16 payload must survive unchanged.
                string largeText = new('x', 300_000);
                Check(clipboard.TryWrite(new ClipboardContent(Text: largeText), GetClipboardSequenceNumber(), out _),
                    "publish 300 KB text");
                Check(clipboard.TryRead(formats, out var largeRead) && largeRead.Echo && largeRead.Content?.Text == largeText,
                    "read 300 KB text unchanged and recognize own write");

                // The old 8 MiB PNG cap rejected normal screenshots/photos.
                byte[] pixels = Noise(1600, 1600);
                byte[] largePng = ClipboardImageCodec.Encode(pixels, 1600, 1600);
                Check(largePng.Length > 8 * 1024 * 1024, "fixture PNG exceeds the old 8 MiB cap");
                Check(clipboard.TryWrite(new ClipboardContent(Png: largePng), GetClipboardSequenceNumber(), out _),
                    "publish PNG larger than 8 MiB");
                Check(clipboard.TryRead(formats, out var imageRead) && imageRead.Echo && imageRead.Content?.Png is not null,
                    "read large PNG and recognize own write");
                byte[] decoded = ClipboardImageCodec.DecodePng(imageRead.Content!.Png!, out int width, out int height);
                Check(width == 1600 && height == 1600 && decoded.AsSpan(0, 256).SequenceEqual(pixels.AsSpan(0, 256)),
                    "large PNG pixels round-trip");
                PublishRaw(RegisterClipboardFormat("PNG"),largePng);
                Check(clipboard.TryRead(formats,out var unmarkedImage)&&unmarkedImage.Echo,
                    "unchanged PNG without private marker stays suppressed");

                // The old 8192-pixel dimension cap rejected wide screenshots even
                // when their byte size was small.
                byte[] widePixels = Noise(8193, 1);
                Check(clipboard.TryWrite(new ClipboardContent(Png: ClipboardImageCodec.Encode(widePixels, 8193, 1)),
                    GetClipboardSequenceNumber(), out _), "publish 8193-pixel-wide PNG");
                Check(clipboard.TryRead(formats, out var wideRead) && wideRead.Content?.Png is not null,
                    "read 8193-pixel-wide PNG");
                _ = ClipboardImageCodec.DecodePng(wideRead.Content!.Png!, out int wideWidth, out int wideHeight);
                Check(wideWidth == 8193 && wideHeight == 1, "8193-pixel width survives the dimension bound");

                // Our own CF_HDROP publication must be an echo, even after its
                // private marker is stripped. Different paths are a new copy.
                Check(clipboard.TryWrite(new ClipboardContent(Text: "before files"), GetClipboardSequenceNumber(), out _),
                    "publish text before files");
                Check(Execute(clipboard, clipboard.PublishFiles([file, empty], GetClipboardSequenceNumber(), default)),
                    "publish native file list");
                Check(clipboard.TryRead(formats, out var fileEcho) &&
                    fileEcho.Echo && fileEcho.Content is null && !fileEcho.LocalFileSelection,
                    "own file publication is suppressed as an echo");
                long fileBaseline=Execute(clipboard,clipboard.ClipboardVersion(default));
                PublishRawFiles([file, empty]);
                Check(clipboard.TryRead(formats,out var unmarked)&&unmarked.Echo&&localCopies.IsEmpty,
                    "unmarked matching file list stays suppressed");
                Check(Execute(clipboard,clipboard.ClipboardVersion(default))==fileBaseline,
                    "unchanged paths keep the file publication baseline");
                PublishRawFiles([empty]);
                Check(!Execute(clipboard,clipboard.PublishFiles([file],fileBaseline,default)),
                    "changed paths reject stale publication");
                Check(clipboard.TryRead(formats, out var localFile) && !localFile.Echo && localFile.LocalFileSelection,
                    "different file paths are a real local copy");
                Check(localCopies.Count == 1, "local file copy reported exactly once");
                Check(Execute(clipboard, clipboard.PublishFiles([file], GetClipboardSequenceNumber(), default)),
                    "republish native file list");
                Check(clipboard.TryRead(formats, out var secondEcho) && secondEcho.Echo && localCopies.Count == 1,
                    "own file publication is not reported as another local copy");
                var first = Execute(clipboard, clipboard.ReadFiles(default));
                var second = Execute(clipboard, clipboard.ReadFiles(default));
                Check(first is not null && second is not null && first.Paths.SequenceEqual([file]) &&
                    second.Paths.SequenceEqual(first.Paths) && first.Version == second.Version,
                    "read the same native CF_HDROP twice");
                publishedVersion = first!.Version;
            }

            using (var observer = new WindowsTextClipboard())
            {
                Check(observer.TryRead(formats, out var disconnected) && disconnected.Version != publishedVersion &&
                    disconnected.Content is { Clear: true }, "invalidate the published file list on teardown");
            }

            // Only a new Host selection is visible through the fallback list.
            var sources=new ClipboardFileSources();
            PublishRawFiles([file]);
            using(var hostClipboard=new WindowsTextClipboard(fileSources:sources))
            {
                Check(hostClipboard.TryRead(formats,out _)&&sources.Latest().Length==0,
                    "initial clipboard is not advertised");
                PublishRawFiles([empty]);
                Check(hostClipboard.TryRead(formats,out var selected)&&selected.Content?.Files is not null&&sources.Latest().Length==1,
                    "new Host selection is published to the independent file channel");
                PublishRawText("stop advertising the old file list");
                Check(hostClipboard.TryRead(formats,out _)&&sources.Latest().Length==0,
                    "replacing Host selection clears the fallback list");
            }

            // Teardown must not clear unrelated text that replaced the file list.
            using (var textAdapter = new WindowsTextClipboard())
            {
                Check(textAdapter.TryRead(formats, out _), "pump text adapter");
                Check(textAdapter.TryWrite(new ClipboardContent(Text: "survives teardown"), GetClipboardSequenceNumber(), out _),
                    "publish text after file teardown");
            }
            Check(ReadRawText() == "survives teardown", "adapter teardown keeps text it did not publish as files");

            SynchronizerEchoTest().GetAwaiter().GetResult();

            Console.WriteLine("PASS native Windows clipboard: post-close sequence, text/HTML echo, 300 KB text, >8 MiB PNG, 8193-pixel width, Unicode CF_HDROP, own-publication echo, real local re-copy detection, teardown invalidation, synchronizer no-echo/no-stale-overwrite");
        }
        finally
        {
            if(replaceStarted)RestoreText(savedText);
            Directory.Delete(fixture, recursive: true);
        }

        T Execute<T>(WindowsTextClipboard clipboard, Task<T> command)
        {
            Check(clipboard.TryRead(formats, out _), "pump native clipboard command");
            return command.GetAwaiter().GetResult();
        }
    }

    static async Task SynchronizerEchoTest()
    {
        var gate = new AccessGate();
        gate.Approve([SessionCapabilities.ClipboardText]);
        string origin = Guid.NewGuid().ToString("N");
        var sent = new ConcurrentQueue<ClipboardContentUpdate>();
        var diagnostics = new ConcurrentQueue<string>();
        await using var worker = new ClipboardContentSynchronizer(() => new WindowsTextClipboard(), gate, sent.Enqueue,
            default, diagnostics.Enqueue, pollMilliseconds: 20);
        await Task.Delay(250);
        string remote = "remote sync " + Guid.NewGuid().ToString("N");
        Check(worker.Receive(new ClipboardContentUpdate(origin, 100, new ClipboardContent(Text: remote))), "receive remote text");
        await Until(() => ReadRawText() == remote, "remote text applied");
        await Task.Delay(400);
        Check(sent.IsEmpty, "remote write must not be echoed back as a local copy");

        // A real local copy after the remote write must be sent exactly once
        // with a revision above the remote update.
        string local = "local copy " + Guid.NewGuid().ToString("N");
        PublishRawText(local);
        await Until(() => sent.Count == 1, "local copy sent");
        Check(sent.Single() is { Content.Text: var text } && text == local && sent.Single().Revision > 100,
            "local copy carries a newer revision");

        // A stale remote update must not overwrite the newer local clipboard.
        Check(worker.Receive(new ClipboardContentUpdate(origin, 99, new ClipboardContent(Text: "stale remote"))), "receive stale update");
        await Task.Delay(400);
        Check(ReadRawText() == local && sent.Count == 1, "stale remote update does not overwrite the newer local copy");
        Console.WriteLine("PASS real adapter synchronizer: no echo, local copy sent once, stale remote rejected");
    }

    static void Check(bool condition, string operation)
    {
        if (!condition) throw new IOException($"Windows clipboard integration failed: {operation}");
    }
}
