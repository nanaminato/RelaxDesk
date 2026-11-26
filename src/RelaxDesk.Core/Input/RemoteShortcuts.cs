namespace RelaxDesk.Core;

public enum RemoteShortcut { Copy, Paste, Cut, SelectAll, Undo, Redo, Save, Find, AddressBar, TaskManager, CloseWindow, SwitchWindow, Explorer, Desktop, Run }
public readonly record struct KeyTransition(int Code, bool Up);

public static class RemoteShortcuts
{
    public static KeyTransition[] Create(RemoteShortcut shortcut)
    {
        int[] keys = shortcut switch
        {
            RemoteShortcut.Copy => [0xA2, 0x43], RemoteShortcut.Paste => [0xA2, 0x56],
            RemoteShortcut.Cut => [0xA2, 0x58], RemoteShortcut.SelectAll => [0xA2, 0x41],
            RemoteShortcut.Undo => [0xA2, 0x5A], RemoteShortcut.Redo => [0xA2, 0x59],
            RemoteShortcut.Save => [0xA2, 0x53], RemoteShortcut.Find => [0xA2, 0x46],
            RemoteShortcut.AddressBar => [0xA2, 0x4C], RemoteShortcut.TaskManager => [0xA2, 0xA0, 0x1B],
            RemoteShortcut.CloseWindow => [0xA4, 0x73], RemoteShortcut.SwitchWindow => [0xA4, 0x09],
            RemoteShortcut.Explorer => [0x5B, 0x45], RemoteShortcut.Desktop => [0x5B, 0x44],
            RemoteShortcut.Run => [0x5B, 0x52],
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut))
        };
        return keys.Select(key => new KeyTransition(key, false))
            .Concat(keys.Reverse().Select(key => new KeyTransition(key, true))).ToArray();
    }
}
