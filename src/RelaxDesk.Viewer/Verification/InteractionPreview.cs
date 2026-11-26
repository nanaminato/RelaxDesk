using System.IO;
using System.Text.Json;
using System.Windows;

namespace RelaxDesk.Viewer;

public sealed partial class ViewerWindow
{
    internal static void RunInteractionPreview(Application app, string output)
    {
        Directory.CreateDirectory(output);
        var window = new ViewerWindow();
        int menus = 0, fullscreenClicks = 0, pinClicks = 0;
        void Record() => File.WriteAllText(Path.Combine(output, "interaction.json"), JsonSerializer.Serialize(new
        {
            synthetic_preview = true, menus, fullscreenClicks, pinClicks,
            horizontalPosition = window.connectionBarPreferences.HorizontalPosition,
            pinned = window.connectionBarPreferences.Pinned, fullScreen = window.fullScreen
        }));
        window.Loaded += (_, _) =>
        {
            window.StartLocalPreview(); window.ToggleFullScreen();
            var bar = window.Toolbar!;
            bar.ShortcutsButton.Click += (_, _) => { menus++; Record(); };
            bar.FullScreenButton.Click += (_, _) => { fullscreenClicks++; Record(); };
            bar.PinButton.Click += (_, _) => { pinClicks++; Record(); };
            bar.Surface.MouseLeftButtonUp += (_, _) => Record();
            window.Closed += (_, _) => Record();
            Record();
        };
        app.Run(window);
    }
}
