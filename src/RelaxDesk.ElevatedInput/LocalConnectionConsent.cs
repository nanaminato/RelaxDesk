using RelaxDesk.Core.Localization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Windows;

internal static class LocalConnectionConsent
{
    const int Yes = 6, No = 7;
    const uint ButtonClicked = 2, Timer = 4, ClickButton = 0x466;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int DialogCallback(nint window, uint notification, nuint value, nint reserved, nint data);

    [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
    struct DialogConfiguration
    {
        public uint Size;
        public nint Parent, Instance;
        public uint Flags, CommonButtons;
        public string Title;
        public nint MainIcon;
        public string Instruction, Content;
        public uint ButtonCount;
        public nint Buttons;
        public int DefaultButton;
        public uint RadioCount;
        public nint Radios;
        public int DefaultRadio;
        public nint Verification, ExpandedInformation, ExpandedControl, CollapsedControl;
        public nint FooterIcon, Footer;
        public DialogCallback Callback;
        public nint CallbackData;
        public uint Width;
    }

    [DllImport("comctl32.dll", ExactSpelling = true)]
    static extern int TaskDialogIndirect(in DialogConfiguration config, out int button, nint radio, nint verification);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    static extern nint SendMessage(nint window, uint message, nuint parameter, nint data);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool PeekNamedPipe(SafePipeHandle pipe, nint buffer, uint size, nint bytesRead, out uint available, nint remaining);

    public static bool ValidFingerprint(string fingerprint) =>
        fingerprint is { Length: 64 } && fingerprint.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');

    public static bool Confirm(string fingerprint, SafePipeHandle pipe)
    {
        if (!ValidFingerprint(fingerprint)) throw new ArgumentException("Invalid device fingerprint", nameof(fingerprint));
        var original = WindowsSessionAccess.Read();
        original.EnsureAllowed();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        bool Unavailable()
        {
            if (System.Diagnostics.Stopwatch.GetElapsedTime(start) >= TimeSpan.FromSeconds(30) ||
                !PeekNamedPipe(pipe, 0, 0, 0, out uint available, 0) || available != 0) return true;
            var current = WindowsSessionAccess.Read();
            return current.SessionChangeFrom(original) is not null || current.Denial is not null;
        }
        if (Unavailable()) return false;
        var config = new DialogConfiguration
        {
            Size = (uint)Marshal.SizeOf<DialogConfiguration>(), Flags = 0x808, CommonButtons = 0x6,
            Title = "RelaxDesk", MainIcon = 0xffff, DefaultButton = No,
            Instruction = Strings.Get("Consent.FullControlQuestion"),
            Content = Strings.Get("Consent.FullControlBody") + fingerprint,
            Callback = (window, notification, value, _, _) =>
            {
                if (notification == Timer && Unavailable()) SendMessage(window, ClickButton, No, 0);
                if (notification == ButtonClicked && value == Yes && Unavailable()) return 1;
                return 0;
            }
        };
        int result = TaskDialogIndirect(in config, out int button, 0, 0);
        return result == 0 && button == Yes && !Unavailable();
    }
}
