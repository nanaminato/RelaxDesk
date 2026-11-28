using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RelaxDesk.Windows;

namespace RelaxDesk.HostShell;

// Launch the ordinary Host in the existing local Administrator logon session.
internal static class RestrictedHostLaunch
{
    [StructLayout(LayoutKind.Sequential)]
    struct SidAttributes { public nint Sid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, Width, Height, Columns, Rows, Fill, Flags;
        public short Show, ReservedBytes;
        public nint ReservedData, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfoEx { public StartupInfo Startup; public nint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInfo { public nint Process, Thread; public int ProcessId, ThreadId; }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool CreateRestrictedToken(SafeAccessTokenHandle original, uint flags, int disableCount,
        [In] SidAttributes[] disabled, int deleteCount, nint deleted, int restrictCount, nint restricted,
        out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, ref SidAttributes value, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value,
        nuint bytes, nint previous, nint returned);

    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder commandLine,
        nint processAttributes, nint threadAttributes, bool inherit, uint flags, nint environment, string directory,
        ref StartupInfoEx startup, out ProcessInfo process);

    [DllImport("kernel32.dll", SetLastError = true)] static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(nint process, uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);

    internal sealed record Child(Process Process, StreamWriter Input, StreamReader Output, StreamReader Error);

    public static bool Required()
    {
        var token = WindowsInputPrivilege.ReadProcessToken();
        if (!token.Elevated || token.UiAccess || token.IntegrityRid != 0x3000) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return IsLocalAdministrator(identity.User?.Value, identity.Name, Environment.MachineName);
    }

    internal static bool IsLocalAdministrator(string? sid, string name, string machine) =>
        sid is not null && sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) &&
        sid.EndsWith("-500", StringComparison.Ordinal) &&
        name.StartsWith(machine + "\\", StringComparison.OrdinalIgnoreCase);

    // Preserve groups used by the interactive desktop and the current logon.
    // Administrative and site-specific group grants are deny-only in the child.
    static bool StandardGroup(string sid) => sid is "S-1-1-0" or "S-1-2-0" or "S-1-2-1" or "S-1-5-4" or
        "S-1-5-11" or "S-1-5-14" or "S-1-5-15" or "S-1-5-32-545" or "S-1-5-113" ||
        sid.StartsWith("S-1-5-5-", StringComparison.Ordinal);

    static nint CopySid(SecurityIdentifier sid)
    {
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        var address = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, address, bytes.Length);
        return address;
    }

    static SafeAccessTokenHandle RestrictedToken()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate |
            TokenAccessLevels.AssignPrimary | TokenAccessLevels.AdjustDefault | TokenAccessLevels.Impersonate);
        var groups = identity.Groups ?? throw new IOException("Could not read the current Windows logon groups.");
        var disabled = new List<SidAttributes>();
        SafeAccessTokenHandle? token = null;
        nint medium = 0;
        try
        {
            foreach (SecurityIdentifier sid in groups)
                if (!StandardGroup(sid.Value)) disabled.Add(new() { Sid = CopySid(sid) });
            const uint disableMaximumPrivileges = 1;
            Check(CreateRestrictedToken(identity.AccessToken, disableMaximumPrivileges, disabled.Count, disabled.ToArray(),
                0, 0, 0, 0, out token));
            var sidMedium = new SecurityIdentifier("S-1-16-8192");
            medium = CopySid(sidMedium);
            var label = new SidAttributes { Sid = medium, Attributes = 0x20 };
            Check(SetTokenInformation(token, 25, ref label, Marshal.SizeOf<SidAttributes>() + sidMedium.BinaryLength));
            WindowsInputPrivilege.EnsureOrdinaryToken(token);
            using var restrictedIdentity = new WindowsIdentity(token.DangerousGetHandle());
            if (identity.User is not { } owner || restrictedIdentity.User is not { } actual || !owner.Equals(actual) ||
                new WindowsPrincipal(restrictedIdentity).IsInRole(WindowsBuiltInRole.Administrator))
                throw new IOException("The restricted sharing token has an unexpected user or administrator group.");
            return token;
        }
        catch { token?.Dispose(); throw; }
        finally
        {
            foreach (var entry in disabled) Marshal.FreeHGlobal(entry.Sid);
            Marshal.FreeHGlobal(medium);
        }
    }

    static void Check(bool succeeded)
    {
        if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static Child Start(ProcessStartInfo start)
    {
        if (!Required() || !Path.IsPathFullyQualified(start.FileName) ||
            !Path.GetFileName(start.FileName).Equals("RelaxDesk.Host.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restricted sharing requires the installed RelaxDesk Host executable.");
        WindowsSessionAccess.EnsureAllowed();
        using var token = RestrictedToken();
        var input = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        AnonymousPipeServerStream? output = null, error = null;
        nint attributes = 0, inheritedHandles = 0, environment = 0;
        bool initialized = false, started = false;
        ProcessInfo child = default;
        Process? process = null;
        try
        {
            output = new(PipeDirection.In, HandleInheritability.Inheritable);
            error = new(PipeDirection.In, HandleInheritability.Inheritable);
            nint[] handles = [input.ClientSafePipeHandle.DangerousGetHandle(),
                output.ClientSafePipeHandle.DangerousGetHandle(), error.ClientSafePipeHandle.DangerousGetHandle()];
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            initialized = true;
            inheritedHandles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
            Marshal.Copy(handles, 0, inheritedHandles, handles.Length);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x20002, inheritedHandles,
                (nuint)(handles.Length * IntPtr.Size), 0, 0));
            var settings = new StartupInfoEx
            {
                Startup = new()
                {
                    Size = Marshal.SizeOf<StartupInfoEx>(), Desktop = @"winsta0\default", Flags = 0x100,
                    Input = handles[0], Output = handles[1], Error = handles[2]
                },
                Attributes = attributes
            };
            string variables = string.Join('\0', start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}")) + "\0";
            environment = Marshal.StringToHGlobalUni(variables);
            string command = string.Join(" ", start.ArgumentList.Prepend(start.FileName).Select(Quote));
            const uint extendedStartup = 0x00080000, unicodeEnvironment = 0x400, noWindow = 0x08000000, suspended = 4;
            Check(CreateProcessAsUser(token, start.FileName, new(command), 0, 0, true,
                extendedStartup | unicodeEnvironment | noWindow | suspended, environment,
                Path.GetDirectoryName(start.FileName)!, ref settings, out child));
            input.DisposeLocalCopyOfClientHandle();
            output.DisposeLocalCopyOfClientHandle();
            error.DisposeLocalCopyOfClientHandle();
            process = Process.GetProcessById(child.ProcessId);
            var launched = new Child(process, new StreamWriter(input, new UTF8Encoding(false)) { AutoFlush = true },
                new StreamReader(output, Console.OutputEncoding), new StreamReader(error, Console.OutputEncoding));
            Check(ResumeThread(child.Thread) != uint.MaxValue);
            started = true;
            return launched;
        }
        finally
        {
            if (!started)
            {
                if (child.Process != 0) TerminateProcess(child.Process, 1);
                process?.Dispose();
                input.Dispose();
                output?.Dispose();
                error?.Dispose();
            }
            if (child.Thread != 0) CloseHandle(child.Thread);
            if (child.Process != 0) CloseHandle(child.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(inheritedHandles);
            Marshal.FreeHGlobal(environment);
        }
    }

    internal static string Quote(string argument)
    {
        var quoted = new StringBuilder().Append('"');
        int slashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
}
