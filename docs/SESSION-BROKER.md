# LocalService session broker and interactive agent

Implemented source: `RelaxDesk.Service` is a dedicated Windows SCM own-process entry; `RelaxDesk.Session` contains local IPC, identity/lease/liveness and separately invoked machine-maintenance code; neutral lease/framing models live in Core. Service references contain no WPF, capture, codec, clipboard or file-transfer implementation. The service does not open a network listener, approve a Viewer, launch/kill another process, acquire a launch token, migrate a worker to another session, inject input or capture Session 0.

This is a functional source milestone with actual cross-process pipe and HostShell/Host/TLS tests. **No SCM service has been installed or started for acceptance.** [Machine service installation/removal and protected binary deployment](MACHINE-SERVICE-INSTALL.md) now have source/contract evidence; actual elevated deployment, opt-in logon launch/restart coordination and physical lifecycle acceptance remain open. Portable/current-user installation still works without a service, and no new package has been generated.

## Trust and configuration

The SCM entry is named `RelaxDeskBroker`, requires a dedicated own-process **LocalService** identity in Session 0, and takes exactly one explicitly configured user SID (`--service --owner-sid <SID>`). There is no default Everyone/all-users policy, stored password or implicit unattended consent. Unknown/missing arguments fail. Changing the approved SID requires a controlled service restart; it cannot be changed over the pipe. Do not register from a per-user writable folder. The [explicit maintenance entry](MACHINE-SERVICE-INSTALL.md) now validates/stages a protected machine payload before registration; runtime also checks the active protected receipt and hashes before opening IPC. No service is installed or started automatically by ordinary HostShell/user installation.

Production pipe is `\\.\pipe\RelaxDesk.SessionBroker.v1`. Native creation uses a protected DACL: SYSTEM/server account full access, the configured user only individual data/attribute/read-control/synchronize rights (`0x0012019B`), with **no FILE_CREATE_PIPE_INSTANCE**, WRITE_DAC or WRITE_OWNER. It does not use generic client write access. `PIPE_REJECT_REMOTE_CLIENTS` rejects network clients; the client API only opens the local namespace. First-instance creation fails if the name already exists. During accept/replacement, one instance stays open to anchor the name. A client that closes before ConnectNamedPipe completes is treated as an abandoned connection, not a service-wide failure.

Before sending registration, the interactive client obtains the kernel pipe-server PID/session and requires Session 0 plus a matching **running SCM process** for the fixed service name. SCM configuration must be an own-process LocalService service. No console/user-process fallback is accepted in production. An administrator able to replace service configuration is outside this local unprivileged-client boundary. Correct machine binary/ACL installation is still required and has not been accepted here.

The server reads a bounded registration, then obtains kernel client PID and session and impersonates that connection only long enough to query its SID and open a read/query/synchronize handle to the same process. The token SID must exactly match the configured user; process-token SID and kernel session must agree, and the session must be nonzero/WTSActive. It holds the process handle to detect exit without a PID-reuse substitution. The impersonation callback contains no await, media, file transfer or arbitrary command/path execution and automatically reverts before processing continues. This is read-only identity inspection of an explicitly authorized local peer, not token duplication/elevation or cross-session process launch.

## Lease and liveness

Framing is a four-byte little-endian length followed by UTF-8 JSON, maximum 2,048 bytes/depth four. All five canonical members (`kind`, `nonce`, `sequence`, `lease`, `version`) are mandatory; unknown/duplicate members and versions are rejected. One-use connection nonce and generated lease ID bind registration and heartbeat. Heartbeat counters start at one and increase exactly; old/wrong nonce, lease, counter, type or version closes that peer. These values correlate a local authenticated channel and never grant remote screen/input/clipboard/files permissions.

The registry admits one agent per interactive session, at most four active sessions for the one configured user. There are at most seven handlers plus a listener and one transient replacement pipe (nine native instances). Initial messages and ongoing complete messages have a five-second deadline; the client heartbeats every second, bounds replies to five seconds and never reconnects silently. A silent, malformed or expired peer releases its slot. Session invalidation keeps a revoked slot occupied until its existing handler completes, and stale cleanup cannot remove a newer lease.

