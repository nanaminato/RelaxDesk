# Combined UI and pointer release validation — 2026-11-26

Includes all pending Host UI/icon/theme changes alongside the committed cursor presentation, high-rate and UI clipping fixes. Display/service settings are inline, and device settings/diagnostics are updated. Release checks exposed a startup XamlParseException: standalone device/service controls now explicitly load their style dictionary. The service navigation test waits for nested controls to become visible before layout assertions.

Validation: stable .NET 11.0.100 Release build, zero warnings/errors; 2,224 Core/TLS checks; 21 cursor checks; Host default/minimum-width layouts; 56 service UI checks with fake privileged launcher and real same-process test broker. The 840x650 service screenshot was inspected. No actual UAC/SCM install, physical dual-machine refresh, mixed-DPI cursor or installer upgrade acceptance is claimed. The already-verified native-high-rate Release DLL is reused because native source has not changed.

Release manifests and installer metadata record the exact source revision. Delivery checksums are generated after packaging.
