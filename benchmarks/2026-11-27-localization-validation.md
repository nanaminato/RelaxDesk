# UI localization completion — 2026-11-27

Scope: internationalize all uncovered application presentation text across Viewer/Host settings/session/transfer/consent/statistics UI and service/elevated approval dialogs. The two gray description rows below video FPS/bitrate are absent, and the unrelated first-connect hint is restored. All five built-in languages contain 618 matching keys. No protocol identifiers, feature grants, fingerprint values or synthetic codec sample content were translated.

Verified with stable .NET 11.0.100:

- Release solution build: zero warnings/errors, existing verified native DLL; no native source changes.
- Source audit: 173 production C#/XAML files, no residual CJK presentation literals except the explicitly retained codec sample; literal resource references resolve.
- Core/protocol/access/TLS/localization: 16,514 passing checks, including all five catalogs, key parity, duplicate detection, .NET composite-format parsing, exact placeholders and no CJK leakage into English/French/Spanish catalog values.
- Viewer language switching: 65 checks across five rendered languages, including live code-created labels/video menus, no gray description items, label widths and unchanged multilingual sample user input. Advanced views rendered and inspected; labels share an auto-sized column. A 100 ms render-settle interval prevents capturing a stale WPF text drawing immediately after changing languages.
- Host language switching: 30 checks, including navigation, service/device controls, login and primary-display summaries. French and Spanish previews inspected.
- File UI/queue: 22 passing production Host/third-TLS checks plus active-session cancellation cleanup, with synthetic concurrent media. Download actions now use explicit direction metadata rather than localized labels.
- Service UI: 56 passing simulated privileged-launcher/real test-broker checks. No installation or SCM mutation.
- Existing Viewer design: 14 passing checks; automatic quality UI/state: 15 passing checks.

No physical dual-machine/DPI or actual UAC acceptance is claimed, no throughput benchmark was run, and no performance improvement is claimed. Historical/raw system log and exception text is retained. No installer regenerated for this source change.
