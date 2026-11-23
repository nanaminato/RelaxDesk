# File transfer UI and queue validation — 2026-11-23

Source checkpoint on `codex/dxgi-media-pipeline`; no new package, as requested by the owner. The historical 0.4.2 ZIP does not contain clipboard or file changes.

## Automated results

Release solution build with isolated artifacts and the existing native module: zero warnings/errors. Core/TLS checks: 599. Windows file scope/cleanup contracts: 29; symlink creation skipped without the required privilege, actual junction and hardlink rejection passed.

The real WPF file panel suite passed 22 checks using generated fixture files, production Host, real third-channel TLS and synthetic media/control. Coverage includes recursive Unicode and empty directories in both directions; overwrite/keep-both/skip; pause/cancel/retry; directory descendant cancellation; staged native HWND drop confirmation; hide/reopen; progress snapshots; and revocation. Additional cleanup checks cancelled a partial upload without temporary/final-file leakage, renamed the generated source parent after completion to verify handle release and observed zero owned native video resources.

The full Viewer session test exercised ConnectVideo, independent file-only grant, toolbar, native drop confirmation, upload, hide/reopen and live revocation. Video/control continued after file revocation; queue and toolbar disabled, and owned native resources returned to zero. HostShell production-process checks verified no-root denial, default-off consent, actual scoped upload and live revocation with subsequent control pong.

The complete nonphysical regression exited 0:

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/office-artifacts -NativeBuildDirectory build/native-office -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

This includes security rejection, media/ticket separation, clipboard, capture recovery, control liveness, WARP contracts, CPU WPF and static heartbeat checks. After final queue/UI refinements, the solution was rebuilt and the 22-check file UI suite rerun successfully. Raw outputs are in [file-ui-validation](file-ui-validation/). No office performance improvement is claimed from synthetic runs.

## Pending acceptance

Keyboard foreground routing, physical GPU Viewer/capture and Windows Home LAN remain unverified in this Server/RDP runner. Capture previously returned 0x80070005; production foreground/security gates remain intact. Physical Explorer drops, mixed-DPI windows, real files above 1GiB and disk-full fault injection need target-machine testing. These are skips, not passes.

Cross-session resume is not implemented: partials are removed on interruption. Next implement persistent single-session Host and secure bounded Viewer reconnect, then add resume bound to fresh authorization and unchanged local scope. Advanced clipboard/input, monitor selection, system integration and audio remain on the full product goal.
