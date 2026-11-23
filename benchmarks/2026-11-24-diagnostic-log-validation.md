# Bounded diagnostic validation

Baseline `e5faf52`, branch `codex/dxgi-media-pipeline`, stable .NET 11.0.100. Release solution build at `build/diagnostic-log-final-artifacts` passes with zero warnings/errors; unchanged native DLL is `build/native-viewport-release/Release/RelaxDesk.Native.dll`. Final `build/diagnostic-log-accepted-artifacts` adds the persistent-session correlation assertion only and also builds warning/error free. No new package.

The audit found unbounded daily Host/Viewer disk logs and synchronous Viewer file appends from the UI path. A bounded background logger now limits new files/archives and queues, isolates storage failures, normalizes record length/control characters, and carries local async connection IDs without persisting input or credentials. [Behavior, retention, privacy and limitations](../docs/DIAGNOSTICS.md).

Observed automated results:

| Coverage | Evidence |
| --- | --- |
| 104 Windows logger assertions | Terminal exit 0; `build/diagnostic-log-contracts.txt`. Actual UTF-8 files/rolling retention, latest/truncation/control-character checks, exclusively locked file/recovery, directory/oversized/archive refusal, real junction/outside sentinel, concurrent writer records, 10,000-event bounded pressure, close and async correlation. |
| Core/protocol/access/TLS | 2,036 checks, terminal exit 0; `build/diagnostic-log-core.txt`. |
| Actual WPF Viewer wrapper | Terminal exit 0; `build/diagnostic-log-wpf.txt`. Dispatcher progresses, credential-bearing exception message is excluded while type/HRESULT/local ID persist, locked file is isolated and unlock recovers; fixture log only. |
| Actual Host injected producer failure | Terminal exit 0; `build/diagnostic-log-producer.txt`. Four-frame original encode-submit cause and session end/cleanup preserve one correlation ID. |
| Hardware runtime fallback / capture budget exhaustion | Terminal exit 0; `build/diagnostic-log-runtime-fallback.txt` and `build/diagnostic-log-capture-exhausted.txt`. Existing original-cause, bounded retry/fallback and native cleanup assertions pass. |
| Actual Host→WPF synthetic desktop session | Terminal exit 0; `build/diagnostic-log-session.txt`. Deflate decode/CPU submission and both process exits; Host native resources zero. |
| Actual HostShell audio/TLS integration | Terminal exit 0; `build/diagnostic-log-hostshell.txt`. Grant/denial/revoke/source/socket failure remain isolated despite added diagnostic suffixes. |
| Actual WPF reconnect integration | Terminal exit 0; `build/diagnostic-log-reconnect.txt`. Fresh consent/pin, revoked features/old input clearing, denial/cancel, changed certificate and five-retry exhaustion still pass; native resources zero. |

Filesystem and WPF fixture checks do not edit existing user logs. Production Host integrations use its normal current-user log location, as previous Host tests did; they send only synthetic media and normal structured errors, never fixture credentials or pixels. No actual desktop input, audible audio or secure-desktop operation is exercised. Hard power loss, a genuinely hung disk, Home physical held-input behavior and long-duration multi-session soak remain unaccepted. A drain barrier is not proof of successful persistence; the lock tests explicitly check the failure counter and recovery.

The final persistent Host test passes with terminal exit 0 (`build/diagnostic-log-persistent.txt`): distinct local diagnostic IDs across three separately approved same-process sessions, matching end/cleanup IDs, fresh grants/media state, stale-command rejection and console listener invitation rotation. Only this new assertion differs from the previously tested production source. No aggregate performance or “80% MSTSC” result is claimed. Continue Viewer failure-stage/remaining release audit with final unified packaging/operator testing deferred.
