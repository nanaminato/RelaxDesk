using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using RelaxDesk.Core;

namespace RelaxDesk.Mobile;

/// <summary>
/// Microsoft Remote Desktop (Windows App) inspired PC list home screen using native Android Views.
/// </summary>
[Activity(Label = "RelaxDesk", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    static readonly Color BgDark = Color.Rgb(18, 20, 24);
    static readonly Color SurfaceDark = Color.Rgb(30, 34, 42);
    static readonly Color AccentBlue = Color.Rgb(0, 120, 212);
    static readonly Color TextPrimary = Color.Rgb(242, 244, 248);
    static readonly Color TextSecondary = Color.Rgb(160, 170, 185);

    LinearLayout pcListContainer = null!;
    TextView emptyHint = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(BgDark);

        // Top AppBar
        var appBar = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        appBar.SetBackgroundColor(Color.Rgb(24, 27, 34));
        appBar.SetPadding(Dp(20), Dp(14), Dp(12), Dp(14));
        appBar.SetGravity(GravityFlags.CenterVertical);

        // Android 15+ (targetSdk 35+) enforces edge-to-edge; apply system bar & cutout insets.
        root.SetOnApplyWindowInsetsListener(new InsetsListener((top, bottom, left, right) =>
        {
            appBar.SetPadding(Dp(20) + left, Dp(14) + top, Dp(12) + right, Dp(14));
            root.SetPadding(0, 0, 0, bottom);
        }));

        var title = new TextView(this) { Text = "RelaxDesk", TextSize = 20f };
        title.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        title.SetTextColor(TextPrimary);
        appBar.AddView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        var addBtn = MakePillButton("+ 添加电脑", AccentBlue);
        addBtn.Click += (_, _) => ShowHostDialog(null);
        appBar.AddView(addBtn);

        var diagBtn = MakePillButton("诊断", Color.Rgb(52, 58, 70));
        var diagLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        diagLp.LeftMargin = Dp(8);
        diagBtn.Click += (_, _) => ShowDiagnosticsMenu();
        appBar.AddView(diagBtn, diagLp);

        root.AddView(appBar, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        // Section subtitle
        var section = new TextView(this) { Text = "已保存的 Windows 电脑", TextSize = 13f };
        section.SetTextColor(TextSecondary);
        section.SetPadding(Dp(20), Dp(16), Dp(20), Dp(8));
        root.AddView(section);

        // Scrollable PC cards container
        var scroll = new ScrollView(this);
        pcListContainer = new LinearLayout(this) { Orientation = Orientation.Vertical };
        pcListContainer.SetPadding(Dp(16), Dp(4), Dp(16), Dp(24));

        emptyHint = new TextView(this)
        {
            Text = "尚未添加 Windows 电脑。\n点击右上角「+ 添加电脑」输入局域网 IP 与 SHA-256 证书指纹即可开始远程控制。",
            TextSize = 14f,
            Gravity = GravityFlags.Center
        };
        emptyHint.SetTextColor(TextSecondary);
        emptyHint.SetPadding(Dp(24), Dp(48), Dp(24), Dp(48));

        scroll.AddView(pcListContainer);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        SetContentView(root);
        RefreshPcList();
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshPcList();
    }

    void RefreshPcList()
    {
        pcListContainer.RemoveAllViews();
        var hosts = ConnectionStore.Load(this);
        if (hosts.Count == 0)
        {
            pcListContainer.AddView(emptyHint);
            return;
        }

        foreach (var entry in hosts)
            pcListContainer.AddView(BuildPcCard(entry));
    }

    View BuildPcCard(SavedHostEntry entry)
    {
        var card = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        card.SetGravity(GravityFlags.CenterVertical);
        card.SetPadding(Dp(16), Dp(14), Dp(12), Dp(14));
        card.Background = MakeRoundedDrawable(SurfaceDark, Dp(12));

        var cardLp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        cardLp.BottomMargin = Dp(10);
        card.LayoutParameters = cardLp;

        // Monitor icon badge
        var badge = new TextView(this) { Text = "PC", TextSize = 14f, Gravity = GravityFlags.Center };
        badge.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        badge.SetTextColor(Color.White);
        badge.Background = MakeRoundedDrawable(AccentBlue, Dp(10));
        var badgeLp = new LinearLayout.LayoutParams(Dp(46), Dp(46));
        badgeLp.RightMargin = Dp(14);
        card.AddView(badge, badgeLp);

        // Info column
        var info = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var nameView = new TextView(this)
        {
            Text = string.IsNullOrWhiteSpace(entry.Name) ? entry.Host : entry.Name,
            TextSize = 16f
        };
        nameView.SetTypeface(Typeface.DefaultBold, TypefaceStyle.Bold);
        nameView.SetTextColor(TextPrimary);

        var detailView = new TextView(this)
        {
            Text = $"{entry.Host}:{entry.Port}  •  {entry.Username}",
            TextSize = 13f
        };
        detailView.SetTextColor(TextSecondary);
        info.AddView(nameView);
        info.AddView(detailView);
        card.AddView(info, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        // Edit button
        var moreBtn = MakePillButton("编辑", Color.Rgb(46, 52, 64));
        moreBtn.Click += (_, _) => ShowHostDialog(entry);
        card.AddView(moreBtn);

        // Tap card -> launch session directly
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

    void ShowHostDialog(SavedHostEntry? existing)
    {
        var container = new LinearLayout(this) { Orientation = Orientation.Vertical };
        container.SetPadding(Dp(20), Dp(12), Dp(20), Dp(4));

        var nameEdit = AddDialogField(container, "电脑名称（可选，如 办公室主机）", existing?.Name ?? "");
        var hostEdit = AddDialogField(container, "局域网 IP（如 192.168.1.10）", existing?.Host ?? "");
        var portEdit = AddDialogField(container, "端口（默认 48200）", (existing?.Port ?? 48200).ToString(), InputTypes.ClassNumber);
        var pinEdit = AddDialogField(container, "64 位 SHA-256 证书指纹", existing?.Fingerprint ?? "");
        var userEdit = AddDialogField(container, "Windows 登录用户名", existing?.Username ?? "");

        var builder = new AlertDialog.Builder(this)
            .SetTitle(existing is null ? "添加 Windows 电脑" : "编辑电脑")!
            .SetView(container)!
            .SetPositiveButton("保存", (_, _) =>
            {
                if (!FirstFrameSession.TryEndpoint(hostEdit.Text ?? "", portEdit.Text ?? "", pinEdit.Text ?? "",
                        out var ip, out var port, out var pin))
                {
                    Toast.MakeText(this, "请输入有效的局域网 IP、端口和 64 位 SHA-256 指纹", ToastLength.Long)?.Show();
                    return;
                }
                string user = (userEdit.Text ?? "").Trim();
                if (!PasswordVerifier.ValidUserName(user))
                {
                    Toast.MakeText(this, "请输入有效的用户名（6-64 字符）", ToastLength.Long)?.Show();
                    return;
                }
                string displayName = (nameEdit.Text ?? "").Trim();
                if (displayName.Length == 0) displayName = ip;
                ConnectionStore.SaveOrUpdate(this, new SavedHostEntry(displayName, ip, port, pin, user, DateTime.UtcNow.Ticks));
                RefreshPcList();
            })!
            .SetNegativeButton("取消", (_, _) => { })!;

        if (existing is not null)
        {
            builder.SetNeutralButton("删除", (_, _) =>
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
        string[] items = ["本地设备密钥自检 (Android Keystore)", "局域网首帧连接探针 (A1 诊断)"];
        new AlertDialog.Builder(this)
            .SetTitle("诊断与自检")!
            .SetItems(items, async (_, e) =>
            {
                if (e.Which == 0)
                {
                    try
                    {
                        await Task.Run(DeviceKeyProbe.Run);
                        Toast.MakeText(this, "Keystore 签名自检通过：同一密钥可重开，指纹变化时拒绝。", ToastLength.Long)?.Show();
                    }
                    catch (Exception ex)
                    {
                        Toast.MakeText(this, $"Keystore 自检未通过：{ex.GetType().Name}", ToastLength.Long)?.Show();
                    }
                }
                else if (e.Which == 1)
                {
                    StartActivity(typeof(RemoteProbeActivity));
                }
            })!
            .Show();
    }

    Button MakePillButton(string text, Color bg)
    {
        var btn = new Button(this) { Text = text, TextSize = 13f };
        btn.SetTextColor(Color.White);
        btn.SetAllCaps(false);
        btn.SetPadding(Dp(14), Dp(6), Dp(14), Dp(6));
        btn.Background = MakeRoundedDrawable(bg, Dp(18));
        return btn;
    }

    static GradientDrawable MakeRoundedDrawable(Color color, float radius)
    {
        var d = new GradientDrawable();
        d.SetColor(color);
        d.SetCornerRadius(radius);
        return d;
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
