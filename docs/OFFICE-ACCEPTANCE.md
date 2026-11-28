# Shared office acceptance entry points

The final EXE collector now uses the same executable office cases as developer source verification. This closes the old collector's video-only coverage gap without depending on Python, a global SDK, or PowerShell script execution in the final bundle. No intermediate package is created by this source change.

For an isolated Release build:

```powershell
dotnet <Smoke.dll> --office-suite-source build/my-artifacts build/fresh-office-results
dotnet <Smoke.dll> --collector-contracts build/collector-fixtures
```

Source verification resolves Host/Viewer/HostShell/Service/Smoke/Tests DLLs from the normal `--artifacts-path` layout and launches them through `dotnet`. It refuses an existing result directory, saves per-step output/exit/time and the exact `office-suite.json` case list/exclusions, and continues after independent child failures. Its only passing status is `PASS_SOURCE_AUTOMATION_ONLY`; it creates neither a distributable nor a ZIP and never claims packaged-runtime or Home acceptance.

The pre-QUIC Viewer from `e9c3ea3` passed `Smoke <Host.dll> --wpf <Viewer.dll> hardware-cpu` against Host revision `daf86d1`. The Host selected TCP/TLS, the historical Viewer submitted 60 H.264 frames, and both processes reported zero remaining native resources. This is a cross-version loopback with a synthetic Host source and the workspace native DLL.

In the reverse direction, Viewer `6150476` passed `Viewer --statistics-contracts <Host.dll> <output> legacy-auto` against Host `e9c3ea3`. Auto selected TCP/TLS and the WPF panel reported that the legacy Host did not offer UDP. The test observed live frame, byte and control RTT samples and verified cleanup on disconnect.

The `58514fc` candidate role EXEs repeated both directions against the
pre-QUIC `e9c3ea3` source build. The old Viewer submitted 60 H.264 frames
from the new Host over TCP/TLS. The new Auto Viewer used TCP/TLS with the
old Host and displayed the missing UDP offer in its statistics. The newer
`ed21ca0` role EXEs also passed bidirectional TCP/TLS sessions against the
`58514fc` candidates. These runs used synthetic sources and local loopback.

For UDP diagnostics, run the built Host with `--deployment-info` to record the .NET runtime directory and recognized QUIC provider. Use a current `RelaxDesk.Native.dll` from the native build and pass its full path via `-p:RelaxDeskNativePath=...` when building the solution. If the machine's runtime cannot run the production UDP cases, repeat those cases with the matching native library and MsQuic provider in an isolated runtime. Put that runtime first on `PATH` for the test process so its child `dotnet` processes use it too. Keep the original report. The core suite also exercises `System.Net.Quic` and can have different provider requirements from the production DATAGRAM transport.

To run both sets of cases in one report, start with the isolated runtime first on `PATH` and pass the full path to the system `dotnet.exe` for Core. The choice applies only to the `office-core-tls` case and is recorded in `office-suite.json`.

```powershell
dotnet <Smoke.dll> --office-suite-source build/my-artifacts build/fresh-office-results --core-dotnet 'C:\Program Files\dotnet\dotnet.exe'
```

In the final extracted bundle, `Test-Office.cmd` calls `Smoke.exe --collect-package <root> --office-only`. The collector first verifies the package hash manifest, then executes the same cases through the included EXEs. Its passing status is `PASS_PACKAGED_SYNTHETIC_OFFICE_ONLY`; Home eligibility is explicitly not evaluated and all physical measurements remain `NOT_TESTED`. Do not combine this mode with `--verify-only` or `--diagnostic-only`. Actual final self-contained execution remains to be validated when the owner requests the unified package.

`Collect-Logs.cmd` retains the full Home eligibility/environment gates, deployment/runtime/version checks and separately gated capture/visible-GPU/benchmark checks, and also runs the office cases. It still cannot certify visible correctness or actual input. `--verify-only` checks listed-file integrity only; this is not publisher authentication and the experimental bundle remains unsigned. The optional PowerShell full graphics/WPF step now has a 1,800-second outer budget because the expanded office tests already exceed the historical 300-second bound. Individual EXE office cases retain 120-second budgets, and all waits/owned-child termination are bounded.

The **77 source-defined office cases** cover Core/TLS, collector integrity/negative cases, broker and simulated deployment, Windows/session/input/display metadata, viewport, text/rich/file clipboard, scoped file queues/resume/denial, persistent Host, controlled TCP delay/cap/cuts, monitor switching/topology, WPF approvals/denial/revoke, Viewer failure stages/privacy/error details/history/statistics/settings, audio with synthetic endpoints, pinned reconnect and HostShell service installation UI/service/recovery coordination. The new UI case uses a fake privileged launcher and never prompts for UAC or installs a service. The delivered g5ebe35d archive retains its historical 76-case definition. Test fixture file contents and providers are generated; no actual desktop capture, foreground keyboard injection, audible sound, real SCM/startup mutation, PowerShell installer contracts, >1GiB copy, long soak or matched office/MSTSC benchmark belongs to this suite. Those have separate commands/evidence and remain explicitly excluded in its manifest.

Collector stdout/stderr capture retains at most 1,048,576 UTF-16 characters per stream and continues draining excess output so child pipes do not deadlock. Truncation, timeout, missing executable, capture failure or evidence-write failure is FAIL even if the child exits 0. Expected negative fixtures in the collector contract intentionally exercise `overflow` and `unwritable` failures; the enclosing contract passes only when these are correctly rejected. Diagnostic text/files can be retained under the result folder for review; generated fixture trees may be sizable and are never deleted automatically.

Package manifest input is streamed, capped at 4MiB/8,192 entries/depth 8. It validates bounded normalized relative paths, case-insensitive duplicates, SHA-256 syntax, optional exact byte length and actual hash; traversal, rooted/ADS/ambiguous paths and redirected ancestors/files fail. This is a path/integrity guard, not authentication of an untrusted publisher or a security boundary against the user changing their own filesystem concurrently.

The optional PowerShell `Collect-Logs.ps1` delegates hash verification to the same included EXE, avoiding a second weaker manifest parser. It still runs only when script execution is allowed; the default CMD/EXE path does not require PowerShell scripts or bypass policy.

See collector validation and the updated [physical result template](PHYSICAL-ACCEPTANCE.md). Passing source/synthetic cases never changes the pending Home/physical/privileged deployment status.

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../benchmarks/README.md).
