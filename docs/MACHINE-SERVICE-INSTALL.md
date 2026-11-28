# Protected machine broker installation and removal

Current source adds [HostShell install/update/start/uninstall buttons](SERVICE-SETUP-UI.md), using a separate explicitly elevated service-maintenance process. The existing CLI remains available in the delivered `g5ebe35d` package, which does not contain the new buttons. Implemented source adds explicit service setup commands to `RelaxDesk.Service.exe`. The ordinary user installer remains independent and never registers/starts this component. No machine service has been registered, upgraded, stopped or removed on the development runner: its token is not an elevated administrator. Actual LocalService/SCM acceptance remains pending. No new package is generated for this UI update.

## Operator entry points

These commands are for the final complete self-contained win-x64 bundle, after review of its origin/integrity. Use an **external bundle copy**, not an executable inside the installed machine release. This avoids a running maintenance executable preventing its own clean removal. The CLI itself does not request UAC, store credentials or change execution policy; the new HostShell buttons explicitly request UAC only after package validation and user confirmation; a mutating operation requires the operator to have explicitly opened an elevated administrator shell. `--what-if` does not elevate and writes no deployment/SCM state.

Capture the intended ordinary user's actual SID before changing to another administrator account. There is no default allowed user, no group-based grant and no inferred permission to share the desktop:

```powershell
[Security.Principal.WindowsIdentity]::GetCurrent().User.Value

# From the external complete bundle; substitute the intended user's SID.
./Service/RelaxDesk.Service.exe --install-service --package . --owner-sid '<user SID>' --what-if
# First install defaults to manual/stopped. --start explicitly starts the broker now.
./Service/RelaxDesk.Service.exe --install-service --package . --owner-sid '<user SID>' --start
# Optional, explicit boot startup, independently of the --start-now choice:
./Service/RelaxDesk.Service.exe --install-service --package . --owner-sid '<user SID>' --automatic --start
# Later upgrade from a newly extracted external bundle, same user:
./Service/RelaxDesk.Service.exe --install-service --package . --owner-sid '<user SID>'
# Explicitly return to manual boot policy:
./Service/RelaxDesk.Service.exe --install-service --package . --owner-sid '<user SID>' --manual
# Complete removal, from the external bundle:
./Service/RelaxDesk.Service.exe --uninstall-service --owner-sid '<user SID>' --what-if
./Service/RelaxDesk.Service.exe --uninstall-service --owner-sid '<user SID>'
```

Initial policy is manual and stopped. Upgrades preserve the existing manual/automatic choice unless explicitly overridden, and restore a previously running broker after the controlled update. Starting the broker does not start an interactive agent, network sharing or a Viewer grant. There is no logon task, hidden UI, automatic worker restart, network/firewall rule, UAC change, RDP modification or privileged desktop access. After installation/start, the intended user opens HostShell, selects optional system-service coordination, connects it, manually starts sharing and separately approves each Viewer/capability. Changing the configured user requires explicit uninstall/reinstall; an upgrade cannot silently transfer the grant to another SID.

## Protected deployment and service identity

Destination is fixed to the OS-known `Program Files/RelaxDesk/Broker`, with direct `app-<GUID>` releases. No arbitrary machine root is accepted. UNC/device/drive roots, overlap, reparse ancestors/members, escaping/ADS/device paths, duplicate/mapping-colliding paths, oversized metadata and corrupt/missing entries fail before registration. Manifest cap: 4 MiB/10,000 entries, 2 GiB per file/8 GiB total; installed tree enumeration cap 20,000 entries. Only the Service subtree, package metadata and redistribution notices are staged. UI/graphics module entries are rejected.

A self-contained claim alone is insufficient: the runtime config must describe one included stable .NET 11 Microsoft.NETCore.App, with no shared-framework reference, and the manifest must include coreclr, hostfxr, hostpolicy and System.Private.CoreLib. The apphost must be x64 PE. Hashes establish package consistency, not publisher authenticity or binary behavior; the experimental bundle remains unsigned. Actual execution of the final runtime/dependency closure is still part of final acceptance.

New directories are created with an explicit protected DACL and Administrators owner. SYSTEM/Administrators have full control; LocalService only read/execute. Files are created atomically with a private SYSTEM/Administrators ACL, copied using read-shared source streams that exclude concurrent writes/deletion, flushed and verified against the captured manifest, then granted LocalService read/execute. Unverified staged bytes are not exposed to LocalService. The existing Program Files/product boundary, owners and effective allow ACEs are checked; null DACLs, untrusted standard/generic write rights and unsupported ACEs fail closed. Fixture tests use only the current test user's trusted ownership in an isolated build directory, never as a production/CLI override.

The service is the fixed dedicated own-process `RelaxDeskBroker`, **LocalService**, normal error control, with no dependencies/group. New registration starts manual even when automatic was requested, is hardened, then receives the requested startup type. Service-object owner is Administrators; SYSTEM/Administrators have full control. The explicitly allowed user gets only QueryConfig/QueryStatus/READ_CONTROL (`0x20005`), not start/stop/configuration/delete/DACL/ownership rights. This does not change the SCM database ACL. The existing broker pipe still authenticates SID/PID/session and requires a running SCM process; desktop permissions remain wholly separate.

