# Product readiness ledger

Initial audit 2026-11-23: local and fetched remote `codex/dxgi-media-pipeline` both `b9c7428873ad2630f2896af624f8e2baa9997e92`; no pre-existing edits. Stable .NET 11.0.100. Phase 0 exit met; Phase 1/1.5 physical Home dual-PC exit remains open.

| Capability | Implementation | Automated evidence | Physical evidence / remaining work |
| --- | --- | --- | --- |
| TLS, approval, strict pin, one-use media ticket | Implemented | Automated Verified: Core/TLS + five denial smokes | Home dual-PC pending |
| DXGI/HDR/H264, GPU display, CPU compatibility | Implemented | Synthetic codec / WARP contracts | Blocked: Home/NVIDIA/visible image/HDR appearance |
| Bounded capture/device recovery + IDR | Implemented | Automated Verified: state budget + production Host/TLS/decoder fault injection | Actual GPU loss pending |
| Silent control peer cleanup | Implemented v2 | Automated Verified: open sockets/no heartbeat -> ~20s cleanup; static desktop + heartbeat stays connected | Real held-key/drag/network loss pending |
| Scan/VK/modifiers, precision pointer, explicit text commit, focus release | Implemented | 818 Core/TLS, 54 native plans, 32 WPF input/text adapter contracts | Actual foreground rich source/Chinese/Japanese remote apps/UIPI/physical pointer pending |
| Auto-hiding fullscreen toolbar | Implemented | Real HWND lifecycle contracts | Physical hover/mixed DPI pending |
| Dedicated remote shortcuts / pointer burst handling | Implemented | Automated Verified: 15 menu actions, balanced authorized chords, bounded motion coalescing | Remote application behavior pending |
| Clipboard Unicode text | Implemented | Automated Verified: Core/TLS + production Host with isolated provider + WPF consent/revoke | Actual Windows clipboard/Home dual-PC pending |
| Clipboard image/HTML/files | In progress | Bounded content/worker/framing, HTML byte offsets, PNG validation and in-box WIC contracts; rich Windows adapter implemented | Image/HTML dedicated binding/consent/UI and actual clipboard IO pending; file-list intents still planned |
| Scoped file engine + third TLS | Implemented | Automated Verified: 599 Core/TLS, 29 Windows file contracts, real fixture files/Host/media/control | Home LAN/long soak pending |
| File panel/folder queue/confirmed drag-drop | Implemented | 22 WPF checks + cleanup; actual Viewer/Host session and live revoke | Physical Explorer/Home LAN pending |
| Cross-session file task/byte resume | Implemented in memory | 682 Core/TLS, real TCP/filesystem contracts, production WPF/TLS directory recovery, real >1GiB payload + SHA-256 | Five-minute process-local scope; crash/reboot, physical LAN/IO soak pending |
| Long-running single-controller Host | Implemented | 622 Core/TLS, real same-process sequential sessions and WPF disconnect/listen checks | Physical Home/long soak pending |
| Viewer automatic reconnect | Implemented v2 | 656 Core/TLS + actual WPF/Host/TCP-cut success, denial, cancellation, changed pin and five-retry exhaustion | Physical LAN/long soak pending |
| Multi-monitor / negative origins | Planned | None | Primary display only |
| Service/installer/audio | Planned | None | Secure desktop unsupported |

Current checkpoint: reliability/input, independently authorized text clipboard, scoped file UI/queues, persistent Host, bounded pinned Viewer reconnect and scoped file task/byte resume are implemented. [Office evidence](../benchmarks/2026-11-23-office-readiness.md), [clipboard evidence](../benchmarks/2026-11-23-clipboard-validation.md), [clipboard contract](CLIPBOARD.md).

Owner execution preferences: continue the complete remote-office goal; skip and label environment-blocked tests instead of stopping other development. **Do not package each slice.** Keep implementing/testing/committing; defer unified package and operator physical tests until the final milestone. Do not mistake skipped tests for passing evidence.

Historical packaged checkpoint: P0/P1 code through `0977e787b7c5d9816184a9aefbd369a9b885ef72`; the 0.4.2 ZIP was verified before clipboard development and does not contain this new feature. Actual capture remains denied (0x80070005); collector rejects Server/RDP for Home acceptance. Current source adds optional v2 clipboard capability/message types with independent grants; file capability is independently negotiated; bounded v2 reconnect is implemented. [Physical test/result template](PHYSICAL-ACCEPTANCE.md).

Input checkpoint: [negotiated rich input and Viewer committed-text UI](INPUT-PROTOCOL.md) are implemented; native and bitmap sources preserve key metadata and precision pointers, input cannot queue before approval, and local IME drafts never replay on reconnect. Advanced clipboard [content/codec/Windows adapter foundation](CLIPBOARD-CONTENT.md) now has 898 Core/TLS and 29 in-box WIC checks; **next: bind the dedicated clipboard channel and integrate exact image/HTML consent/stop handling with actual Host/Viewer providers**, then file-list copy intents, monitor/DPI selection, Windows session/service integration and audio. Use `build/clipboard-artifacts` with `build/native-clipboard` for the current isolated build; old office outputs may be stale. Scoped in-memory file task/byte resume is implemented; [evidence](../benchmarks/2026-11-23-file-resume-validation.md). Physical Home/LAN, actual application input and disk/driver soak remain pending. Foreground keyboard and physical GPU/capture tests are explicitly skipped in this runner; production security gates stay strict. Do not count protocol-only/mock tests as physical acceptance.

Physical exit: run existing `scripts/accept-home.ps1` or bundled `Collect-Logs.cmd` on an unlocked Home local console. Use two PCs and explicit approval; verify visible image, held Ctrl/drag disconnect, Microsoft Pinyin and Japanese Microsoft IME in actual remote applications, DPI, monitor unplug and real GPU reset. Record edition/build/driver/session/network/package identity. API Present success is not visible correctness or input-to-photon proof.
