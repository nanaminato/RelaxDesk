using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using Orientation = Android.Widget.Orientation;
using Resource = RelaxDesk.Android.Resource;

namespace RelaxDesk.Mobile;

/// <summary>
/// PC list home screen backed by Resources/layout/activity_main.xml,
/// multi-language strings, Light/Dark/System theme, and responsive grid layout.
/// </summary>
[Activity(
    Label = "RelaxDesk",
    MainLauncher = true,
    Exported = true,
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.SmallestScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.UiMode)]
public sealed class MainActivity : Activity
{
    View root = null!;
    LinearLayout appBar = null!;
    View appBarDivider = null!;
    TextView mainTitle = null!;
    LinearLayout contentColumn = null!;
    LinearLayout pcListContainer = null!;
    Button btnAddPc = null!, btnMoreMenu = null!;
    TextView txtSectionTitle = null!;

    int insetTop, insetBottom, insetLeft, insetRight;
    int currentColumns = 1;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidStrings.Init(this);
        AndroidTheme.Init(this);
        SetContentView(Resource.Layout.activity_main);

        root = FindViewById<View>(Resource.Id.main_root)!;
        appBar = FindViewById<LinearLayout>(Resource.Id.main_app_bar)!;
        appBarDivider = FindViewById<View>(Resource.Id.main_app_bar_divider)!;
        mainTitle = FindViewById<TextView>(Resource.Id.main_title)!;
        contentColumn = FindViewById<LinearLayout>(Resource.Id.main_content_column)!;
        pcListContainer = FindViewById<LinearLayout>(Resource.Id.pc_list_container)!;
        btnAddPc = FindViewById<Button>(Resource.Id.btn_add_pc)!;
        btnMoreMenu = FindViewById<Button>(Resource.Id.btn_more_menu)!;
        txtSectionTitle = FindViewById<TextView>(Resource.Id.txt_section_title)!;

        // Android 15+ edge-to-edge: apply system bar & display cutout insets cleanly.
        root.SetOnApplyWindowInsetsListener(new InsetsListener((top, bottom, left, right) =>
        {
            insetTop = top;
            insetBottom = bottom;
            insetLeft = left;
            insetRight = right;
            ApplyResponsiveLayout();
        }));

        btnAddPc.Click += (_, _) => ShowHostDialog(null);
        btnMoreMenu.Click += (_, _) => ShowMoreMenu();