Before opening the production pipe, service startup verifies its fixed protected root, active/ready receipt, configured user, current release, ACLs and exact file hashes/count. A stale, inactive, pending-delete, modified or unlisted deployment cannot start the broker. This is defense in depth after the native/runtime loader; safe protected image installation before SCM registration remains essential.

## Update, failure and removal semantics

A single named setup mutex serializes this component. A protected `broker-install.json` records the immutable user, current/known releases and readiness/removal state. Files are staged and verified before stopping an old broker. Every service mutation compares the expected owned command/account/type/start policy; foreign registration, changed account/path/rights or user mismatch is refused. Command paths are quoted, and only captured canonical SID/GUID values enter the service command.

Setup holds and releases the Windows mutex on the same worker thread, including while SCM reports start, stop, or deletion pending. The transaction fixture makes each of those waits asynchronous to cover a first start that requires polling and the equivalent update, rollback, and uninstall paths.

Updates stop only the verified service and wait, never terminate an arbitrary process. The new known release is recorded as not ready before configuration changes; the active receipt is atomically replaced only after configuration. Pre-commit failures restore the prior configuration/receipt/running state where possible. A staging-only failure does not stop a running old broker. Unverified/modified partial files are retained with a protected recovery record rather than blindly deleted. Stop/start are bounded to ten seconds each; recovery has its own bounded token. Unrecoverable rollback reports an error and retains protected evidence for explicit inspection. Startup OS/RPC hangs, process termination, power loss and full disk are not claimed atomic or automatically repaired.

After commit, unchanged old files are pruned; locked/read-only/modified or unknown content remains tracked. At most four release records are retained. Unknown files/empty folders are preserved. A commit followed by cleanup failure can leave the new active release plus retained previous records; inspect the error/receipt, and do not assume a generic error means no change. Re-running with the same explicit user can operate on a known owned registration/release; unowned/inconsistent state is never force-adopted.

Uninstall stops the verified broker, records not-ready/pending deletion, calls DeleteService and closes handles. **No binaries are removed until the SCM record is actually gone.** Windows can retain a marked service while another handle/management window is open. Pending deletion returns JSON `PendingDelete=true`, `Changed=false` and exit 2; keep files, close relevant service management handles and rerun the same uninstall. A foreign re-created service is not removed. Missing SCM registration with a valid owned receipt can still clean owned files. Modified/unknown/locked files return `RetainedFiles=true`; unchanged owned content fully removes the broker root. Application logs, current-user application installation and shared product parent are preserved. There is no forced recursive delete, delayed reboot deletion or automatic security-policy repair.

The operations affect service-coordinated agents: stop/update makes the optional HostShell lease fail and its own Host stop. There is no restoration of prior Viewer approval, input, clipboard or file grant after restarting. Portable mode remains independently usable by explicit user choice.

## Validation

Evidence distinguishes real filesystem/ACL checks, simulated SCM transactions, actual read-only Windows service metadata/ABI queries, public CLI preview/denial and unexecuted privileged mutations. Reproduce the nonprivileged contract suite:

```powershell
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\service-setup-final-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-viewport-release\Release\RelaxDesk.Native.dll
dotnet build/service-setup-final-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll build/service-setup-final-artifacts/bin/RelaxDesk.Service/release/RelaxDesk.Service.dll --service-setup-contracts E:\RelaxDesk\build\machine-service-setup-verification
```

Still required: actual elevated deployment/ACLs, Create/Change/SetSecurity/Delete/Start/Stop under SCM, LocalService effective read/query/impersonation permissions, final self-contained payload execution, manual/automatic boot behavior, pending-deletion handles, protected upgrade/uninstall under file locks/AV/power loss, cross-account denial and the Home physical session transition matrix. Logon launch/restart coordination and audio now have separate source contracts; their actual deployment/hardware acceptance remains pending. Do not count fixture runtime stand-ins or simulated service startup as those exits.

API references: [CreateService and LocalService/startup policy](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-createservicew), [service security rights](https://learn.microsoft.com/en-us/windows/win32/services/service-security-and-access-rights), [setting service security](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nf-winsvc-setserviceobjectsecurity), [deferred DeleteService semantics](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-scmr/6744cdb8-f162-4be0-bb31-98996b6495be), [atomic directory creation with security](https://learn.microsoft.com/en-us/dotnet/api/system.io.directoryinfo.create).

## 本机 UAC 与管理员代理验收

在普通权限 Windows 交互会话中，从仓库根目录运行 `dotnet <Viewer.dll> --admin-real-worker-contracts <Host.dll>`。需要已部署的受保护机器 Broker 及可用的 DXGI 捕获。测试连接本机 Host，发出独立的管理员申请，等待本机用户确认 Windows UAC，再从 Viewer 降级回 Work。成功要求普通输入授权恢复，且同一条 TLS 连接继续收到视频帧。

本机实测通过。机器 Broker 修复退出码为 0，普通权限 Host 预检确认受保护代理已就绪，实机测试退出码为 0。该测试覆盖普通桌面的代理启动和降级。管理员窗口键鼠、UAC 安全桌面输入、锁屏及登录界面仍需各自验收。

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../benchmarks/README.md).
