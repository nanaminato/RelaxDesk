using Android.Content;
using Java.Util;

namespace RelaxDesk.Mobile;

/// <summary>
/// Multi-language localization aligned with RelaxDesk desktop locales:
/// zh-CN, en, ja, fr, es. Persists user selection in SharedPreferences.
/// </summary>
internal static class AndroidStrings
{
    const string PrefsName = "relaxdesk_settings_v1";
    const string KeyLocale = "locale";

    public static readonly (string Code, string DisplayName)[] SupportedLanguages =
    [
        ("zh-CN", "简体中文"),
        ("en", "English"),
        ("ja", "日本語"),
        ("fr", "Français"),
        ("es", "Español")
    ];

    static string current = "zh-CN";

    public static string CurrentLanguage => current;

    public static void Init(Context context)
    {
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        string? saved = prefs?.GetString(KeyLocale, null);
        if (!string.IsNullOrEmpty(saved) && Dictionaries.ContainsKey(saved))
        {
            current = saved;
            return;
        }

        string sysLang = Locale.Default?.Language ?? "en";
        current = sysLang switch
        {
            "zh" => "zh-CN",
            "ja" => "ja",
            "fr" => "fr",
            "es" => "es",
            _ => "en"
        };
    }

    public static void SetLanguage(Context context, string code)
    {
        if (!Dictionaries.ContainsKey(code)) return;
        current = code;
        using var prefs = context.GetSharedPreferences(PrefsName, FileCreationMode.Private);
        using var editor = prefs?.Edit();
        editor?.PutString(KeyLocale, code);
        editor?.Apply();
    }

    public static string Get(string key)
    {
        if (Dictionaries.TryGetValue(current, out var map) && map.TryGetValue(key, out var val))
            return val;
        if (Dictionaries["en"].TryGetValue(key, out var fallback))
            return fallback;
        return key;
    }

