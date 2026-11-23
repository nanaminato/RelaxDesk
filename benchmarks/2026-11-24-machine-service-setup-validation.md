# Machine broker deployment validation — 2026-11-24

Implemented source: explicit fixed-root install/upgrade/removal CLI, protected file/service ACL model, manifest/self-contained-runtime/x64 checks, staged copy/receipt, controlled owned service lifecycle, manual/explicit auto startup, rollback and deferred deletion. Service startup now checks its protected active deployment before opening its pipe. No machine service was registered, changed, started, stopped or deleted; the development token is non-administrator. No UAC or packaging command was run.

Stable .NET 11.0.100; nine-project Release build at `build/service-setup-final-artifacts`, zero warnings/errors, existing native viewport module for the interactive Host only.

**80 contract checks pass** on final source. Filesystem, ACL construction/application/inspection, hashing, private create, atomic receipt replacement, junction, Unicode/space/ampersand/parentheses paths and cleanup use real Windows objects in isolated workspace/build fixtures. All service mutations use an in-memory SCM backend. Source fixtures copy the current development apphost/managed assemblies, synthesize included-framework metadata and use inert runtime component stand-ins. They are never launched and do not prove a functioning self-contained service package.

Coverage:

- WhatIf creates no destination/service state; first install defaults manual/stopped; ordinary-token mutation is refused before write; protected production runtime rejects the test root. Public CLI preview, duplicate/conflicting/unknown flags and forbidden root override are exercised without elevation/SCM writes.
- Null DACL, generic write, untrusted file owner, LocalService write, service user stop rights and untrusted service owner are rejected; read-only LocalService file and explicit owner query-only service descriptors pass. Active receipt/current-user/hash conditions are checked against a real fixture tree.
- Fresh mid-copy/post-copy/post-state/post-config/post-commit failures clean up owned files/registration in the model. Existing running-service failures restore configuration/receipt/running state; staging-only failures do not stop/restart it. Simulated start failure restores the old version.
- Immutable allowed SID, foreign LocalSystem account, reparse installed member prevent taking over unrelated service state; native mutation-time snapshot comparisons are implemented but real mutation races remain untested. Upgrades preserve prior automatic policy/running state; explicit manual changes the policy.
- Pending DeleteService retains binaries and a not-ready pending receipt until a later successful retry. Owned unchanged install fully removes; modified notice and unknown empty directory remain, then clean removal succeeds after fixture edits are removed/restored.
- Source corruption, traversal, mapped-path collision and shared-framework runtime config are rejected before registration. Third-party notice content remains in the selected payload.
- Actual **read-only SCM** opens/query of Windows EventLog service exercise status/config/security marshalling; ABI sizes are status-process 36, config 64, status 28 bytes. This does not test modifying that service or actual RelaxDesk registration.

Standalone final-source log: `build/service-setup-contracts.txt` (first final 80-check fixture `build/machine-service-setup-verification/machine-setup-fd47f23593f84a45804768091975b335`). The full suite generates its own separate fixture. Early testing found no symbolic-link privilege; the reparse contract uses a real unprivileged junction instead, without enabling a privilege or weakening production checks.

Full regression completed with **exit code 0** on the final source/build, including the 80 installation contracts, 1,787 Core/TLS checks, 26 actual broker IPC checks, 22 WPF service-coordination checks and existing display/topology, clipboard/files/resume/reconnect/media/resource suites.

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/service-setup-final-artifacts -NativeBuildDirectory build/native-viewport-release -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

Generated full log: `build/service-setup-regression.txt`. Full suite also covers existing 1,787 Core/TLS, local broker IPC, WPF service supervision, topology, files/clipboard/resume/reconnect and synthetic media/liveness/resource cleanup. Actual foreground input/capture/physical GPU are explicit skips.

Not accepted: actual privileged SCM mutation or LocalService runtime; real protected machine ownership/DACL efficacy; self-contained bundle install/launch; Windows boot/manual/automatic startup; actual pending-deletion management handles; power/RPC/AV/file-lock failures; unrelated account/logon access; Home physical capture/input/secure-desktop transitions. Mock start/stop and inert runtime fixtures do not establish these. Opt-in logon agent launch/restart and audio remain development work. [Commands, recovery and exact limitations](../docs/MACHINE-SERVICE-INSTALL.md).
