using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;
using Resource = RelaxDesk.Android.Resource;

namespace RelaxDesk.Mobile;

/// <summary>
/// PC list home screen backed by Resources/layout/activity_main.xml and multi-language strings.
/// </summary>
[Activity(Label = "RelaxDesk", MainLauncher = true, Exported = true, Theme = "@android:style/Theme.DeviceDefault.NoActionBar")]
public sealed class MainActivity : Activity
{
    LinearLayout appBar = null!;
    LinearLayout pcListContainer = null!;
    Button btnAddPc = null!, btnLanguage = null!, btnDiagnostics = null!;
    TextView txtSectionTitle = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        AndroidStrings.Init(this);
        SetContentView(Resource.Layout.activity_main);

        var root = FindViewById<View>(Resource.Id.main_root)!;
        appBar = FindViewById<LinearLayout>(Resource.Id.main_app_bar)!;
        pcListContainer = FindViewById<LinearLayout>(Resource.Id.pc_list_container)!;
        btnAddPc = FindViewById<Button>(Resource.Id.btn_add_pc)!;
        btnLanguage = FindViewById<Button>(Resource.Id.btn_language)!;
        btnDiagnostics = FindViewById<Button>(Resource.Id.btn_diagnostics)!;
        txtSectionTitle = FindViewById<TextView>(Resource.Id.txt_section_title)!;

        // Android 15+ edge-to-edge: apply system bar & display cutout insets cleanly.
        root.SetOnApplyWindowInsetsListener(new InsetsListener((top, bottom, left, right) =>
        {
            appBar.SetPadding(Dp(20) + left, Dp(14) + top, Dp(12) + right, Dp(14));
            root.SetPadding(0, 0, 0, bottom);
        }));

        btnAddPc.Click += (_, _) => ShowHostDialog(null);
        btnLanguage.Click += (_, _) => ShowLanguageDialog();
        btnDiagnostics.Click += (_, _) => ShowDiagnosticsMenu();

        ApplyLocalization();
        RefreshPcList();
    }

    protected override void OnResume()
    {
        base.OnResume();
        ApplyLocalization();
        RefreshPcList();
    }

    void ApplyLocalization()
    {
        btnAddPc.Text = AndroidStrings.Get("add_pc");
        btnLanguage.Text = AndroidStrings.Get("language");
        btnDiagnostics.Text = AndroidStrings.Get("diagnostics");
        txtSectionTitle.Text = AndroidStrings.Get("saved_pcs");
    }

    void RefreshPcList()
    {
        pcListContainer.RemoveAllViews();
        var hosts = ConnectionStore.Load(this);
        if (hosts.Count == 0)
        {
            var empty = new TextView(this)
            {
                Text = AndroidStrings.Get("empty_hint"),
                TextSize = 14f,
                Gravity = GravityFlags.Center
            };
            empty.SetTextColor(Color.Rgb(160, 170, 185));
            empty.SetPadding(Dp(24), Dp(48), Dp(24), Dp(48));
            pcListContainer.AddView(empty);
            return;
        }

        var inflater = LayoutInflater!;
        foreach (var entry in hosts)
        {
            var card = inflater.Inflate(Resource.Layout.item_pc_card, pcListContainer, false)!;
            card.FindViewById<TextView>(Resource.Id.card_pc_name)!.Text =
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Host : entry.Name;
            card.FindViewById<TextView>(Resource.Id.card_pc_detail)!.Text =
                $"{entry.Host}:{entry.Port}  •  {entry.Username}";
            var editBtn = card.FindViewById<Button>(Resource.Id.card_btn_edit)!;
            editBtn.Text = AndroidStrings.Get("edit");
            editBtn.Click += (_, _) => ShowHostDialog(entry);
            card.Click += (_, _) => LaunchSession(entry);
            pcListContainer.AddView(card);
        }
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
        new AlertDialog.Builder(this)
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

    void ShowHostDialog(SavedHostEntry? existing)
    {
        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetPadding(Dp(20), Dp(12), Dp(20), Dp(4));

        var nameEdit = AddDialogField(container, AndroidStrings.Get("hint_name"), existing?.Name ?? "");
        var hostEdit = AddDialogField(container, AndroidStrings.Get("hint_host"), existing?.Host ?? "");
        var portEdit = AddDialogField(container, AndroidStrings.Get("hint_port"), (existing?.Port ?? 48200).ToString(), InputTypes.ClassNumber);
        var pinEdit = AddDialogField(container, AndroidStrings.Get("hint_pin"), existing?.Fingerprint ?? "");
        var userEdit = AddDialogField(container, AndroidStrings.Get("hint_user"), existing?.Username ?? "");

        var builder = new AlertDialog.Builder(this)
            .SetTitle(AndroidStrings.Get(existing is null ? "dialog_add_pc" : "dialog_edit_pc"))!
            .SetView(container)!
            .SetPositiveButton(AndroidStrings.Get("save"), (_, _) =>
            {
                if (!FirstFrameSession.TryEndpoint(hostEdit.Text ?? "", portEdit.Text ?? "", pinEdit.Text ?? "",
                        out var ip, out var port, out var pin))
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
                ConnectionStore.SaveOrUpdate(this, new SavedHostEntry(displayName, ip, port, pin, user, DateTime.UtcNow.Ticks));
                RefreshPcList();
            })!
            .SetNegativeButton(AndroidStrings.Get("cancel"), (_, _) => { })!;

        if (existing is not null)
        {
            builder.SetNeutralButton(AndroidStrings.Get("delete"), (_, _) =>
            {
                ConnectionStore.Delete(this, existing.Host, existing.Port);
                RefreshPcList();
            });
        }

        builder.Show();
    }

    EditText AddDialogField(LinearLayout parent, string hint, string value, InputTypes type = InputTypes.ClassText)
    {
        var edit = new EditText(this)
        {
            Hint = hint,
            Text = value,
            InputType = type,
            SaveEnabled = false,
            ImportantForAutofill = ImportantForAutofill.No
        };
        edit.SetSingleLine(true);
        parent.AddView(edit);
        return edit;
    }

    void ShowDiagnosticsMenu()
    {
        string[] items = [AndroidStrings.Get("diag_keystore"), AndroidStrings.Get("diag_probe")];
        new AlertDialog.Builder(this)
            .SetTitle(AndroidStrings.Get("diag_title"))!
            .SetItems(items, async (_, e) =>
            {
                if (e.Which == 0)
                {
                    try
                    {
                        await Task.Run(DeviceKeyProbe.Run);
                        Toast.MakeText(this, "Android Keystore OK", ToastLength.Long)?.Show();
                    }
                    catch (Exception ex)
                    {
                        Toast.MakeText(this, $"Keystore failed: {ex.GetType().Name}", ToastLength.Long)?.Show();
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