The SCM handler accepts stop/shutdown and queues console/remote disconnect, logoff, lock and session-terminate invalidations. The queue is bounded; overflow stops all leases rather than ignoring a security transition. Callbacks do no pipe IO or worker joins. Stop cancels the listener/readers, revokes leases and joins workers. A canonical local stop notice is best effort; EOF/timeout/crash also invalidates the agent. Event Log diagnostics use the service source and sanitized event types/HRESULTs, not nonce/lease values. Actual SCM callback, Event Log display and LocalService permission behavior remain untested.

Health checks establish process/session/IPC liveness; they are not an atomic OS transition guarantee. WTSActive alone does not prove an unlocked/default desktop. A hung Dispatcher/OS query can delay cooperative UI cleanup; the broker does not forcibly terminate arbitrary processes. Existing Host capture/input/clipboard desktop checks and Windows security remain authoritative.

## HostShell behavior

“系统服务协调（可选）” is a collapsed optional section. It defaults to ordinary user mode, creates no pipe connection and registers no startup task. Selecting service mode disables Start until the service identity/lease and current interactive desktop are verified. Connecting the service itself does not construct/start Host or grant Viewer access. The operator still presses Start and separately approves every Viewer and optional capability.

The choice cannot be changed while its Host runs. Service stop/crash/lost lease, invalid local desktop or connection failure stops **HostShell's own child** through its existing bounded STOP/teardown path, blocks further sharing and retains service mode. There is no automatic downgrade to portable mode, new network grant or restored old input. The operator must reconnect the service and approve a new Viewer, or explicitly choose ordinary mode while stopped. Closing the UI cancels a pending connection, disposes its lease/observer and stops its child. Service-coordinated cleanup is cooperative; a malicious process under the approved user can decline to cooperate, so leases are not a substitute for Host authorization or an OS security sandbox.

## Reproduce without installing a service

```powershell
dotnet build RelaxDesk.sln -c Release --artifacts-path E:\RelaxDesk\build\broker-final-artifacts -p:RelaxDeskNativePath=E:\RelaxDesk\build\native-viewport-release\Release\RelaxDesk.Native.dll
dotnet build/broker-final-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll build/broker-final-artifacts/bin/RelaxDesk.Service/release/RelaxDesk.Service.dll --broker-contracts
# WPF WinExe: redirect and wait, as scripts/verify.ps1 does.
# HostShell --service-contracts <Host.dll> <Service.dll>
```

Only explicit `RelaxDesk.Test.<32 hex>` console endpoints accept a test PID/SID/session verifier; production always uses SCM identity. Test servers run as the current user and therefore grant that same server SID full pipe rights. Tests inspect the production LocalService DACL separately; they do not prove cross-account ACL behavior. No fallback from production to this test verifier exists in HostShell UI. The test entry accepts STOP and invalidation on its own parent stdio, not on a public socket or the production pipe.

[Evidence](../benchmarks/2026-11-24-session-broker-validation.md) separates neutral protocol/state, actual Windows identity/pipe calls, WPF/synthetic Host/TLS and pending SCM/physical work. Required later acceptance: protected install/upgrade/uninstall; actual SCM LocalService start/stop/boot; approved and unrelated Windows accounts; different logon sessions; remote pipe access denial; session 0; real lock/logoff/fast-switch/UAC; stale/missing service identity; UI/process/OS-query hang; orphan-worker cleanup; explicit recovery/fresh consent; file/clipboard/input teardown and long resource soak on Home/Pro where available. No secure desktop, reboot-persistent connection or unattended access is claimed.

## API references

- [Named-pipe access and the generic-write/create-instance overlap](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).
- [CreateNamedPipe first-instance and local-only modes](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createnamedpipew).
- [Kernel pipe peer process/session functions](https://learn.microsoft.com/en-us/windows/win32/ipc/pipe-functions).
- [SCM handler callback requirements](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/nc-winsvc-lphandler_function_ex).
- [Service status/accepted controls](https://learn.microsoft.com/en-us/windows/win32/api/winsvc/ns-winsvc-service_status).
