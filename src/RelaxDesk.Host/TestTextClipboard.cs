using RelaxDesk.Core;

namespace RelaxDesk.Host;

// Explicit loopback-only test backend. Never reads/writes the Windows clipboard.
internal sealed class TestTextClipboard(bool fail = false) : ITextClipboard
{
    long version = 1; int reads;
    string text = "initial test clipboard";
    public bool TryRead(out ClipboardSnapshot snapshot)
    {
        if (fail) throw new IOException("Injected clipboard backend failure");
        if (++reads == 3) { text = "Host 中文 / 日本語\r\nclipboard test"; version++; }
        snapshot = new(version, text); return true;
    }
    public bool TryWrite(string value, long expectedVersion, out long writtenVersion)
    {
        writtenVersion = version;
        if (version != expectedVersion) return false;
        text = value; writtenVersion = ++version;
        Console.WriteLine("test_clipboard_remote_applied=True content_logged=False"); return true;
    }
    public void Dispose() => Console.WriteLine("test_clipboard_disposed=True");
}
