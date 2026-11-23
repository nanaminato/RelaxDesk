# Session broker / interactive agent validation — 2026-11-24

Implemented: dedicated SCM LocalService entry and bounded authenticated local pipe, explicit single-user SID configuration, per-session leases/heartbeats, process/session health, stop/session invalidation, and optional HostShell supervision. Service has no Windows graphics/WPF dependency, native DLL, media stack, network listener or process launch/termination implementation. Source-only optional service support is not an installed/accepted Windows service. No package or machine service was created.

Environment: stable .NET 11.0.100, development Server/RDP runner. Nine-project Release solution build succeeds with zero warnings/errors at `build/broker-final-artifacts`, existing native viewport module for interactive Host only. Actual service output contains Core/Session/Service managed assemblies and no RelaxDesk.Windows or native graphics DLL.

Verified:

- **52 new neutral session-agent checks**: canonical bounded/duplicate-free framing, version/identity/session-zero denial, nonce/lease/counter replay barriers, per-session uniqueness, four-session cap, revocation/stop, stale cleanup safety and concurrent registration. Current full Core/TLS count **1,787**.
- **26 actual cross-process Windows pipe checks**: native peer PID/session and impersonated/process-token SID; approved-user registration and explicit foreign-SID policy rejection; production-client rejection of a console server; wrong expected PID; first-instance collision; 16 rapid abandoned connections; duplicate registration; heartbeat liveness/replay; silent open peer expiry under eight seconds; session invalidation and service stop; slot/handle/process cleanup. Protected production SDDL is inspected for no client create-instance permission. The console broker runs under the same test user, so this is not a real cross-account LocalService/DACL or remote SMB acceptance test.
- **22 actual WPF HostShell/Host/TLS checks**: default off, required mode blocks an unconnected Start, service connection does not start sharing, fresh explicit Viewer approval, synthetic H264 frame, both broker STOP and abrupt process death stop the owned Host and produce its explicit network end notice; no automatic portable fallback; operator-selected ordinary mode remains usable; Host native resources return to zero.
- Early pipe testing found the client-close-before-ConnectNamedPipe race (Win32 232). The accept loop now replaces only that abandoned instance while retaining the pipe name anchor; rapid-close regression is included.
- A first WPF test attempt ran a stale binary after compilation failed; that exact test process was identified and closed without starting sharing. Subsequent tests used successfully built isolated final artifacts. The initial Core test's missing InvalidDataException catch was corrected, its error-reporting process cleaned up, and the actual protocol suite passed. These initial failures are not counted as acceptance.

Full regression completed with **exit code 0** on the final source/build. It includes 1,787 Core/TLS checks, 26 actual broker IPC checks, 22 WPF coordination checks and the existing display/topology, clipboard/files/resume, pinned reconnect, media recovery, CPU WPF and resource/liveness suites. Command:

```powershell
./scripts/verify.ps1 -SkipBuild -ArtifactsDirectory build/broker-final-artifacts -NativeBuildDirectory build/native-viewport-release -Graphics -Wpf -Capture -SkipInteractiveInput -SkipPhysicalGraphics
```

Raw generated log: `build/broker-regression.txt`. Final regression includes the new actual broker and WPF coordination tests plus existing display/topology, clipboard/files/resume, reconnection, media recovery, CPU WPF rendering and resource/liveness checks. Foreground input, actual desktop capture and physical GPU visibility are explicit skips. User installer tests are separate evidence from the preceding slice, not rerun/combined into this IPC count.

Not accepted: real SCM registration/start/stop under LocalService, status/handler ABI behavior and Event Log UI, protected machine installation/ACLs and uninstall, opt-in boot/logon agent launch/restart, unrelated account/logon access enforcement, remote pipe attempts, Home physical capture/input/clipboard/file transition, lock/logoff/UAC/user switch/reboot, arbitrary UI/OS-query hangs or long soak. The existing user-level installer does not register/start this service, even when a future bundle contains its executable. No automatic startup/firewall/UAC/RDP change, secure desktop control or physical performance/MSTSC ratio claim.

[Contract, reproduction and exact remaining acceptance](../docs/SESSION-BROKER.md).
