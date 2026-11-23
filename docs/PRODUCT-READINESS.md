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
| Clipboard Unicode text | Implemented | Automated Verified: Core/TLS + production Host with isolated provider + WPF consent/revoke | Actual Windows clipboard/Home dual-PC pending |
| Clipboard image/HTML/files | Planned | None | Additional negotiated formats required |
| Scoped file engine + third TLS | Implemented CLI/library | Automated Verified: 593 Core/TLS, 29 Windows file contracts, real fixture files/Host/media/control | UI/folder orchestration/resume + Home LAN pending |
| File panel/folder queue/drag-drop/cross-session resume | Planned next | None | Local folder selection + complete task lifecycle required |
| Long-running Host / reconnect | Planned | None | One session per Host process |
| Multi-monitor / negative origins | Planned | None | Primary display only |
| Service/installer/audio | Planned | None | Secure desktop unsupported |

Current checkpoint: P0/P1 reliability/input slices and independently authorized text clipboard are implemented. [Office evidence](../benchmarks/2026-11-23-office-readiness.md), [clipboard evidence](../benchmarks/2026-11-23-clipboard-validation.md), [clipboard contract](CLIPBOARD.md).

Owner execution preferences: continue the complete remote-office goal; skip and label environment-blocked tests instead of stopping other development. **Do not package each slice.** Keep implementing/testing/committing; defer unified package and operator physical tests until the final milestone. Do not mistake skipped tests for passing evidence.

Historical packaged checkpoint: P0/P1 code through `0977e787b7c5d9816184a9aefbd369a9b885ef72`; the 0.4.2 ZIP was verified before clipboard development and does not contain this new feature. Actual capture remains denied (0x80070005); collector rejects Server/RDP for Home acceptance. Current source adds optional v2 clipboard capability/message types with independent grants; file/reconnect capabilities remain unadvertised. [Physical test/result template](PHYSICAL-ACCEPTANCE.md).

Next development slice: **Host local folder chooser + independent file consent; Viewer transfer panel/remote directory browser/multi-file-folder queue/drag-drop**, using the now functional FileTransferClient/WindowsFileRoot/FileTransferServer. Then scoped cross-session resume, persistent Host and secure reconnect. Negotiated scan-code/precision-pointer/TextCommit input and advanced clipboard formats remain required. Expanded F1-F24/OEM/numpad routing test is skipped because native foreground acquisition is unavailable in this runner; production gating stays strict. Do not count protocol-only/mock tests as physical office acceptance.

Physical exit: run existing `scripts/accept-home.ps1` or bundled `Collect-Logs.cmd` on an unlocked Home local console. Use two PCs and explicit approval; verify visible image, held Ctrl/drag disconnect, Microsoft Pinyin and Japanese Microsoft IME in actual remote applications, DPI, monitor unplug and real GPU reset. Record edition/build/driver/session/network/package identity. API Present success is not visible correctness or input-to-photon proof.
