# Current-user installation, upgrade and removal

Implemented source: `scripts/Install-RelaxDesk.ps1`, `scripts/Uninstall-RelaxDesk.ps1`, shared `RelaxDesk.Setup.psm1`, and the future bundle's `Install-RelaxDesk.cmd`. Packaging scripts now include these files but have **not** been executed for this slice. Existing distributed archives are unchanged. Optional service installation remains separate, unfinished work.

## Install

Use a complete, newly extracted self-contained `win-x64` bundle with `package.json` and `SHA256SUMS.json`. Portable execution remains available. No SDK or downloaded installer framework is needed; scripts use Windows PowerShell 5.1 or PowerShell 7 under the system's existing execution policy. No execution-policy change, automatic unblock or elevation is performed. Managed policies may prohibit this optional script entry; use the portable application or follow your administrator's policy. The hashes verify consistency, not publisher identity: the experimental bundle is still unsigned.

```powershell
# From a future unified bundle; preview performs reads and writes nothing.
./scripts/Install-RelaxDesk.ps1 -WhatIf
./scripts/Install-RelaxDesk.ps1 -DesktopShortcuts
# Optional local, non-system directory:
./scripts/Install-RelaxDesk.ps1 -InstallRoot 'D:\My Apps\RelaxDesk' -DesktopShortcuts
```

Default destination is `%LOCALAPPDATA%\Programs\RelaxDesk`. Start-menu links are created in the current user's Programs/RelaxDesk folder; desktop links require `-DesktopShortcuts`. The CMD entry explicitly chooses desktop links. No app starts automatically. No services, startup entries, scheduled tasks, network listeners, firewall rules, RDP components, machine registry keys or system security policies are changed. Windows may ask about network access when Host is manually started: allow only the intended trusted private network as appropriate. Host still binds the selected address and requires explicit consent for each connection.

The current-user uninstall key `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\RelaxDesk` records version, installation location and quoted uninstall command. App diagnostics remain at `%LOCALAPPDATA%\RelaxDesk\logs`, independent of the installed binaries. A second installation using another directory cannot silently take over the existing uninstall registration or another shortcut. This is a single registered current-user installation, not a multi-instance installer.

## Upgrade and rollback boundary

Close this installation's Host and Viewer first. Setup refuses to kill running RelaxDesk processes; it also refuses an unreadable matching process whose ownership cannot be established. Do not launch applications while setup is in progress. A per-user/per-root named mutex rejects another setup process.

Preflight rejects overlapping source/destination, drive/network/device roots, machine/system install locations, unowned existing destinations, redirected/reparse ancestors or package entries, invalid/duplicate/escaping/ADS/device paths, invalid sizes/hashes and missing required entry points. Package metadata is bounded to 4 MiB, 10,000 files, 2 GiB per file and 8 GiB in total; tree enumeration is bounded to 20,000 entries. Packages with added unlisted files, including logs collected inside an extracted bundle, require a fresh extraction. Windows path-length/filesystem restrictions still apply.

Each install copies into an `app-<random GUID>` directory and verifies the staged hashes before changing links or registration. An atomic same-volume replacement of `setup-state.json` commits the selected release. This state records the current user SID, canonical root and release folders. The file/registry/shortcut steps are not one Windows transaction; ordinary failures before commit restore saved registration values and exact shortcut bytes and remove verified staging files. Copy-time and post-registration injected failures are tested. Power failure, process termination, full disk, malicious concurrent path changes and arbitrary rollback IO failure are not claimed atomic. Incomplete or unowned directories fail closed and require operator inspection; there is no blind recursive force-delete recovery.

After commit, only unchanged old package files are pruned. Modified/locked/unknown files retain their release folder; at most four release folders are tracked, and setup refuses another upgrade until the owner reviews the retained content. Desktop choice persists across upgrades. There is no unattended updater, automatic restart, downgrade protection or retained-version selection UI.

## Uninstall

Use the current-user Windows Apps/Programs uninstall entry, or run the package script:

```powershell
./scripts/Uninstall-RelaxDesk.ps1 -InstallRoot "$env:LOCALAPPDATA\Programs\RelaxDesk" -WhatIf
./scripts/Uninstall-RelaxDesk.ps1 -InstallRoot "$env:LOCALAPPDATA\Programs\RelaxDesk"
```

The registered command uses Windows PowerShell without changing execution policy. Policy restrictions can therefore also block that entry; the same policy requirements as installation apply. Uninstall preflights owned state and installed paths before removing integration. Owned links and registration are removed; only files matching the installed manifest are deleted, then empty package directories are removed. Modified/unknown files and shortcuts with changed targets/arguments are retained with warnings. Unknown content directly under the install root and application logs outside it are preserved. `RemainingFiles=True` means the owner must review this retained content; it is not a promise of an empty directory. If all owned files are unchanged and no extra content exists, the app directories, marker, links and registration are fully removed. The installed module can remove its own loaded file; no delayed startup cleanup or process killing is used. A portable source bundle's uninstall script can inspect/continue removal of retained content later.

## Verification and remaining acceptance

`./scripts/test-user-setup.ps1` creates a fresh repository/build fixture and a random isolated HKCU test key. It uses real filesystem, Windows junctions, COM .lnk files, a separate mutex-holder process and Windows ping under a temporary application filename. Generated package EXEs are text; RelaxDesk media/input is never launched. This is a developer test, not an installer run against the real user's Programs/Desktop/Apps entry. It requires normal current-user registry access, unavailable in the restricted sandbox.

[Evidence](../benchmarks/2026-11-24-user-setup-validation.md) records 68 passing checks on both PowerShell versions. Final unified bundle install/launch/upgrade/uninstall, actual Windows Apps presentation, execution-policy/Mark-of-the-Web UX, redirected known folders, antivirus/file-lock races, power interruption, Home dual-PC behavior and service-mode install/removal remain unverified. No performance or physical acceptance claim is made.

API conventions: [uninstall registration values](https://learn.microsoft.com/en-us/windows/win32/msi/uninstall-registry-key), [WSH shortcut targets and working directories](https://learn.microsoft.com/en-us/troubleshoot/windows-client/admin-development/create-desktop-shortcut-with-wsh).

The optional [broker executable and HostShell lease mode](SESSION-BROKER.md) now exist in source. A future user bundle may carry the executable, but copying it does not register a service. Machine service installation must be a separate explicit operation with protected binaries and reviewed ACLs; current-user app directories are not suitable service image paths. No such machine installer has been run or accepted.
