using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class WindowsFileClipboardVerification
{
    public static void Run(bool replaceClipboard)
    {
        var formats = new ClipboardFormats(Text: true, Image: false, Html: true, Files: true);
        string fixture = Path.Combine(Path.GetTempPath(), "RelaxDesk-Clipboard-Test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string file = Path.Combine(fixture, "中文-日本語.txt");
            string empty = Path.Combine(fixture, "空");
            File.WriteAllText(file, "Clipboard integration fixture");
            Directory.CreateDirectory(empty);
            long publishedVersion;
            using (var clipboard = new WindowsTextClipboard())
            {
                Check(clipboard.TryRead(formats, out var baseline), "read native clipboard");
                if (!replaceClipboard && baseline.Content is not { Clear: true })
                {
                    Console.WriteLine("SKIP native Windows clipboard: current clipboard contains data");
                    return;
                }
                Check(clipboard.TryWrite(new ClipboardContent(Text: "RelaxDesk clipboard test", Html: "<b>test</b>"),
                    baseline.Version, out long newVersion), "publish native text and HTML");
                Check(!Execute(clipboard, clipboard.PublishFiles([file], baseline.Version, default)), "reject stale publication");
                Check(clipboard.TryRead(formats, out var rich) && rich.Content is { Text: "RelaxDesk clipboard test", Html: "<b>test</b>" },
                    "preserve newer text and HTML");
                Check(Execute(clipboard, clipboard.PublishFiles([file, empty], newVersion, default)), "publish native file list");
                var first = Execute(clipboard, clipboard.ReadFiles(default));
                var second = Execute(clipboard, clipboard.ReadFiles(default));
                Check(first is not null && second is not null && first.Paths.SequenceEqual(new[] { file, empty }) &&
                    second.Paths.SequenceEqual(first.Paths) && first.Version == second.Version, "read the same native CF_HDROP twice");
                publishedVersion = first!.Version;
                Check(clipboard.TryRead(formats, out var written) && written.Content is null && !written.LocalFileSelection,
                    "suppress native notification from remote file publication");
            }

            using var observer = new WindowsTextClipboard();
            Check(observer.TryRead(formats, out var disconnected) && disconnected.Version != publishedVersion &&
                disconnected.Content is { Clear: true }, "invalidate the remotely published file list on teardown");
            Console.WriteLine("PASS native Windows clipboard: text/HTML, stale file publication, Unicode CF_HDROP, repeated read, echo suppression, teardown");
        }
        finally
        {
            Directory.Delete(fixture, recursive: true);
        }

        T Execute<T>(WindowsTextClipboard clipboard, Task<T> command)
        {
            Check(clipboard.TryRead(formats, out _), "pump native clipboard command");
            return command.GetAwaiter().GetResult();
        }
    }

    static void Check(bool condition, string operation)
    {
        if (!condition) throw new IOException($"Windows clipboard integration failed: {operation}");
    }
}
