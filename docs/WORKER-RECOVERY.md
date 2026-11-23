# Explicit worker recovery

HostShell offers a default-off checkbox for restarting its own Host child after an unexpected nonzero exit. One explicit manual Start gets at most three retries with 1/2/4 second delays. Listening or healthy frames never replenish this budget. Normal Stop, normal process exit, Viewer session end, service lease loss, cancellation and closing do not authorize recovery. Failure to launch a new process stops the attempt; retry exhaustion requires a manual Start.

During backoff the address, port, file root, display and service mode controls remain frozen. The original backend/configuration stays in process memory only. Immediately before launch the current Windows session/desktop is checked again; service mode additionally requires the existing healthy lease and cannot fall back to portable mode. Stop or disabling recovery cancels a pending delay. Delayed events from older child generations are ignored.

A recovered Host is a new process with a new ephemeral certificate, pairing state and fresh connection approval. It does not replay input, grant clipboard/files, resume an old socket or bypass changed certificate checks. The viewer may need an explicit new connection/pin selection. This option is not unattended access or process-independent file task recovery.

`--logon` opens only the visible HostShell; `--service-agent` additionally attempts an authenticated local broker lease. Neither starts sharing or enables crash recovery. A per-user/session mutex prevents duplicate normal/logon HostShell windows; test commands are intentionally separate. Unknown or repeated launch flags exit with code 2.

Automated evidence uses actual owned child processes, WPF, TLS and synthetic media. Real reboot/logon, Windows Startup Apps UX, LocalService runtime and Home two-PC acceptance remain pending. Abrupt process death loses its process-local input ledger; release of physically held keys/drag after a real crash is specifically unverified. No synthetic result establishes physical Windows input, visible GPU quality or MSTSC responsiveness.
