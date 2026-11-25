using System.ComponentModel;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    readonly CheckBox rememberCredentials = new() { Content = "记住我的凭据(_R)" };
    ViewerCredentialStore? credentialStore;
    bool loadingCredential;
    string? credentialEndpoint;
    string? CurrentCredentialEndpoint() => IPAddress.TryParse(host.Text.Trim(), out var ip) &&
        int.TryParse(port.Text, out int number) && number is >= 1 and <= 65535
            ? new IPEndPoint(ip, number).ToString() : null;

    void ConfigureSavedCredentials()
    {
        if (!App.VerificationMode) credentialStore = new();
        rememberCredentials.ToolTip = "由 Windows 凭据管理器保护，仅供当前 Windows 用户使用。每个地址保存一组登录凭据。";
        host.TextChanged += (_, _) => LoadSavedCredential(endpointChanged: true);
        port.TextChanged += (_, _) => LoadSavedCredential(endpointChanged: true);
        loginName.TextChanged += (_, _) => LoadSavedCredential(endpointChanged: false);
        // History clears temporary secrets first; refill from the separate opted-in store.
        recentConnections.SelectionChanged += (_, _) =>
        {
            if (recentConnections.SelectedItem is not null) LoadSavedCredential(endpointChanged: false);
        };
        rememberCredentials.Unchecked += (_, _) =>
        {
            if (loadingCredential || credentialStore is null || CurrentCredentialEndpoint() is not { } endpoint) return;
            try
            {
                if (credentialStore.Read(endpoint)?.UserName == loginName.Text.Trim()) credentialStore.Delete(endpoint);
            }
            catch (Exception error) when (CredentialError(error)) { status.Text = "无法删除保存的凭据，请在 Windows 凭据管理器中移除。"; }
        };
        Loaded += (_, _) => LoadSavedCredential(endpointChanged: true);
    }
    static bool CredentialError(Exception error) => error is Win32Exception or System.IO.IOException or UnauthorizedAccessException or System.Security.SecurityException;

    void LoadSavedCredential(bool endpointChanged)
    {
        if (loadingCredential || credentialStore is null || connectionRun is not null) return;
        string? endpoint = CurrentCredentialEndpoint();
        if (endpointChanged && endpoint == credentialEndpoint) return;
        loadingCredential = true;
        try
        {
            token.Clear(); rememberCredentials.IsChecked = false;
            if (endpointChanged) { credentialEndpoint = endpoint; loginName.Clear(); }
            if (endpoint is null || credentialStore.Read(endpoint) is not { } saved) return;
            if (!endpointChanged && loginName.Text.Trim() != saved.UserName) return;
            loginName.Text = saved.UserName; token.Password = saved.Password; rememberCredentials.IsChecked = true;
        }
        catch (Exception error) when (CredentialError(error)) { status.Text = "保存的凭据暂不可用，请手动输入。"; }
        finally { loadingCredential = false; }
    }
    void SaveConnectedCredential(ConnectionTarget target, bool remember)
    {
        if (!remember || credentialStore is null || target.UserName is null) return;
        try { credentialStore.Save(new IPEndPoint(IPAddress.Parse(target.Host), target.Port).ToString(), target.UserName, target.Token); }
        catch (Exception error) when (CredentialError(error)) { ViewerSessionLog.Record("credential-save-failed", new InvalidOperationException("Windows credential storage unavailable")); }
    }
}
