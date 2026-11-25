using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RelaxDesk.Windows;

public sealed record ViewerCredential(string UserName, string Password)
{
    public override string ToString() => "ViewerCredential [redacted]";
}

// Generic credentials are protected by Windows for the current signed-in user.
// Scope includes the canonical endpoint; these are never RDP/Windows logon credentials.
public sealed class ViewerCredentialStore
{
    const uint Generic = 1, NotFound = 1168, PersistLocalMachine = 2;
    readonly string prefix;
    public ViewerCredentialStore(string scope = "RelaxDesk.Viewer") => prefix = scope;
    string Target(string endpoint) => prefix + "/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint)));

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist, AttributeCount;
        public nint Attributes;
        public string? Alias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ReadNative(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WriteNative(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool DeleteNative(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] static extern void CredFree(nint buffer);
    static void Fail() => throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

    public ViewerCredential? Read(string endpoint)
    {
        if (!ReadNative(Target(endpoint), Generic, 0, out var buffer))
        {
            if (Marshal.GetLastWin32Error() == NotFound) return null;
            Fail();
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(buffer);
            if (value.BlobSize > RelaxDesk.Core.PasswordVerifier.MaximumPasswordLength * 2 || value.BlobSize % 2 != 0 || !RelaxDesk.Core.PasswordVerifier.ValidUserName(value.UserName ?? ""))
                throw new InvalidDataException("Invalid saved Viewer credential");
            string password = Marshal.PtrToStringUni(value.Blob, (int)value.BlobSize / 2) ?? "";
            if (!RelaxDesk.Core.PasswordVerifier.ValidPasswordLength(password.Length))
                throw new InvalidDataException("Invalid saved Viewer credential");
            return new(value.UserName!, password);
        }
        finally { CredFree(buffer); }
    }

    public void Save(string endpoint, string userName, string password)
    {
        if (!RelaxDesk.Core.PasswordVerifier.ValidUserName(userName) ||
            !RelaxDesk.Core.PasswordVerifier.ValidPasswordLength(password.Length))
            throw new ArgumentException("Invalid Viewer credential");
        nint secret = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var value = new Credential { Type = Generic, TargetName = Target(endpoint),
                UserName = userName, Blob = secret, BlobSize = checked((uint)password.Length * 2), Persist = PersistLocalMachine };
            if (!WriteNative(ref value, 0)) Fail();
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(secret); }
    }

    public void Delete(string endpoint)
    {
        if (!DeleteNative(Target(endpoint), Generic, 0) && Marshal.GetLastWin32Error() != NotFound) Fail();
    }
}
