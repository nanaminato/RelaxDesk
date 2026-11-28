using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace RelaxDesk.Session;

/// <summary>
/// Launches a LocalSystem process inside the active physical console session
/// (winsta0\default) from the Session 0 broker service so the interactive host
/// can dynamically follow Winlogon/Default desktop switches for UAC control.
/// </summary>
public static class InteractiveSessionLauncher
{
    const uint TokenDuplicate = 0x0002;
    const uint TokenQuery = 0x0008;
    const uint TokenAssignPrimary = 0x0001;
    const uint TokenAdjustDefault = 0x0080;
    const uint TokenAdjustSessionId = 0x0100;
    const uint MaximumAllowed = 0x02000000;
    const int SecurityIdentification = 1;
    const int TokenPrimary = 1;
    const int TokenSessionId = 12;
    const uint CreateUnicodeEnvironment = 0x00000400;
    const uint CreateNoWindow = 0x08000000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public uint Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, Reserved2;
        public nint Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public nint Process, Thread;
        public uint ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("kernel32.dll")] static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(nint handle);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(SafeAccessTokenHandle existing, uint access, nint attributes,
        int impersonationLevel, int tokenType, out SafeAccessTokenHandle duplicate);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetTokenInformation(SafeAccessTokenHandle token, int infoClass, ref uint sessionId, uint length);
    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, string commandLine,
        nint processAttributes, nint threadAttributes, bool inheritHandles, uint creationFlags,
        nint environment, string? currentDirectory, ref StartupInfo startup, out ProcessInformation process);

    public static bool IsLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
    }

    public static uint ActiveConsoleSessionId()
    {
        uint sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId is 0 or uint.MaxValue)
            throw new IOException("No active physical console session is available.");
        return sessionId;
    }

    public static uint LaunchInConsoleSession(string executablePath, string arguments, uint? targetSessionId = null)
    {
        if (!IsLocalSystem())
            throw new SecurityException("Interactive SYSTEM session launch requires LocalSystem service identity.");
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            throw new ArgumentException("Executable path must be fully qualified.", nameof(executablePath));

        uint sessionId = targetSessionId ?? ActiveConsoleSessionId();
        if (sessionId is 0 or uint.MaxValue)
            throw new IOException("Target interactive session is invalid.");

        uint desired = TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault | TokenAdjustSessionId;
        if (!OpenProcessToken(GetCurrentProcess(), desired, out var currentToken))
            throw new Win32Exception(Marshal.GetLastWin32Error());

        using (currentToken)
        {
            if (!DuplicateTokenEx(currentToken, MaximumAllowed, 0, SecurityIdentification, TokenPrimary, out var sessionToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            using (sessionToken)
            {
                if (!SetTokenInformation(sessionToken, TokenSessionId, ref sessionId, sizeof(uint)))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                var startup = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfo>(),
                    Desktop = @"winsta0\default"
                };
                string commandLine = string.IsNullOrWhiteSpace(arguments)
                    ? $"\"{executablePath}\""
                    : $"\"{executablePath}\" {arguments}";
                string? workingDirectory = Path.GetDirectoryName(executablePath);

                if (!CreateProcessAsUser(sessionToken, executablePath, commandLine, 0, 0, false,
                    CreateUnicodeEnvironment | CreateNoWindow, 0, workingDirectory, ref startup, out var info))
                    throw new Win32Exception(Marshal.GetLastWin32Error());

                if (info.Thread != 0) CloseHandle(info.Thread);
                if (info.Process != 0) CloseHandle(info.Process);
                return info.ProcessId;
            }
        }
    }
}
