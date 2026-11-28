# Viewer error details

The connection footer now offers a collapsed **最近一次错误详情** section after configuration, connection, handshake, decode/render/UI or legacy preflight errors. It stays outside the disabled connection form so the last error can be inspected during bounded retry. Successful connection or a fresh manual connect clears stale details; **清除详情** clears the local view only. Normal Host termination/user cancellation does not create an error. Existing retry, approval, identity and capability decisions are unchanged.

The readonly/selectable snapshot contains UTC, an allowlisted local stage, local diagnostic correlation ID, up to four exception types and HRESULTs, and numeric native error codes for Win32/socket exceptions. Unknown stage/ID input becomes `unknown`/`none`; type names and report size are bounded. The Core snapshot never retains exception messages, Data, Source, HelpLink, stack paths, endpoint fields, passwords, invitations, certificate fingerprints, clipboard text or input. This is selected diagnostic metadata, not a promise to redact arbitrary secrets from arbitrary text. Correlation IDs are the existing process-local labels, not wire tickets or cross-machine identity.

**复制安全诊断摘要** copies the full snapshot; Ctrl+C/context-menu Copy on selected diagnostic text copies that selection through the same async path. Only this readonly metadata control supplies selection text. No clipboard content is read, no remote clipboard permission is requested or changed, and nothing is copied automatically. A single background STA worker publishes at most 2,048 characters; concurrent native copies fail fast. The UI waits at most two seconds, remains responsive and offers retry/selection on failure. Clear/new-error/success generations suppress stale feedback. Closing the window cancels its wait. An already entered native clipboard call cannot be forcibly canceled; its background worker may finish later and prevents additional native workers until it returns.

Executable validation:

```powershell
dotnet <Viewer.dll> --error-details-contracts build/error-details-fixtures
dotnet <Viewer.dll> --pipeline-failure-contracts <Host.dll> cpu-decode
dotnet <Viewer.dll> --pipeline-failure-contracts <Host.dll> cpu-submit
dotnet <Viewer.dll> --pipeline-failure-contracts <Host.dll> viewer-update
```

The dedicated WPF test uses isolated logs and memory copy sinks, not the actual Windows clipboard. It checks invalid endpoint and legacy fingerprint preflight without networking, private-field/message exclusion, readonly selection, button/command copying, failure/duplicate/pending/timeout isolation, stale completion and clear/success lifecycle, with normal/minimum-size render artifacts. Production Host/TLS failure tests also assert automatically populated stage/correlation and absence of actual fixture pin/token/message. Evidence. Real local clipboard publication and physical Home/remote application behavior remain operator checks for the final unified package.

> Historical benchmark attachments were removed during documentation cleanup. Historical results describe their original revision, not current acceptance; original records remain in Git history. See the [current benchmark index](../benchmarks/README.md).
