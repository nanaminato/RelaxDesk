using RelaxDesk.SharedUI;
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
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or System.Security.SecurityException)
        { LocalizedText.Bind(status,"Text","Ui.ViewerPreferencesUi.Text294"); }
        ConfigureVideoPreferenceChoices();
        ApplyPreferences();
        foreach (var check in PreferenceChecks())
        {
            check.Checked += (_, _) => SavePreferences();
            check.Unchecked += (_, _) => SavePreferences();
        }
        networkMode.SelectionChanged += (_, _) => SavePreferences();
        fileRateLimit.SelectionChanged += (_, _) => SavePreferences();
        videoFrameRate.SelectionChanged += (_, _) => SavePreferences();
        videoBitrate.SelectionChanged += (_, _) => SavePreferences();
        Closing += (_, _) => { SavePreferences(); preferencesOperation.GetAwaiter().GetResult(); };
    }
    CheckBox[] PreferenceChecks()=>[startFullScreen,gpuRendering,rememberConnections,autoConnectionQuality];
    void ApplyPreferences()
    {
        loadingPreferences = true;
        try
        {
            bool[] values=[preferences.FullScreen,preferences.Gpu,preferences.RememberConnections,preferences.AutomaticVideoQuality];
            var controls = PreferenceChecks();
            for (int i = 0; i < controls.Length; i++) controls[i].IsChecked = values[i];
            networkMode.SelectedIndex = preferences.Network; fileRateLimit.SelectedIndex = preferences.FileRate;
            ApplyVideoPreferenceValues();
            viewport = new(preferences.Zoom, 0, 0);
        }
        finally { loadingPreferences = false; }
    }
    void ConfigureVideoPreferenceChoices()
    {
        foreach (int fps in new[] { 15, 30, 60, 120, 144 })
            videoFrameRate.Items.Add(new ComboBoxItem { Content = $"{fps} FPS",Tag = fps });
        foreach (int rate in new[] { 2, 8, 20, 40, 60, 100 })
            videoBitrate.Items.Add(new ComboBoxItem { Content = $"{rate} Mbit/s",Tag = rate * 1_000_000 });
    }
    void ApplyVideoPreferenceValues()
    {
        bool previousLoading = loadingPreferences; loadingPreferences = true;
        try
        {
            void Select(ComboBox combo, int value, string label)
            {
                if (!combo.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, value)))
                    combo.Items.Add(new ComboBoxItem { Content = label,Tag = value });
                combo.SelectedValue = value;
            }
            Select(videoFrameRate, preferences.FramesPerSecond, $"{preferences.FramesPerSecond} FPS");
            Select(videoBitrate, preferences.Bitrate, $"{preferences.Bitrate / 1_000_000d:g} Mbit/s");
            autoConnectionQuality.IsChecked = preferences.AutomaticVideoQuality;
        }
        finally { loadingPreferences = previousLoading; }
    }
    void SavePreferences()
    {
        if (loadingPreferences) return;
        bool previousAutomatic = preferences.AutomaticVideoQuality;
        preferences = preferences with
        {
            FullScreen = startFullScreen.IsChecked == true, Gpu = gpuRendering.IsChecked == true,
            RememberConnections = rememberConnections.IsChecked == true, Network = Math.Max(0, networkMode.SelectedIndex),
            FileRate = Math.Max(0, fileRateLimit.SelectedIndex), Zoom = CurrentViewport.Zoom,
            FramesPerSecond = videoFrameRate.SelectedValue is int fps ? fps : preferences.FramesPerSecond,
            Bitrate = videoBitrate.SelectedValue is int bitrate ? bitrate : preferences.Bitrate,
            AutomaticVideoQuality = autoConnectionQuality.IsChecked == true
        };
        if (previousAutomatic != preferences.AutomaticVideoQuality) AutomaticVideoModeChanged();
        if (preferencesStore is not { } store) return;
        var snapshot = preferences;
        // Serialize writes off the Dispatcher and let the final close flush them.
        preferencesOperation = preferencesOperation.ContinueWith(_ =>
        {
            try { store.Save(snapshot); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException)
            { ViewerSessionLog.Record("viewer-preferences-save-failed", new IOException("Viewer preferences unavailable")); }
        }, TaskScheduler.Default);
    }
}
