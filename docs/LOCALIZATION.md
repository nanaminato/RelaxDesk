# RelaxDesk 国际化与自定义翻译指南

RelaxDesk 内置了 **5 种常用语言**，并支持用户在无需重新编译的情况下直接添加自定义翻译文件。

## 内置语言

- `en` — English（默认回退语言）
- `zh-CN` — 简体中文
- `es` — Español
- `ja` — 日本語
- `fr` — Français

## 首次启动语言选择规则

首次启动应用（尚未手动切换过语言）时，RelaxDesk 会自动参考当前 Windows 系统的界面语言（`CultureInfo.CurrentUICulture`）：

1. 优先精确匹配已内置或用户添加的语言代码（例如 `zh-CN`）。
2. 其次按主语言代码前缀匹配（例如 `ja-JP` 匹配 `ja`，`es-MX` 匹配 `es`，`zh-TW` 匹配 `zh-CN`）。
3. 如果与所有可用语言完全不匹配，则自动回退为 **English (`en`)**。

在界面中手动切换语言后，选择会自动保存到 `%LOCALAPPDATA%\RelaxDesk\language.json`，下次启动时生效。

## 添加自定义翻译文件

只需在以下任一目录中放入 `.json` 翻译文件，然后重启应用即可自动识别：

1. 应用安装/运行目录下的 `locales\` 文件夹（例如 `<AppDir>\locales\de.json`）
2. 当前用户配置目录下的 `locales\` 文件夹：`%LOCALAPPDATA%\RelaxDesk\locales\`（例如 `%LOCALAPPDATA%\RelaxDesk\locales\de.json`）

### 文件格式示例

推荐使用包含 `code`、`displayName` 和 `strings` 的标准 JSON 格式（参考内置文件 `src/RelaxDesk.Core/Localization/Locales/en.json`）：

```json
{
  "code": "de",
  "displayName": "Deutsch",
  "strings": {
    "Common.Cancel": "Abbrechen",
    "Host.WindowTitle": "RelaxDesk Host",
    "Host.StartSharing": "Freigabe starten",
    "Viewer.WindowTitle": "RelaxDesk Remotedesktopverbindung"
  }
}
```

### 说明

- **部分翻译回退**：自定义文件中未提供的键（key）会自动回退显示英文（`en`）。
- **覆盖内置语言**：如果放入与内置语言同名的文件（例如 `zh-CN.json`），其中的条目会覆盖内置翻译。
- **容错保护**：单个翻译文件大小上限为 1 MiB；格式损坏的外部文件会被自动忽略，不会影响应用启动。
