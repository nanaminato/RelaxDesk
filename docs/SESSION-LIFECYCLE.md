# Host lifetime and fresh consent

HostShell and an interactive console launch now keep one Host process alive between sessions. A scripted `Host <IP> <port> [backend]` retains its one-session behavior for existing callers; use `Host --listen [--file-root <directory>] <IP> <port> [backend]` for repeated console sessions. The listener binds only the explicitly selected local address. No service, startup registration, firewall change or unattended authorization is installed.

Only one controller is admitted. While waiting for consent, the Host has not constructed capture/input or granted optional capabilities. After role-specific media/data binding, the listener closes while the current session runs. Teardown joins workers, revokes authorization, closes sockets and releases held input/native resources before reopening the listener. Denied/malformed/abandoned connections also return to listening. A 250ms cancellable delay bounds repeated failed-connection churn. TLS/hello has a 30-second limit; local approval has a 60-second limit, with disconnected or unsolicited-data peers removed during approval.

The Host process holds one ephemeral TLS certificate for its lifetime. Console pairing tokens rotate for every new invitation/connection attempt. A Host restart changes the certificate; nothing stores a permanent secret. `SessionClient.ConnectByPinnedApproval` supports fresh explicit approval against a previously verified SHA256 certificate without reusing an old pairing token. Missing/malformed pins and changed control/media/data certificates fail closed. This is a transport prerequisite; the Viewer automatic reconnect loop is not implemented in this checkpoint. First-contact IP/port approval still has its documented lack of out-of-band identity proof.

## Local UI boundary

Each connection owns a unique one-shot ConsentRequest plus independent cancellation/revocation scopes. Host stdio events carry `sessionId`; APPROVE/APPROVE_CLIPBOARD/APPROVE_FILES/APPROVE_ALL/DENY, DISCONNECT and REVOKE commands must include that exact current identifier. Stale decisions, duplicate decisions and old-session revocations/disconnects are ignored. This identifier is a local IPC correlation value, not a network authentication credential. The UI captures the displayed request ID rather than silently applying an old click to the latest peer. Command writes serialize.

“断开本次连接” cancels the current request/session and returns to waiting. “停止共享” cancels the global Host lifetime and exits the child. Closing HostShell also stops its own child; a closed stdio parent stops Host. File-root configuration remains fixed until global stop. New connection checkboxes always start unchecked; no previous clipboard/file approval or revocation carries into the next session. A `denied-v2` reply carries no media/data ticket, capability grant or content before the socket closes; older v2 clients also fail closed on this non-approval reply.

## Reproduce and limits

`Smoke <Host.dll> --persistent-host <fixture-directory>` launches the actual Host in shell mode and exercises several real pinned TLS sessions, stale local commands, explicit rejection, grants/revokes and cleanup. `HostShell --shell-contracts <Host.dll> <output-directory>` exercises actual WPF approval and both stop actions. All test capture/clipboard sources are synthetic/isolated and file content is generated under a unique fixture directory. [Evidence](../benchmarks/2026-11-23-session-lifecycle-validation.md).

This is not a Windows service, logon/reboot recovery, secure-desktop/UAC implementation, file resume or automatic Viewer reconnect. Physical Windows Home desktop and held-input cleanup require local-console dual-PC testing; synthetic media and zero native resource counters cannot prove those behaviors.
