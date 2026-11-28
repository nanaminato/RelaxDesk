using Android.App;
using Android.Content;
using Android.Content.Res;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Views;

namespace RelaxDesk.Mobile;

internal enum AppThemeMode
{
    System,
    Light,
    Dark
}

internal readonly record struct ThemePalette(
    bool IsLight,
    Color PageBackground,
    Color AppBarBackground,
    Color AppBarDivider,
    Color CardBackground,
    Color CardStroke,
    Color PrimaryText,
    Color SecondaryText,
    Color HintText,
    Color ButtonNeutralBg,
    Color ButtonNeutralText,
    Color AccentBg,
    Color AccentText,
    Color DangerBg,
    Color DangerText,
    Color OverlayScrim,
    Color SessionPillBg,
    Color SessionPillStroke,
    Color ExtraKeysBarBg,
    Color KeyBg,
    Color KeyText,
    Color HudBg,
    Color HudText);

/// <summary>
/// Manages Light / Dark / Follow-System theme preference and semantic color palettes
/// for all RelaxDesk Android activities and dialogs.
/// </summary>
internal static class AndroidTheme
{
    const string PrefsName = "relaxdesk_settings_v1";
    const string KeyTheme = "theme_mode";

    static readonly ThemePalette DarkPalette = new(
        IsLight: false,
        PageBackground: Color.ParseColor("#FF121418"),
        AppBarBackground: Color.ParseColor("#FF181B22"),
        AppBarDivider: Color.ParseColor("#FF252A34"),
        CardBackground: Color.ParseColor("#FF1E222A"),
        CardStroke: Color.ParseColor("#FF2B313D"),
        PrimaryText: Color.ParseColor("#FFF2F4F8"),
        SecondaryText: Color.ParseColor("#FFA0AAB9"),
        HintText: Color.ParseColor("#A0BEC8D7"),
        ButtonNeutralBg: Color.ParseColor("#FF343A46"),
        ButtonNeutralText: Color.White,
        AccentBg: Color.ParseColor("#FF0078D4"),
        AccentText: Color.White,
        DangerBg: Color.ParseColor("#FFD13438"),
        DangerText: Color.White,
        OverlayScrim: Color.ParseColor("#F5101318"),
        SessionPillBg: Color.ParseColor("#CD181C24"),
        SessionPillStroke: Color.ParseColor("#33FFFFFF"),
        ExtraKeysBarBg: Color.ParseColor("#DC141820"),
        KeyBg: Color.ParseColor("#FF2E3440"),
        KeyText: Color.White,
        HudBg: Color.ParseColor("#D91E222A"),
        HudText: Color.ParseColor("#EB82FFAA"));

    static readonly ThemePalette LightPalette = new(
        IsLight: true,
        PageBackground: Color.ParseColor("#FFF4F6FA"),
        AppBarBackground: Color.ParseColor("#FFFFFFFF"),
        AppBarDivider: Color.ParseColor("#FFE2E7F0"),
        CardBackground: Color.ParseColor("#FFFFFFFF"),
        CardStroke: Color.ParseColor("#FFDCE2EC"),
        PrimaryText: Color.ParseColor("#FF171C26"),
        SecondaryText: Color.ParseColor("#FF5A6577"),
        HintText: Color.ParseColor("#FF8893A4"),
        ButtonNeutralBg: Color.ParseColor("#FFE6EBF2"),
        ButtonNeutralText: Color.ParseColor("#FF1E2530"),
        AccentBg: Color.ParseColor("#FF0078D4"),
        AccentText: Color.White,
        DangerBg: Color.ParseColor("#FFD13438"),
        DangerText: Color.White,
        OverlayScrim: Color.ParseColor("#F2F4F6FA"),
        SessionPillBg: Color.ParseColor("#E8FFFFFF"),
        SessionPillStroke: Color.ParseColor("#26000000"),
        ExtraKeysBarBg: Color.ParseColor("#ECF2F5FA"),
        KeyBg: Color.ParseColor("#FFE2E8F0"),
        KeyText: Color.ParseColor("#FF1A202C"),
        HudBg: Color.ParseColor("#E6FFFFFF"),
        HudText: Color.ParseColor("#FF0E7A3E"));

    static AppThemeMode currentMode = AppThemeMode.System;

    public static AppThemeMode CurrentMode => currentMode;

    public static void Init(Context context)
    {
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        string? saved = prefs?.GetString(KeyTheme, null);
        currentMode = saved switch
        {
            "light" => AppThemeMode.Light,
            "dark" => AppThemeMode.Dark,
            _ => AppThemeMode.System
        };
    }

    public static void SetMode(Context context, AppThemeMode mode)
    {
        currentMode = mode;
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        using var editor = prefs?.Edit();
        editor?.PutString(KeyTheme, mode switch
        {
            AppThemeMode.Light => "light",
            AppThemeMode.Dark => "dark",
            _ => "system"
        });
        editor?.Apply();
    }

    public static bool IsLight(Context context) => currentMode switch
    {
        AppThemeMode.Light => true,
        AppThemeMode.Dark => false,
        _ => ((context.Resources?.Configuration?.UiMode ?? UiMode.NightUndefined) & UiMode.NightMask) != UiMode.NightYes
    };

    public static ThemePalette GetPalette(Context context) => IsLight(context) ? LightPalette : DarkPalette;

    public static AlertDialog.Builder CreateDialogBuilder(Context context)
    {
        int themeResId = IsLight(context)
            ? global::Android.Resource.Style.ThemeDeviceDefaultLightDialogAlert
            : global::Android.Resource.Style.ThemeDeviceDefaultDialogAlert;
        return new AlertDialog.Builder(context, themeResId);
    }

    public static void ApplyWindowChrome(Activity activity, bool immersiveDark = false)
    {
        var window = activity.Window;
        if (window is null) return;

        bool light = !immersiveDark && IsLight(activity);
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            var controller = window.InsetsController;
            if (controller is not null)
            {
                int mask = (int)(WindowInsetsControllerAppearance.LightStatusBars | WindowInsetsControllerAppearance.LightNavigationBars);
                controller.SetSystemBarsAppearance(light ? mask : 0, mask);
            }
        }
    }

    public static GradientDrawable MakeRounded(Color fill, float radiusPx, Color? strokeColor = null, int strokeWidthPx = 0)
    {
        var drawable = new GradientDrawable();
        drawable.SetColor(fill);
        drawable.SetCornerRadius(radiusPx);
        if (strokeColor.HasValue && strokeWidthPx > 0)
            drawable.SetStroke(strokeWidthPx, strokeColor.Value);
        return drawable;
    }
}