        ApplyLocalization();
        ApplyTheme();
        ApplyResponsiveLayout();
        RefreshPcList();
    }

    protected override void OnResume()
    {
        base.OnResume();
        AndroidStrings.Init(this);
        AndroidTheme.Init(this);
        ApplyLocalization();
        ApplyTheme();
        ApplyResponsiveLayout();
        RefreshPcList();
    }

    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        ApplyTheme();
        ApplyResponsiveLayout();
        RefreshPcList();
    }

    void ApplyLocalization()
    {
        btnAddPc.Text = AndroidStrings.Get("add_pc");
        btnMoreMenu.ContentDescription = AndroidStrings.Get("more_options");
        txtSectionTitle.Text = AndroidStrings.Get("saved_pcs");
    }

    void ApplyTheme()
    {
        var p = AndroidTheme.GetPalette(this);
        AndroidTheme.ApplyWindowChrome(this);

        root.SetBackgroundColor(p.PageBackground);
        appBar.SetBackgroundColor(p.AppBarBackground);
        appBarDivider.SetBackgroundColor(p.AppBarDivider);
        mainTitle.SetTextColor(p.PrimaryText);
        txtSectionTitle.SetTextColor(p.SecondaryText);

        btnAddPc.SetTextColor(p.AccentText);
        btnAddPc.Background = AndroidTheme.MakeRounded(p.AccentBg, Dp(18));

        btnMoreMenu.SetTextColor(p.ButtonNeutralText);
        btnMoreMenu.Background = AndroidTheme.MakeRounded(p.ButtonNeutralBg, Dp(18));
    }

    void ApplyResponsiveLayout()
    {
        var dm = Resources!.DisplayMetrics!;
        float density = Math.Max(1f, dm.Density);
        int safeWidthPx = Math.Max(1, dm.WidthPixels - insetLeft - insetRight);
        float widthDp = safeWidthPx / density;

        bool compact = widthDp < 360f;
        int horizPad = Dp(compact ? 12 : 16);
        int appBarStart = Dp(compact ? 14 : 18);
        int appBarEnd = Dp(compact ? 10 : 12);

        appBar.SetPadding(appBarStart + insetLeft, Dp(12) + insetTop, appBarEnd + insetRight, Dp(12));
        root.SetPadding(0, 0, 0, insetBottom);

        int maxContentWidthPx = Dp(1080);
        int targetWidth = safeWidthPx > maxContentWidthPx ? maxContentWidthPx : ViewGroup.LayoutParams.MatchParent;
        var colLp = contentColumn.LayoutParameters ?? new LinearLayout.LayoutParams(targetWidth, ViewGroup.LayoutParams.WrapContent);
        if (colLp.Width != targetWidth)
        {
            colLp.Width = targetWidth;
            contentColumn.LayoutParameters = colLp;
        }

        int sidePad = horizPad + (targetWidth == ViewGroup.LayoutParams.MatchParent ? Math.Max(insetLeft, insetRight) : 0);
        txtSectionTitle.SetPadding(sidePad + Dp(4), Dp(16), sidePad + Dp(4), Dp(8));
        pcListContainer.SetPadding(sidePad, Dp(4), sidePad, Dp(24));

        currentColumns = widthDp >= 900f ? 3 : widthDp >= 600f ? 2 : 1;
    }

    void ShowMoreMenu()
    {
        string[] items =
        [
            AndroidStrings.Get("select_language"),
            AndroidStrings.Get("select_theme"),
            AndroidStrings.Get("diag_title")
        ];
        AndroidTheme.CreateDialogBuilder(this)
            .SetItems(items, (_, e) =>
            {
                if (e.Which == 0) ShowLanguageDialog();
                else if (e.Which == 1) ShowThemeDialog();
                else if (e.Which == 2) ShowDiagnosticsMenu();
            })!
            .Show();
    }

    void RefreshPcList()
    {
        pcListContainer.RemoveAllViews();
        var p = AndroidTheme.GetPalette(this);
        var hosts = ConnectionStore.Load(this);
        if (hosts.Count == 0)
        {
            var empty = new TextView(this)
            {
                Text = AndroidStrings.Get("empty_hint"),
                TextSize = 14f,
                Gravity = GravityFlags.Center
            };
            empty.SetTextColor(p.SecondaryText);
            empty.SetPadding(Dp(24), Dp(48), Dp(24), Dp(48));
            pcListContainer.AddView(empty);
            return;
        }

        var inflater = LayoutInflater!;
        int cols = Math.Max(1, currentColumns);
        int gap = Dp(10);

        if (cols == 1)
        {
            foreach (var entry in hosts)
            {
                var card = InflatePcCard(inflater, pcListContainer, entry, p);
                pcListContainer.AddView(card);
            }
            return;
        }

        for (int i = 0; i < hosts.Count; i += cols)
        {
            var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
            var rowLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
            rowLp.BottomMargin = gap;
            row.LayoutParameters = rowLp;

            for (int c = 0; c < cols; c++)
            {
                int index = i + c;
                if (index < hosts.Count)
                {
                    var card = InflatePcCard(inflater, row, hosts[index], p);
                    var cardLp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
                    if (c > 0) cardLp.LeftMargin = gap;
                    card.LayoutParameters = cardLp;
                    row.AddView(card);
                }
                else
                {
                    var spacer = new View(this);
                    var spacerLp = new LinearLayout.LayoutParams(0, 1, 1f);
                    if (c > 0) spacerLp.LeftMargin = gap;
                    spacer.LayoutParameters = spacerLp;
                    row.AddView(spacer);
                }
            }

            pcListContainer.AddView(row);
        }
    }

    View InflatePcCard(LayoutInflater inflater, ViewGroup parent, SavedHostEntry entry, ThemePalette p)
    {
        var card = inflater.Inflate(Resource.Layout.item_pc_card, parent, false)!;
        card.Background = AndroidTheme.MakeRounded(p.CardBackground, Dp(12), p.CardStroke, Dp(1));

        var badge = card.FindViewById<TextView>(Resource.Id.card_pc_badge)!;
        badge.Background = AndroidTheme.MakeRounded(p.AccentBg, Dp(14));
        badge.SetTextColor(p.AccentText);

        var nameView = card.FindViewById<TextView>(Resource.Id.card_pc_name)!;
        nameView.Text = string.IsNullOrWhiteSpace(entry.Name) ? entry.Host : entry.Name;
        nameView.SetTextColor(p.PrimaryText);

        var detailView = card.FindViewById<TextView>(Resource.Id.card_pc_detail)!;
        detailView.Text = $"{entry.Host}:{entry.Port}  •  {entry.Username}";
        detailView.SetTextColor(p.SecondaryText);

        var editBtn = card.FindViewById<Button>(Resource.Id.card_btn_edit)!;
        editBtn.Text = AndroidStrings.Get("edit");
        editBtn.SetTextColor(p.ButtonNeutralText);
        editBtn.Background = AndroidTheme.MakeRounded(p.ButtonNeutralBg, Dp(18));
        editBtn.Click += (_, _) => ShowHostDialog(entry);

        card.Click += (_, _) => LaunchSession(entry);
        return card;
    }

    void LaunchSession(SavedHostEntry entry)
    {
        var intent = new Intent(this, typeof(RemoteSessionActivity));
        intent.PutExtra(RemoteSessionActivity.ExtraName, entry.Name);
        intent.PutExtra(RemoteSessionActivity.ExtraHost, entry.Host);
        intent.PutExtra(RemoteSessionActivity.ExtraPort, entry.Port);
        intent.PutExtra(RemoteSessionActivity.ExtraPin, entry.Fingerprint);
        intent.PutExtra(RemoteSessionActivity.ExtraUser, entry.Username);
        StartActivity(intent);
    }

    void ShowLanguageDialog()
    {
        string[] labels = AndroidStrings.SupportedLanguages.Select(l => l.DisplayName).ToArray();
        int checkedIndex = Array.FindIndex(AndroidStrings.SupportedLanguages, l => l.Code == AndroidStrings.CurrentLanguage);
        AndroidTheme.CreateDialogBuilder(this)
            .SetTitle(AndroidStrings.Get("select_language"))!
            .SetSingleChoiceItems(labels, checkedIndex, (sender, e) =>
            {
                AndroidStrings.SetLanguage(this, AndroidStrings.SupportedLanguages[e.Which].Code);
                ApplyLocalization();
                RefreshPcList();
                (sender as Dialog)?.Dismiss();
            })!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { })!
            .Show();
    }

    void ShowThemeDialog()
    {
        string[] labels =
        [
            AndroidStrings.Get("theme_system"),
            AndroidStrings.Get("theme_light"),
            AndroidStrings.Get("theme_dark")
        ];
        int checkedIndex = AndroidTheme.CurrentMode switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0
        };
        AndroidTheme.CreateDialogBuilder(this)
            .SetTitle(AndroidStrings.Get("select_theme"))!
            .SetSingleChoiceItems(labels, checkedIndex, (sender, e) =>
            {
                var mode = e.Which switch
                {
                    1 => AppThemeMode.Light,
                    2 => AppThemeMode.Dark,
                    _ => AppThemeMode.System
                };
                AndroidTheme.SetMode(this, mode);
                ApplyTheme();
                RefreshPcList();
                (sender as Dialog)?.Dismiss();
            })!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { })!
            .Show();
    }

    void ShowHostDialog(SavedHostEntry? existing)
    {
        var builder = AndroidTheme.CreateDialogBuilder(this);
        var dialogCtx = builder.Context!;
        var p = AndroidTheme.GetPalette(this);

        var container = new LinearLayout(dialogCtx) { Orientation = Orientation.Vertical };
        container.SetPadding(Dp(20), Dp(12), Dp(20), Dp(4));

        var nameEdit = AddDialogField(dialogCtx, container, p, AndroidStrings.Get("hint_name"), existing?.Name ?? "");
        var hostEdit = AddDialogField(dialogCtx, container, p, AndroidStrings.Get("hint_host"), existing?.Host ?? "");
        var portEdit = AddDialogField(dialogCtx, container, p, AndroidStrings.Get("hint_port"), (existing?.Port ?? 48200).ToString(), InputTypes.ClassNumber);
        var userEdit = AddDialogField(dialogCtx, container, p, AndroidStrings.Get("hint_user"), existing?.Username ?? "");

        var scroll = new ScrollView(dialogCtx);
        scroll.AddView(container);

        builder
            .SetTitle(AndroidStrings.Get(existing is null ? "dialog_add_pc" : "dialog_edit_pc"))!
            .SetView(scroll)!
            .SetPositiveButton(AndroidStrings.Get("save"), (_, _) =>
            {
                string ip = (hostEdit.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(ip) || !int.TryParse(portEdit.Text, out int port) || port is < 1 or > 65535)
                {
                    Toast.MakeText(this, AndroidStrings.Get("err_invalid_endpoint"), ToastLength.Long)?.Show();
                    return;
                }
                string user = (userEdit.Text ?? "").Trim();
                if (!PasswordVerifier.ValidUserName(user))
                {
                    Toast.MakeText(this, AndroidStrings.Get("err_invalid_user"), ToastLength.Long)?.Show();
                    return;
                }
                string displayName = (nameEdit.Text ?? "").Trim();
                if (displayName.Length == 0) displayName = ip;
                ConnectionStore.SaveOrUpdate(this, new SavedHostEntry(displayName, ip, port, existing?.Fingerprint ?? "", user, DateTime.UtcNow.Ticks));
                RefreshPcList();
            })!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { });

        if (existing is not null)
        {
            builder.SetNeutralButton(AndroidStrings.Get("delete"), (_, _) =>
            {
                ConnectionStore.Delete(this, existing.Host, existing.Port);
                PasswordStore.Clear(this, existing.Host, existing.Port, existing.Username);
                RefreshPcList();
            });
        }

        builder.Show();
    }

    EditText AddDialogField(Context dialogCtx, LinearLayout parent, ThemePalette p, string hint, string value, InputTypes type = InputTypes.ClassText)
    {
        var edit = new EditText(dialogCtx)
        {
            Hint = hint,
            Text = value,
            InputType = type,
            SaveEnabled = false,
            ImportantForAutofill = ImportantForAutofill.No
        };
        edit.SetSingleLine(true);
        edit.SetTextColor(p.PrimaryText);
        edit.SetHintTextColor(p.HintText);
        edit.BackgroundTintList = ColorStateList.ValueOf(p.CardStroke);
        parent.AddView(edit);
        return edit;
    }

    void ShowDiagnosticsMenu()
    {
        string[] items = [AndroidStrings.Get("diag_keystore"), AndroidStrings.Get("diag_probe")];
        AndroidTheme.CreateDialogBuilder(this)
            .SetTitle(AndroidStrings.Get("diag_title"))!
            .SetItems(items, async (_, e) =>
            {
                if (e.Which == 0)
                {
                    try
                    {
                        await Task.Run(DeviceKeyProbe.Run);
                        Toast.MakeText(this, AndroidStrings.Get("diag_keystore_ok"), ToastLength.Long)?.Show();
                    }
                    catch (Exception ex)
                    {
                        Toast.MakeText(this, AndroidStrings.Format("diag_keystore_fail", ex.GetType().Name), ToastLength.Long)?.Show();
                    }
                }
                else if (e.Which == 1)
                {
                    StartActivity(typeof(RemoteProbeActivity));
                }
            })!
            .Show();
    }

    int Dp(int dp) => (int)Math.Round(dp * Resources!.DisplayMetrics!.Density);

    sealed class InsetsListener(Action<int, int, int, int> onInsets) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var bars = insets.GetInsets(WindowInsets.Type.SystemBars() | WindowInsets.Type.DisplayCutout());
                onInsets(bars.Top, bars.Bottom, bars.Left, bars.Right);
            }
            return insets;
        }
    }
}
