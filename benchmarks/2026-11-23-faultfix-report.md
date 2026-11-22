# Four-frame producer termination and v0.3.1 recovery

The operator trace shows DXGI + NVIDIA hardware H264 actually encoded and sent 4 frames before termination. It establishes capture/encoding startup, not the cause of the later failure. accepted_setting_mask 13 means the no-B-frame property was not accepted; it does **not** prove B-frames or explain this crash.

## Confirmed code defect

Producer finally called queue.Complete() without the original failure. That woke sender with ChannelClosedException; Task.WhenAny could select it before producer cleanup completed. The primary capture/encode/metadata exception was then hidden. It is a deterministic diagnostic/lifecycle defect; NVIDIA-specific root cause is still unknown.

Producer now records stage/type/HRESULT/frame count before publishing queue failure or cancelling siblings. FirstSessionFailure atomically preserves the primary cause; sender unpacks channel-wrapped cause. Cleanup attempts both encoder and capture release and cannot replace an already recorded original cause. Supervisor revokes access, cancels/closes and joins all workers before rethrowing the primary exception. Logs distinguish capture-init/frame, encoder-init/submit, metadata, queue publication and disposal. Structured Host errors are saved under LocalAppData/RelaxDesk/logs/host-YYYYMMDD.log, excluding startup credentials or input contents. Interactive Host launch waits for Enter after session ends.

## Bounded recovery

A non-device-loss ExternalException during hardware encoder submission permits one explicitly logged software H264 switch for that session. Device loss keeps its bounded capture-recreation path. DXGI capture, TLS/control/media binding and consent remain intact; no GDI or runtime Deflate downgrade is used. Software initialization failure is fatal and reported, never retried indefinitely. The following frame requests IDR; a HardwareEncoded change resets the decoder at a keyframe so the new SPS/PPS stream is accepted. CPU readback/encode costs are expected during software fallback; no speed improvement is claimed.

## Executed validation

- Solution Release 0 warnings/0 errors; all four self-contained publishes succeed.
- 253 Core/TLS assertions, original pin/token/consent/media-ticket refusals and IP-approval denial pass.
- Injected fatal producer failure after exactly 4 frames: primary encode-submit/InvalidDataException/HRESULT survives, not generic channel closure. [Original test](2026-11-22-video-producer-failure.txt), [fresh final package](2026-11-23-faultfix-primary.txt).
- Hardware failure injected after 4 frames: one software H264 fallback, decoder generation >= 2 and 60 decoded frames with concurrent authenticated control. [Headless recovery](2026-11-22-video-runtime-fallback.txt).
- Real WPF CPU display in IP/port approval mode continued 60 submitted frames and all native resources returned to 0. [Initial WPF test](2026-11-22-wpf-encoder-fallback.txt), [final package](2026-11-23-faultfix-recovery.txt). Final run received 109/submitted 60/dropped 49, so this is a functional recovery test, **not** a performance pass or improvement claim.
- Normal hardware video still passes; unread media still closes within a bounded ~6.2s while control pings remain responsive. Timeout/security/queue bounds were not relaxed.

Tests use the available **Intel GPU with synthetic frames and deliberate failure injection**, not NVIDIA or a physical DXGI desktop on the operator's PC. HDR, physical GPU Present, input-to-photon, multi-minute LAN stability and actual NVIDIA recovery remain unverified. Software-MFT initialization-failure guard is compiled/inspected; the injected runtime-failure tests exercise successful software initialization, not missing-MFT coverage.

## Delivery

Final ZIP: `build/delivery-fault-r2/RelaxDesk-0.3.1-Fault-Fix-win-x64.zip`.
Version **0.3.1-faultfix.20261123-000111.g7220a30**, source commit **7220a304d69c86c7be6082f72da0ef3a1d970e12**.
SHA256:

```
FA2BC89BC6844D9D688AA723ED354A6A7CD298426478421B63AB3933687496DA
```

Fresh ZIP extraction, no dotnet/dev tools in PATH: 1041 hashes, Host/Viewer version equality, local runtime loading, security and UI contracts pass. Collector correctly returns NOT_ACCEPTED_ENVIRONMENT on Server/RDP. [Final package run](2026-11-23-faultfix-package.txt). Manifest dirty=true reflects an earlier untracked benchmark log at packaging time; compiled source changes were committed. Earlier candidate artifacts are not the final download.

Replace **both Host and Viewer** with this ZIP in a new directory. If it still disconnects, return new producer_error or recoverable_encoder_error plus session_end, or Host's credential-free local log; also include Viewer's local log. No need to send fingerprint/token/invitation. These lines will identify the currently missing original NVIDIA operation/HRESULT rather than assuming ChannelClosedException is the primary failure.
