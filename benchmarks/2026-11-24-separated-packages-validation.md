# Actual separate Host and Viewer ZIP validation

Packaged clean source **`7e51e9549819c9d088bae7cacbc4e7ed4fd4740b`**, version **`0.4.2-roles.20261124-202127.g7e51e95`**, stable SDK 11.0.100, native ABI 2. This includes the latest one-click service enable flow from `bf56734`. Subsequent verification scripts/docs do not modify either archive. [Unmodified result](2026-11-24-separated-packages-result.json), [layout and reproduction](../docs/ROLE-PACKAGES.md).

| ZIP under `build/RelaxDesk-separated-win-x64-20261124` | Bytes | Manifest entries | ZIP SHA-256 |
| --- | ---: | ---: | --- |
| `RelaxDesk-Host-win-x64.zip` | 148,930,864 | 831 | `57864633CCEA6ECAE13385BFCBA5C0ABB8D34C5B68C66DEBE9DCA7C97F467B08` |
| `RelaxDesk-Viewer-win-x64.zip` | 68,558,639 | 420 | `AE8663EB58E6A13439969C73292FB10F4824E3FAC8F559DC45566CF1447D9597` |

Both ZIPs have adjacent `.sha256` files. Host contains Host/HostUi/Service and `启动被控端.cmd`; Viewer contains Viewer only and `启动连接端.cmd`. Both carry local runtimes, role-specific guide, package metadata/manifests and relevant .NET redistribution notices. No SDK, developer fixtures, all-role collector or incompatible combined user installer is included. These are portable applications; optional service installation remains inside the Host UI. No project license was chosen.

`scripts/package-roles.ps1` exits 0, four actual self-contained publish operations; raw `build/separated-packages-build.txt`. Existing validated native binary is reused via explicit `-SkipNativeBuild`; CMake uses static MSVC runtime. No new graphics implementation, codec or protocol behavior.

`scripts/verify-role-packages.ps1` extracts the **actual ZIPs** into a fresh `build/separated-packages-verification`, exits **0**, **23 assertions / 10 child steps PASS**, `PASS_SEPARATE_PACKAGES_SYNTHETIC_ONLY`. Full step stdout/stderr is retained there; launcher output is `build/separated-packages-verification.txt`.

- Both ZIP sidecars/manifests pass, 831/420 files respectively; exact file counts reject unlisted additions. Root entries, role/component isolation and matching clean version/revision are checked.
- Actual Host reports .NET 11 bundled x64 runtime under extracted Host directory. Actual HostShell, Viewer and Service versions match. Host, HostUi and Viewer native DLL copies agree on `718F8C5D4433145BA586AE1EC985764E4FB0AB5982BA227611572F768EFDA0D4`.
- Actual packaged Service CLI readonly `--install-service --package <Host-role> --owner-sid <current-user> --what-if` passes. It validates the role manifest, service payload/PE/runtime/notices and returns matching version with Changed=false. No UAC or SCM deployment occurs.
- Actual packaged HostShell **54 WPF service UI assertions PASS**, using the packaged Host EXE, fake privileged launcher and authenticated same-process test broker. This verifies the new role layout does not break the one-click UI, not that a real elevated install succeeds.
- Separate extracted Host and Viewer EXEs complete **60 CPU-presented synthetic frames** in each of Deflate and consent-first injected hardware-to-software H.264 fallback sessions over real TLS with certificate-matched media binding. Deflate uses an initial strict pin; the fallback case uses fresh first-contact Host approval. Both processes report zero native resources after cleanup. The fallback case records exactly one injected encoder failure then software H.264 continuation. No foreground input or physical capture.

The raw short synthetic traces contain FPS/stage distributions; these are packaging/interoperability probes, not office performance, physical scanout, input-to-photon or MSTSC comparisons. The historical 76/76 combined ZIP and source-defined 77-case suite are not relabeled as split-package full-office acceptance. Home two-machine desktop, actual UAC/admin/SCM installation, input/IME/clipboard/audio, mixed DPI/HDR/drivers and soak remain **NOT_TESTED** here. No service/firewall/startup registration, remote push/merge or release publication was performed.
