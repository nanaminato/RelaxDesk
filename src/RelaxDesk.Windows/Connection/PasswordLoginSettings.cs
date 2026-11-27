using RelaxDesk.Core.Localization;
using System.Text.Json;
using Microsoft.Win32;
using RelaxDesk.Core;

namespace RelaxDesk.Windows;

public static class PasswordLoginSettings
{
    const string HostKey = @"Software\RelaxDesk\HostLogin";
    const string DeviceCredentialKey = @"Software\RelaxDesk\DeviceCredentials";
    const string ViewerKey = @"Software\RelaxDesk\ServerPins";

    public static PasswordVerifier? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(HostKey);
        if (key?.GetValue("Verifier") is not string json) return null;
        if (json.Length > 1024 || JsonSerializer.Deserialize<PasswordVerifier>(json) is not { IsValid: true } verifier)
            throw new InvalidDataException(Strings.Get("Ui.PasswordLoginSettings.Text151"));
        return verifier;
    }

    public static void Save(PasswordVerifier verifier)
    {
        using var key = Registry.CurrentUser.CreateSubKey(HostKey);
        key.SetValue("Verifier", JsonSerializer.Serialize(verifier), RegistryValueKind.String);
    }

    public static string? ReadServerPin(string endpoint)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ViewerKey);
        return key?.GetValue(endpoint) as string;
    }

    public static string? ReadDeviceCredential(string endpoint)
    {
        using var key = Registry.CurrentUser.OpenSubKey(DeviceCredentialKey);
        return key?.GetValue(endpoint) is string { Length: <= 800 } credential ? credential : null;
    }

    public static void SaveDeviceCredential(string endpoint, string credential)
    {
        if (credential.Length is < 16 or > 800) throw new ArgumentException("Invalid device credential", nameof(credential));
        using var key = Registry.CurrentUser.CreateSubKey(DeviceCredentialKey);
        key.SetValue(endpoint, credential, RegistryValueKind.String);
    }

    public static void SaveServerPin(string endpoint, string? pin)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ViewerKey);
        if (pin is null)
        {
            key.DeleteValue(endpoint, throwOnMissingValue: false);
            using var credentials = Registry.CurrentUser.OpenSubKey(DeviceCredentialKey, writable: true);
            credentials?.DeleteValue(endpoint, throwOnMissingValue: false);
        }
        else key.SetValue(endpoint, pin, RegistryValueKind.String);
    }

}
