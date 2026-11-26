using System.IO;
using System.Windows.Controls;
using RelaxDesk.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    ViewerPreferences preferences = new();
    ViewerPreferencesStore? preferencesStore;
    bool loadingPreferences;
    Task preferencesOperation = Task.CompletedTask;

    void ConfigurePreferences()
    {
        if (!App.VerificationMode)
            preferencesStore = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RelaxDesk", "preferences", "viewer.json"));
        try { if (preferencesStore is not null) preferences = preferencesStore.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.SecurityException)
        { status.Text = "无法读取连接设置，已使用默认值。"; }
        ApplyPreferences();
        foreach (var check in PreferenceChecks())
        {
            check.Checked += (_, _) => SavePreferences();
            check.Unchecked += (_, _) => SavePreferences();
        }
        networkMode.SelectionChanged += (_, _) => SavePreferences();
        fileRateLimit.SelectionChanged += (_, _) => SavePreferences();
        Closing += (_, _) => { SavePreferences(); preferencesOperation.GetAwaiter().GetResult(); };
    }
    CheckBox[] PreferenceChecks() => [startFullScreen, gpuRendering, requestTopology, requestClipboard,
        requestClipboardImage, requestClipboardHtml, requestFileClipboard, requestAudio, rememberConnections];
    void ApplyPreferences()
    {
        loadingPreferences = true;
        try
        {
            bool[] values = [preferences.FullScreen, preferences.Gpu, preferences.Topology, preferences.ClipboardText,
                preferences.ClipboardImage, preferences.ClipboardHtml, preferences.FileClipboard, preferences.Audio, preferences.RememberConnections];
            var controls = PreferenceChecks();
            for (int i = 0; i < controls.Length; i++) controls[i].IsChecked = values[i];
            networkMode.SelectedIndex = preferences.Network; fileRateLimit.SelectedIndex = preferences.FileRate;
            viewport = new(preferences.Zoom, 0, 0);
        }
        finally { loadingPreferences = false; }
    }
    void SavePreferences()
    {
        if (loadingPreferences) return;
        preferences = preferences with
        {
            FullScreen = startFullScreen.IsChecked == true, Gpu = gpuRendering.IsChecked == true,
            Topology = requestTopology.IsChecked == true, ClipboardText = requestClipboard.IsChecked == true,
            ClipboardImage = requestClipboardImage.IsChecked == true, ClipboardHtml = requestClipboardHtml.IsChecked == true,
            FileClipboard = requestFileClipboard.IsChecked == true, Audio = requestAudio.IsChecked == true,
            RememberConnections = rememberConnections.IsChecked == true, Network = Math.Max(0, networkMode.SelectedIndex),
            FileRate = Math.Max(0, fileRateLimit.SelectedIndex), Zoom = CurrentViewport.Zoom
        };
        if (preferencesStore is not { } store) return;
        var snapshot = preferences;
        // Serialize writes off the Dispatcher and let the final close flush them.
        preferencesOperation = preferencesOperation.ContinueWith(_ =>
        {
            try { store.Save(snapshot); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { ViewerSessionLog.Record("viewer-preferences-save-failed", new IOException("Viewer preferences unavailable")); }
        }, TaskScheduler.Default);
    }
}
