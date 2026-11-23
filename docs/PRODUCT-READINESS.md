# Product readiness ledger

Reviewed 2026-11-23: local and fetched remote `codex/dxgi-media-pipeline` both `b9c7428873ad2630f2896af624f8e2baa9997e92`; no pre-existing edits. Stable .NET 11.0.100. Phase 0 exit met; Phase 1/1.5 physical Home dual-PC exit remains open.

| Capability | Implementation | Automated evidence | Physical evidence / remaining work |
| --- | --- | --- | --- |
| TLS, approval, strict pin, one-use media ticket | Implemented | Automated Verified: Core/TLS + five denial smokes | Home dual-PC pending |
| DXGI/HDR/H264, GPU display, CPU compatibility | Implemented | Synthetic codec / WARP contracts | Blocked: Home/NVIDIA/visible image/HDR appearance |
| Bounded capture/device recovery + IDR | Implemented | Automated Verified: state budget + production Host/TLS/decoder fault injection | Actual GPU loss pending |
| Silent control peer cleanup | Implemented v2 | Automated Verified: open sockets/no heartbeat -> ~20s cleanup; static desktop + heartbeat stays connected | Real held-key/drag/network loss pending |
| VK/modifiers, remote IME routing, focus release | Implemented | WPF routing contracts | Chinese/Japanese apps pending; scan-code transport/text commit Planned |
| Auto-hiding fullscreen toolbar | Implemented | Real HWND lifecycle contracts | Physical hover/mixed DPI pending |
| Dedicated remote shortcuts / pointer burst handling | Implemented | Automated Verified: 15 menu actions, balanced authorized chords, bounded motion coalescing | Remote application behavior pending |
| Clipboard text/image/HTML | Planned | None | Independent opt-in capability required |
| File/folder upload/download/drag-drop/resume | Planned | None | Scoped selection + bounded data channel required |
| Long-running Host / reconnect | Planned | None | One session per Host process |
| Multi-monitor / negative origins | Planned | None | Primary display only |
| Service/installer/audio | Planned | None | Secure desktop unsupported |

Current slice: P0 bounded recovery/control liveness. [Evidence and commands](../benchmarks/2026-11-23-office-readiness.md).

Delivery checkpoint: P0/P1 code committed through `0977e787b7c5d9816184a9aefbd369a9b885ef72`; self-contained 0.4.2 ZIP freshly extracted/manifest-verified and synthetic office/Host-WPF regressions passed without SDK/global runtime. Actual capture remains denied (0x80070005); collector rejects Server/RDP for Home acceptance. [Physical test/result template](PHYSICAL-ACCEPTANCE.md). No clipboard/files/reconnect capability is advertised. Protocol v1/v2 message types and granted permissions remain unchanged.

P1 shortcuts/queue slice complete; expanded F1-F24/OEM/numpad routing test added but blocked because native foreground acquisition is unavailable in this runner. Existing routing rejects input as designed. Next: negotiated scan-code/precision-pointer input and opt-in text clipboard. Do not count protocol-only or mock tests as complete office features.

Physical exit: run existing `scripts/accept-home.ps1` or bundled `Collect-Logs.cmd` on an unlocked Home local console. Use two PCs and explicit approval; verify visible image, held Ctrl/drag disconnect, Microsoft Pinyin and Japanese Microsoft IME in actual remote applications, DPI, monitor unplug and real GPU reset. Record edition/build/driver/session/network/package identity. API Present success is not visible correctness or input-to-photon proof.
