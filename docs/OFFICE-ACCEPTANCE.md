# Shared office acceptance entry points

The final EXE collector now uses the same executable office cases as developer source verification. This closes the old collector's video-only coverage gap without depending on Python, a global SDK, or PowerShell script execution in the final bundle. No intermediate package is created by this source change.

For an isolated Release build:

```powershell
dotnet <Smoke.dll> --office-suite-source build/my-artifacts build/fresh-office-results
dotnet <Smoke.dll> --collector-contracts build/collector-fixtures
```

Source verification resolves Host/Viewer/HostShell/Service/Smoke/Tests DLLs from the normal `--artifacts-path` layout and launches them through `dotnet`. It refuses an existing result directory, saves per-step output/exit/time and the exact `office-suite.json` case list/exclusions, and continues after independent child failures. Its only passing status is `PASS_SOURCE_AUTOMATION_ONLY`; it creates neither a distributable nor a ZIP and never claims packaged-runtime or Home acceptance.

In the final extracted bundle, `Test-Office.cmd` calls `Smoke.exe --collect-package <root> --office-only`. The collector first verifies the package hash manifest, then executes the same cases through the included EXEs. Its passing status is `PASS_PACKAGED_SYNTHETIC_OFFICE_ONLY`; Home eligibility is explicitly not evaluated and all physical measurements remain `NOT_TESTED`. Do not combine this mode with `--verify-only` or `--diagnostic-only`. Actual final self-contained execution remains to be validated when the owner requests the unified package.

`Collect-Logs.cmd` retains the full Home eligibility/environment gates, deployment/runtime/version checks and separately gated capture/visible-GPU/benchmark checks, and also runs the office cases. It still cannot certify visible correctness or actual input. `--verify-only` checks listed-file integrity only; this is not publisher authentication and the experimental bundle remains unsigned. The optional PowerShell full graphics/WPF step now has a 1,800-second outer budget because the expanded office tests already exceed the historical 300-second bound. Individual EXE office cases retain 120-second budgets, and all waits/owned-child termination are bounded.

The **76 office cases** cover Core/TLS, collector integrity/negative cases, broker and simulated deployment, Windows/session/input/display metadata, viewport, text/rich/file clipboard, scoped file queues/resume/denial, persistent Host, controlled TCP delay/cap/cuts, monitor switching/topology, WPF approvals/denial/revoke, Viewer failure stages/privacy/error details/history/statistics/settings, audio with synthetic endpoints, pinned reconnect and HostShell service/recovery coordination. Test fixture file contents and providers are generated; no actual desktop capture, foreground keyboard injection, audible sound, real SCM/startup mutation, PowerShell installer contracts, >1GiB copy, long soak or matched office/MSTSC benchmark belongs to this suite. Those have separate commands/evidence and remain explicitly excluded in its manifest.

Collector stdout/stderr capture retains at most 1,048,576 UTF-16 characters per stream and continues draining excess output so child pipes do not deadlock. Truncation, timeout, missing executable, capture failure or evidence-write failure is FAIL even if the child exits 0. Expected negative fixtures in the collector contract intentionally exercise `overflow` and `unwritable` failures; the enclosing contract passes only when these are correctly rejected. Diagnostic text/files can be retained under the result folder for review; generated fixture trees may be sizable and are never deleted automatically.

Package manifest input is streamed, capped at 4MiB/8,192 entries/depth 8. It validates bounded normalized relative paths, case-insensitive duplicates, SHA-256 syntax, optional exact byte length and actual hash; traversal, rooted/ADS/ambiguous paths and redirected ancestors/files fail. This is a path/integrity guard, not authentication of an untrusted publisher or a security boundary against the user changing their own filesystem concurrently.

The optional PowerShell `Collect-Logs.ps1` delegates hash verification to the same included EXE, avoiding a second weaker manifest parser. It still runs only when script execution is allowed; the default CMD/EXE path does not require PowerShell scripts or bypass policy.

See [collector validation](../benchmarks/2026-11-24-office-collector-validation.md) and the updated [physical result template](PHYSICAL-ACCEPTANCE.md). Passing source/synthetic cases never changes the pending Home/physical/privileged deployment status.
