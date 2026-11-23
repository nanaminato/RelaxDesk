# Bounded local diagnostics

Current source writes `%LOCALAPPDATA%/RelaxDesk/logs/host.log` and `viewer.log`; each has three rolling archives (`.1` newest through `.3`). Each file is limited to 2MiB, for at most 8MiB newly managed data per role. Existing date-named logs from historical packages are preserved and are not included in this bound or automatically removed. Oversized pre-existing managed files are refused, not silently truncated. No package is produced by this change.

Host keeps existing structured stage/type/HRESULT/reason and cleanup diagnostics on its console; startup fingerprint, pairing token, invitation and protocol/input payloads never go through the file logger. Viewer stores only operation, exception type/HRESULT and local correlation, deliberately excluding exception messages that can contain user-supplied credentials. Normalized records replace control characters, cap diagnostic text at 2,048 UTF-16 characters and mark truncation; bounded UTF-8 records fit within the minimum configured file cap. Neither screenshots, clipboard text, file contents nor full keyboard data belong in this API. This is developer-controlled diagnostic text, not a general-purpose redactor for arbitrary secret strings.

Each accepted Host connection and Viewer v2 attempt uses a new random `correlation_id`, carried by execution context into async/thread workers and restored at scope exit. Host `session_end` and `session_cleanup` share that connection's ID. IDs differ across sequential sessions/reconnect attempts. These IDs are process-local diagnostic labels; they are not transmitted, do not match across the two PCs and grant no authentication/permission. Existing HostShell consent IDs and network tickets are unchanged.

One background worker per role owns file IO/rotation. Callers only normalize and attempt insertion into a 64-entry queue; they never wait on disk, the named writer mutex or file permissions. A full/closed queue drops the new record and increments a counter, preserving already queued records. File/permission/rotation failures increment a separate counter and do not stop the session. Subsequent records carry cumulative `logger_dropped`/`logger_file_failures`, when writing becomes possible. `TryWrite` means queued, not persisted. `Flush` is a queue-drain barrier, not a durability or write-success assertion; file failure counters must also be checked. Normal process exit drains for up to two seconds. Abrupt crash, full queues or a blocked filesystem can lose recent diagnostics; this is not an audit journal.

Rotation serializes same-session writers with a path-derived local mutex and a 100ms acquisition budget. Appends share read access; directory/reparse paths, redirected ancestors and invalid archive destinations are refused. Same-user filesystem changes can still race path inspection; this logger is not a security boundary against the account controlling its own directory. Cross-session writer contention may drop records instead of blocking application work. Legacy logs and foreign/oversized data are not silently deleted. A blocked kernel filesystem call can outlive the bounded exit wait; no UI or remote-control worker joins it indefinitely.

Viewer logs remain lazy and close after the WPF application exits. Host starts the logger before file-resume storage and disposes it afterward, so storage teardown diagnostics can still drain. No UI dependency is added to Host/Core. No wire capability, permission grant, service, startup or firewall behavior changes.

Executable checks:

```powershell
dotnet <Smoke.dll> --diagnostic-log-contracts build/diagnostic-fixtures
dotnet <Viewer.dll> --diagnostic-log-contracts build/diagnostic-fixtures
dotnet <Smoke.dll> <Host.dll> --persistent-host build/persistent-fixtures
```

The Viewer executable needs an ordinary WPF-capable Windows session. All test log files use isolated fixture directories, including an exception whose message contains a generated credential sentinel; that sentinel must not persist. `scripts/verify.ps1` includes filesystem contracts and the optional `-Wpf` privacy/dispatcher test. [Evidence](../benchmarks/2026-11-24-diagnostic-log-validation.md) distinguishes actual file/TLS/WPF execution from pending Home/physical and long-soak acceptance.
