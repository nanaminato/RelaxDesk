# Current-user setup validation — 2026-11-24

Source implements current-user install/upgrade/removal, manifest verification, GUID release directories and atomic active-state file, HKCU uninstall registration, Start-menu/optional desktop .lnk, running-process and cross-process setup exclusion, rollback, and preservation of unknown/modified content. Packaging integration is prepared; no publish/archive/package command was run. Service mode and audio remain open.

Final **68 checks pass with exit 0 in both PowerShell 7 and Windows PowerShell 5.1**, using independent fresh fixtures and random HKCU test keys. Commands:

```powershell
./scripts/test-user-setup.ps1
& "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -File ./scripts/test-user-setup.ps1
```

Logs: `build/setup-tests.txt` and `build/setup-tests-windows-powershell.txt`. Final fixtures: `build/setup-verification-dabbe3f6d0e24d2aa418f0e81fea8c68` and `build/setup-verification-675693e09548464bb2d88e6db5634c1a`. Test outputs remain local/ignored evidence. Generated EXEs are text; the running-process fixture launches only the existing Windows ping binary copied under an application filename, then stops that exact owned process. No RelaxDesk capture, input, listening, default install folder, real Start-menu/Desktop, startup, firewall or machine registry mutation occurs.

Coverage:

- Source/destination overlap, corruption, unlisted files, duplicate manifest paths, traversal/absolute/ADS/backslash/device/trailing-dot/trailing-space/empty-component paths, unowned destination and foreign uninstall registration are rejected.
- First install preserves third-party notice bytes, binds state to current SID, registers actual isolated HKCU version/uninstall values with quoted Unicode/space/ampersand/parentheses path, and creates real COM shortcut targets without remote arguments.
- Module and public-script `WhatIf` reads/validates without creating root, registry or shortcuts. Public Windows PowerShell CLI entry handles the complex path correctly.
- First-install mid-copy and post-registration failures remove owned app/registration. Upgrade mid-copy, post-copy and post-registration failures preserve active state, restore registry version and exact shortcut bytes, and remove staging files.
- Real separately held mutex prevents concurrent install/removal without mutating active state. A real owned running process causes upgrade/removal refusal and stays running; setup never terminates it.
- Junction ancestors/roots are rejected without modifying their sentinel target. A junction added inside an installed release is rejected during uninstall preflight before registration removal.
- Upgrade activates a separately verified release, preserves desktop choice, removes unchanged old release. Uninstall removes owned integration but retains modified/unknown file content and unknown empty folders; after restoring/removing fixture edits, complete cleanup succeeds. The installed module is imported from its own release before removal, proving it can delete its loaded module file.

Initial development uncovered and corrected Windows PowerShell 5.1 JSON-array pipeline expansion, null-string binding for File.Replace, and Get-FileHash behavior under an inherited WhatIf preference. The final implementation uses a read-only .NET SHA256 stream for both hash verification and removal matching. Initial restricted-sandbox registry access failed; actual HKCU/COM tests passed in the normal environment. No automatic approval rejection occurred.

PowerShell source/parser and `git diff --check` are checked. Managed/native application code is unchanged by this slice; the preceding session-boundary commit's full Core/TLS/WPF/media regression remains its evidence, not a newly rerun installer test. Final distribution build and installation of its actual self-contained binaries are deferred at the owner's request. No new test ZIP is supplied.

Remaining: final package install/launch/upgrade/uninstall on Home, actual Windows Apps entry and shell launch, downloaded-script policy/Mark-of-the-Web behavior, redirected known folders, power/process interruption and arbitrary IO/path races, optional service installer/worker coordination, audio and physical acceptance. [Contract, commands and limits](../docs/USER-INSTALL.md).
