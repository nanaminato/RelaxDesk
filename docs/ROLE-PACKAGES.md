# Separate Host and Viewer packages

For double-click installation instead of portable extraction, use the [new independent EXE installers](EXE-INSTALLERS.md). This page preserves the portable ZIP layout and its exact unchanged payload evidence.

Delivered version **`0.4.2-roles.20261124-202127.g7e51e95`**. Exact ZIPs, hashes and observed verification: 23 split-package assertions, actual packaged 54-check one-click UI and two 60-frame synthetic cross-role sessions PASS. Physical acceptance remains pending. Archives live under `build/RelaxDesk-separated-win-x64-20261124`.

The owner requests independent controlled-side and connecting-side ZIPs. `scripts/package-roles.ps1` publishes the same committed version into two self-contained win-x64 closures:

| Role | Product components | Root entry |
| --- | --- | --- |
| Host | `Host/`, WPF `HostUi/`, isolated `Service/` | `启动被控端.cmd` |
| Viewer | `Viewer/` only | `启动连接端.cmd` |

Each has its own `package.json` (format 1 plus explicit role/entry/components), complete `SHA256SUMS.json`, runtime redistribution notices, concise role-specific guide and ZIP SHA256 sidecar. Nothing is copied from an old combined ZIP. Only the Host includes service payload/maintenance; its existing fixed-layout validation and one-click setup can resolve the same-bundle helper. Service defaults, permissions and protocol are unchanged. Core and native code are shared, not compiled with role-specific network behavior.

These are **portable app packages**, not the combined current-user installer. The combined `Install-RelaxDesk.cmd` requires both roles and is deliberately not shipped as a broken entry in either split package. User-level install/uninstall remains available through the existing combined recipe; separate role installer registration is not claimed. Host's optional machine service installation stays integrated in its own window. Neither role includes the all-role `Collect-Logs`/office collector or developer fixtures; those would require absent components. App logs and the Host/Viewer connection UI remain available.

Build from a clean committed tree, for example:

```powershell
./scripts/package-roles.ps1 -OutputDirectory build/RelaxDesk-separated-win-x64-20261124 -NativeBuildDirectory build/native-final-office -SkipNativeBuild
```

Omit `-SkipNativeBuild` to rebuild the native DLL. Output must be a fresh directory under workspace `build`; packages/evidence are never overwritten. Stable SDK 11.0.100, ordinary version/notice checks and unchanged native ABI 2 apply. Runtime hash manifests are produced per role after its final contents; no role performs installation, listening or startup mutation during packaging.

Reproduce the actual split-archive verification with developer tools outside the distribution:

```powershell
./scripts/verify-role-packages.ps1 -HostArchive E:/RelaxDesk/build/RelaxDesk-separated-win-x64-20261124/RelaxDesk-Host-win-x64.zip -ViewerArchive E:/RelaxDesk/build/RelaxDesk-separated-win-x64-20261124/RelaxDesk-Viewer-win-x64.zip -SmokeApplication E:/RelaxDesk/build/service-one-click-authorized-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll -OutputDirectory E:/RelaxDesk/build/separated-packages-verification-fresh
```

Final delivery validation must extract both actual ZIPs into a separate directory, verify each manifest and role isolation, check actual EXE versions/Host bundled runtime and native hashes, perform Host service-payload readonly preflight, run the Host's one-click UI contracts with a fake privileged launcher and a synthetic authenticated Host-to-Viewer session across the two extracted packages. Do not count that as a real service install or Home dual-PC acceptance. Exact results and archive hashes are recorded after the observed run; physical UAC/SCM/input/audio/GPU/long-duration validation remains pending.

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../benchmarks/README.md).
