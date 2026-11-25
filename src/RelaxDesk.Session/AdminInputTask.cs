using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;

namespace RelaxDesk.Session;

internal interface IAdminInputTaskRegistration
{
    void Register(string executable, string owner);
    void Remove(string executable, string owner);
}

internal sealed class AdminInputTask : IAdminInputTaskRegistration
{
    const string Arguments = "--local-consent $(Arg0) $(Arg1) $(Arg2) $(Arg3) \"$(Arg4)\"";
    const int InteractiveToken = 3, Highest = 1, Exec = 0, FileNotFound = unchecked((int)0x80070002);
    const int Create = 2, DoNotAddPrincipalAce = 16;

    static string Name(string executable)
    {
        string? worker = Path.GetDirectoryName(executable);
        string? release = Path.GetFileName(Path.GetDirectoryName(worker));
        if (release is not { Length: 36 } || !release.StartsWith("app-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(release[4..], "N", out _) || !string.Equals(worker,
                Path.Combine(ServiceFilePolicy.MachineRoot, release, "ElevatedInput"), StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable) != "RelaxDesk.ElevatedInput.exe")
            throw new SecurityException("Unexpected protected worker path");
        return "RelaxDesk.Input." + release[4..];
    }

    static string SecurityDescriptor(string owner) => $"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{owner})";

    static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service", throwOnError: true)!;
        dynamic scheduler = Activator.CreateInstance(type)!;
        scheduler.Connect();
        return scheduler;
    }

    static object? Find(dynamic folder, string name)
    {
        try { return folder.GetTask(name); }
        // COM interop can map HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND) to a
        // FileNotFoundException instead of a COMException.
        catch (Exception error) when (error.HResult == FileNotFound &&
            error is COMException or FileNotFoundException) { return null; }
    }

    public void Register(string executable, string owner)
    {
        owner = LocalAgentPipe.CanonicalUser(owner);
        var name = Name(executable);
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder("\\");
        if (Find(folder, name) is not null) throw new SecurityException("Elevated input task already exists");
        dynamic task = scheduler.NewTask(0);
        task.RegistrationInfo.Description = "RelaxDesk on-demand administrator input. Each connection requires local approval.";
        task.Principal.UserId = owner;
        task.Principal.LogonType = InteractiveToken;
        task.Principal.RunLevel = Highest;
        task.Settings.AllowDemandStart = true;
        task.Settings.StartWhenAvailable = false;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        task.Settings.MultipleInstances = 2; // IgnoreNew.
        dynamic action = task.Actions.Create(Exec);
        action.Path = executable;
        action.WorkingDirectory = Path.GetDirectoryName(executable);
        action.Arguments = Arguments;
        _ = folder.RegisterTaskDefinition(name, task, Create | DoNotAddPrincipalAce, owner, null, InteractiveToken, SecurityDescriptor(owner));
    }

    public void Remove(string executable, string owner)
    {
        owner = LocalAgentPipe.CanonicalUser(owner);
        var name = Name(executable);
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder("\\");
        if (Find(folder, name) is not { } task) return;
        Validate(task, executable, owner);
        folder.DeleteTask(name, 0);
    }

    public void CheckReady(string executable, string owner) => _ = Resolve(executable, owner);

    public void Run(string executable, AdminInputLaunchRequest request, string fingerprint, int sessionId)
    {
        if (sessionId <= 0 || fingerprint is not { Length: 64 } ||
            !fingerprint.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F') ||
            request.Display.Contains('"') || request.Display.EndsWith('\\'))
            throw new SecurityException("Invalid scheduled input request");
        dynamic task = Resolve(executable, request.Owner);
        string[] arguments = [fingerprint, .. request.Arguments()];
        const int UseSessionId = 4;
        _ = task.RunEx(arguments, UseSessionId, sessionId, null);
    }

    static object Resolve(string executable, string owner)
    {
        owner = LocalAgentPipe.CanonicalUser(owner);
        dynamic scheduler = Connect();
        dynamic folder = scheduler.GetFolder("\\");
        object task = Find(folder, Name(executable)) ?? throw new IOException("Elevated input task is not registered");
        Validate((dynamic)task, executable, owner);
        return task;
    }

    static void Validate(dynamic task, string executable, string owner)
    {
        dynamic definition = task.Definition;
        if ((int)definition.Actions.Count != 1 || (int)definition.Triggers.Count != 0 ||
            (string)definition.Principal.UserId != owner || (int)definition.Principal.LogonType != InteractiveToken ||
            (int)definition.Principal.RunLevel != Highest)
            throw new SecurityException("Unexpected elevated input task configuration");
        dynamic action = definition.Actions.Item(1);
        if ((int)action.Type != Exec || !string.Equals((string)action.Path, executable, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals((string)action.WorkingDirectory, Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase) ||
            (string)action.Arguments != Arguments)
            throw new SecurityException("Unexpected elevated input task action");
        var actual = new RawSecurityDescriptor((string)task.GetSecurityDescriptor(7));
        var expected = new RawSecurityDescriptor(SecurityDescriptor(owner));
        if (!actual.GetSddlForm(AccessControlSections.All & ~AccessControlSections.Audit).Equals(
            expected.GetSddlForm(AccessControlSections.All & ~AccessControlSections.Audit), StringComparison.Ordinal))
            throw new SecurityException("Unexpected elevated input task permissions");
    }
}