    static readonly Dictionary<string, Dictionary<string, string>> Dictionaries = new()
    {
        ["zh-CN"] = new()
        {
            ["add_pc"] = "+ 添加电脑",
            ["language"] = "语言",
            ["diagnostics"] = "诊断",
            ["saved_pcs"] = "已保存的 Windows 电脑",
            ["empty_hint"] = "尚未添加 Windows 电脑。\n点击右上角「+ 添加电脑」输入局域网 IP 与 SHA-256 证书指纹即可开始远程控制。",
            ["edit"] = "编辑",
            ["dialog_add_pc"] = "添加 Windows 电脑",
            ["dialog_edit_pc"] = "编辑电脑",
            ["hint_name"] = "电脑名称（可选，如 办公室主机）",
            ["hint_host"] = "局域网 IP（如 192.168.1.10）",
            ["hint_port"] = "端口（默认 48200）",
            ["hint_pin"] = "64 位 SHA-256 证书指纹",
            ["hint_user"] = "Windows 登录用户名",
            ["hint_password"] = "Windows 登录密码（不保存）",
            ["save"] = "保存",
            ["cancel"] = "取消",
            ["delete"] = "删除",
            ["select_language"] = "选择界面语言",
            ["diag_title"] = "诊断与自检",
            ["diag_keystore"] = "本地设备密钥自检 (Android Keystore)",
            ["diag_probe"] = "局域网首帧连接探针 (A1 诊断)",
            ["err_invalid_endpoint"] = "请输入有效的局域网 IP、端口和 64 位 SHA-256 证书指纹。",
            ["err_invalid_user"] = "请输入有效的用户名（6-64 字符）。",
            ["connect_prompt"] = "输入 Windows 密码以连接（不保存密码）",
            ["back_list"] = "返回列表",
            ["connect_btn"] = "连接桌面",
            ["mode_touchpad"] = "触控板",
            ["mode_direct"] = "直触",
            ["keys_bar"] = "键盘条",
            ["metrics"] = "指标",
            ["disconnect"] = "断开",
            ["send_text"] = "输入文字",
            ["send"] = "发送",
            ["text_hint"] = "输入要发送到 Windows 的文字",
            ["disconnected"] = "已断开连接。"
        },
        ["en"] = new()
        {
            ["add_pc"] = "+ Add PC",
            ["language"] = "Lang",
            ["diagnostics"] = "Diag",
            ["saved_pcs"] = "Saved Windows PCs",
            ["empty_hint"] = "No Windows PCs added yet.\nTap \"+ Add PC\" above and enter your LAN IP & SHA-256 certificate fingerprint.",
            ["edit"] = "Edit",
            ["dialog_add_pc"] = "Add Windows PC",
            ["dialog_edit_pc"] = "Edit PC",
            ["hint_name"] = "PC Name (optional, e.g. Office PC)",
            ["hint_host"] = "LAN IP (e.g. 192.168.1.10)",
            ["hint_port"] = "Port (default 48200)",
            ["hint_pin"] = "64-char SHA-256 Fingerprint",
            ["hint_user"] = "Windows Username",
            ["hint_password"] = "Windows Password (not saved)",
            ["save"] = "Save",
            ["cancel"] = "Cancel",
            ["delete"] = "Delete",
            ["select_language"] = "Select Language",
            ["diag_title"] = "Diagnostics",
            ["diag_keystore"] = "Android Keystore Self-Test",
            ["diag_probe"] = "LAN First-Frame Probe (A1)",
            ["err_invalid_endpoint"] = "Enter a valid LAN IP, port, and 64-char SHA-256 fingerprint.",
            ["err_invalid_user"] = "Enter a valid username (6-64 chars).",
            ["connect_prompt"] = "Enter Windows password to connect (not saved)",
            ["back_list"] = "Back",
            ["connect_btn"] = "Connect",
            ["mode_touchpad"] = "Touchpad",
            ["mode_direct"] = "Touch",
            ["keys_bar"] = "Keys",
            ["metrics"] = "Stats",
            ["disconnect"] = "Disconnect",
            ["send_text"] = "Text",
            ["send"] = "Send",
            ["text_hint"] = "Enter text to send to Windows",
            ["disconnected"] = "Disconnected."
        },
        ["ja"] = new()
        {
            ["add_pc"] = "+ PCを追加",
            ["language"] = "言語",
            ["diagnostics"] = "診断",
            ["saved_pcs"] = "保存済みの Windows PC",
            ["empty_hint"] = "Windows PC がまだ登録されていません。\n右上の「+ PCを追加」から LAN IP と SHA-256 フィンガープリントを入力してください。",
            ["edit"] = "編集",
            ["dialog_add_pc"] = "Windows PC を追加",
            ["dialog_edit_pc"] = "PC を編集",
            ["hint_name"] = "PC 名（任意）",
            ["hint_host"] = "LAN IP（例: 192.168.1.10）",
            ["hint_port"] = "ポート（既定 48200）",
            ["hint_pin"] = "64桁 SHA-256 証明書フィンガープリント",
            ["hint_user"] = "Windows ユーザー名",
            ["hint_password"] = "Windows パスワード（保存されません）",
            ["save"] = "保存",
            ["cancel"] = "キャンセル",
            ["delete"] = "削除",
            ["select_language"] = "表示言語を選択",
            ["diag_title"] = "診断ツール",
            ["diag_keystore"] = "Android Keystore セルフテスト",
            ["diag_probe"] = "LAN 初回フレームプローブ (A1)",
            ["err_invalid_endpoint"] = "有効な LAN IP、ポート、64桁の SHA-256 フィンガープリントを入力してください。",
            ["err_invalid_user"] = "有効なユーザー名を入力してください（6〜64文字）。",
            ["connect_prompt"] = "接続するには Windows パスワードを入力してください",
            ["back_list"] = "戻る",
            ["connect_btn"] = "接続",
            ["mode_touchpad"] = "タッチパッド",
            ["mode_direct"] = "直接タッチ",
            ["keys_bar"] = "キー",
            ["metrics"] = "統計",
            ["disconnect"] = "切断",
            ["send_text"] = "文字入力",
            ["send"] = "送信",
            ["text_hint"] = "Windows に送信するテキスト",
            ["disconnected"] = "切断されました。"
        },
        ["fr"] = new()
        {
            ["add_pc"] = "+ Ajouter PC",
            ["language"] = "Langue",
            ["diagnostics"] = "Diag",
            ["saved_pcs"] = "PC Windows enregistrés",
            ["empty_hint"] = "Aucun PC Windows ajouté.\nAppuyez sur « + Ajouter PC » pour saisir l'IP LAN et l'empreinte SHA-256.",
            ["edit"] = "Modifier",
            ["dialog_add_pc"] = "Ajouter un PC Windows",
            ["dialog_edit_pc"] = "Modifier le PC",
            ["hint_name"] = "Nom du PC (optionnel)",
            ["hint_host"] = "IP LAN (ex: 192.168.1.10)",
            ["hint_port"] = "Port (défaut 48200)",
            ["hint_pin"] = "Empreinte SHA-256 (64 car.)",
            ["hint_user"] = "Nom d'utilisateur Windows",
            ["hint_password"] = "Mot de passe Windows (non enregistré)",
            ["save"] = "Enregistrer",
            ["cancel"] = "Annuler",
            ["delete"] = "Supprimer",
            ["select_language"] = "Choisir la langue",
            ["diag_title"] = "Diagnostics",
            ["diag_keystore"] = "Test Android Keystore",
            ["diag_probe"] = "Sonde LAN première image (A1)",
            ["err_invalid_endpoint"] = "Saisissez une IP LAN, un port et une empreinte SHA-256 valides.",
            ["err_invalid_user"] = "Nom d'utilisateur invalide (6-64 caractères).",
            ["connect_prompt"] = "Entrez le mot de passe Windows pour vous connecter",
            ["back_list"] = "Retour",
            ["connect_btn"] = "Connecter",
            ["mode_touchpad"] = "Pavé tactile",
            ["mode_direct"] = "Tactile",
            ["keys_bar"] = "Touches",
            ["metrics"] = "Stats",
            ["disconnect"] = "Quitter",
            ["send_text"] = "Texte",
            ["send"] = "Envoyer",
            ["text_hint"] = "Texte à envoyer vers Windows",
            ["disconnected"] = "Déconnecté."
        },
        ["es"] = new()
        {
            ["add_pc"] = "+ Añadir PC",
            ["language"] = "Idioma",
            ["diagnostics"] = "Diag",
            ["saved_pcs"] = "PCs Windows guardados",
            ["empty_hint"] = "No hay PCs Windows añadidos.\nPulsa « + Añadir PC » e introduce la IP LAN y la huella SHA-256.",
            ["edit"] = "Editar",
            ["dialog_add_pc"] = "Añadir PC Windows",
            ["dialog_edit_pc"] = "Editar PC",
            ["hint_name"] = "Nombre del PC (opcional)",
            ["hint_host"] = "IP LAN (ej: 192.168.1.10)",
            ["hint_port"] = "Puerto (predet. 48200)",
            ["hint_pin"] = "Huella SHA-256 (64 caracteres)",
            ["hint_user"] = "Usuario de Windows",
            ["hint_password"] = "Contraseña de Windows (no se guarda)",
            ["save"] = "Guardar",
            ["cancel"] = "Cancelar",
            ["delete"] = "Eliminar",
            ["select_language"] = "Seleccionar idioma",
            ["diag_title"] = "Diagnósticos",
            ["diag_keystore"] = "Autocomprobación Android Keystore",
            ["diag_probe"] = "Sonda LAN primer fotograma (A1)",
            ["err_invalid_endpoint"] = "Introduce una IP LAN, puerto y huella SHA-256 válidos.",
            ["err_invalid_user"] = "Usuario no válido (6-64 caracteres).",
            ["connect_prompt"] = "Introduce la contraseña de Windows para conectar",
            ["back_list"] = "Volver",
            ["connect_btn"] = "Conectar",
            ["mode_touchpad"] = "Panel táctil",
            ["mode_direct"] = "Táctil",
            ["keys_bar"] = "Teclas",
            ["metrics"] = "Stats",
            ["disconnect"] = "Desconectar",
            ["send_text"] = "Texto",
            ["send"] = "Enviar",
            ["text_hint"] = "Texto para enviar a Windows",
            ["disconnected"] = "Desconectado."
        }
    };
}
