# UI localization

The built-in `zh-CN`, `en`, `ja`, `fr`, and `es` catalogs each contain 489 presentation messages. The audit covers Viewer connection/session menus, clipboard/text UI and automatic file copy, display/audio/statistics/error UI, Host consent/login/device/service settings, user-facing transfer validation, service result dialogs and the elevated input approval question. The two informational rows formerly below FPS and bitrate in the session menu remain removed. The unrelated first-connection hint is retained.

XAML uses `ui:Loc` bindings. Code-created presentation properties use `LocalizedText.Bind` for live language changes, with weak event subscriptions so localization does not retain closed controls. Formatted values retain named resource keys and positional arguments; compound labels can use a presentation-only string factory. This mechanism never scans or translates arbitrary text fields, filenames, fingerprints, credentials or remote content. Invariant protocol identifiers, key codes and diagnostic event names stay unchanged. Native Windows standard dialog buttons follow the OS language; application-owned title/body text uses the selected language.

File transfer decisions use `IsDownload` metadata rather than a translated direction label. Language changes must not change access decisions, selected monitor IDs, startup modes, transfer choices or negotiated fields. The fixed multilingual editor sample in `OfficeTextQuality.cs` remains a codec benchmark input, not translatable UI copy. Historical or raw system exception/log text may retain the language in which it was generated.

The existing catalog loader supports local custom packs, English fallback and saved language preferences. New built-in keys must exist in all five catalogs with matching positional indices and format clauses. Do not create resource keys from received/user text. Long labels use a shared auto-sized column in the connection form; translated views should be rendered after a complete WPF layout/render cycle.

Validation commands:

```powershell
python scripts/test-localization.py
dotnet run --project src/RelaxDesk.Tests -c Release
dotnet src/RelaxDesk.Viewer/bin/Release/net11.0-windows/RelaxDesk.Viewer.dll --localization-contracts build/viewer-localization
dotnet src/RelaxDesk.HostShell/bin/Release/net11.0-windows/RelaxDesk.HostShell.dll --localization-contracts build/host-localization
```

The shared office acceptance list includes both GUI cases. These checks cover five rendered languages, live bindings, dynamic video menus with no description rows, long labels, matching resources and unchanged sample user text. Physical mixed-DPI monitors, native UAC dialogs, external custom translation layouts and exhaustive native-speaker review remain manual coverage.
