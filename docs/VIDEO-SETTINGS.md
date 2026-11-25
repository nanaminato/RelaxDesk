# Session video settings

The authorized Viewer toolbar menu now has Video Settings. Presets select a capture/encode frame-rate limit of 15/30/60 FPS and an H.264 target bitrate of 2/8/20 Mbit/s. The protocol bounds are 5–60 FPS and 1–40 Mbit/s. Defaults remain 30 FPS and 8 Mbit/s. Targets do not guarantee delivered FPS, NIC rate, visual quality or office responsiveness; compare the measured live statistics and final physical workloads. No adaptive/hybrid transport or automatic codec preference change is introduced.

Changes retain the current desktop/display grant and pixel dimensions. They never authorize another monitor, clipboard, files, audio or input mode. Deflate compatibility still supports the FPS limit; its bitrate controls are disabled with an explicit explanation. Reconnect renegotiates a fresh session and Host defaults; queued/in-flight old settings are not replayed or persisted in history.

## Negotiation and validation

Optional `VideoVersion: 1` is requested on v2 hello/request with no supplied initial state. Host approves `VideoVersion: 1` and a validated `VideoSettings` record (`FramesPerSecond`, `Bitrate`, `Generation`), initially generation 1. Zero/omitted version requires absent settings and retains old framing. Unsupported, unsolicited, malformed or non-initial approval generations reject the session. This is an extension of the existing approved video stream, not an extra privacy grant or unauthenticated control endpoint.

An exact `video-settings-v2` control command carries the shared increasing control sequence, version 1 and desired fields tagged with the current generation. AccessGate requires both approved session and negotiated version; extra metadata, replay and invalid ranges fail. Host permits one pending request, rejects stale/unchanged generations, and limits requests to at most one per second. Generation cannot exceed 1,000,000; further adjustment requires a new session. The UI waits one second after acknowledgement and refuses input while pending, rather than flooding the encoder.

`video-settings-state-v2` acknowledges the resulting new generation/targets only after the capture/encode worker has applied them and produced a keyframe. Viewer matches the pending target exactly and waits for a frame from that generation to be submitted before allowing another request. Acknowledgement may arrive before or after that frame. Unknown future generations/replayed acknowledgements reject protocol state; older queued frames cannot overwrite a newer presented setting generation.

## Media boundary and worker ownership

With negotiated version 1, binary media header word at byte 28 (previously reserved) carries a positive bounded **video generation**. The 72-byte v2 header, display generation at byte 64, frame sequence, timestamps and existing flags remain independent. Without negotiation byte 28 stays zero and old readers keep rejecting nonzero values. Parsers validate both extensions before allocating payloads.

All encoder replacement occurs on its existing owning capture/encode thread, outside the control-reader/Dispatcher/input lock. The old encoder is disposed; selected FPS/bitrate are passed to hardware and existing software fallback constructors. Only a matching already-applied pending request may commit: a request arriving midway through encoding an old frame is deferred to the next iteration. Up to 60 encode submissions are permitted to obtain the new keyframe. Existing initialization/runtime fallback and causal error reporting remain; failure does not fabricate a successful setting. A mandatory video-pipeline failure can end the session with its original cause.

MediaDecoder checks video generation separately from display/size/codec/hardware changes and requires a keyframe before resetting that generation. Same-size bitrate/FPS changes therefore recreate the native decoder and, where applicable, its GPU renderer resources. Existing bounded queues/keyframe recovery handle a dropped transition frame. No old reference frames or queued configuration commands are restored across sessions.

Capture attempts are paced on the dedicated producer using monotonic time and rounded-up remaining millisecond waits, including iterations with no encoder output; cancellation wakes the wait. The limit is not a promise that capture/device/codec can attain the selected rate. Static desktops remain idle unless a settings change, keyframe request or queue recovery requires a refresh.

## Static desktop refresh

`CaptureFrameRefresh` first asks for a fresh image and uses an explicitly supported owned cached image only on a no-update result when refresh is requested. Capture/access/device errors propagate and never fall back to cache. DXGI's native capture context already keeps an owned normalized texture on `WAIT_TIMEOUT`; managed `CachedFrame` revalidates apartment/session access and its last successful metadata. Errors/failed metadata extraction/disposal invalidate it. Encoder refresh reuses that owned GPU image without adding CPU readback; it does not widen the original display scope. GDI normally produces a fresh image and other backends default to no cache support.

Synthetic static tests intentionally produce only one desktop image until refresh is requested. They verify settings complete without unrelated scene changes; they do not establish actual Windows Home/DXGI cached-frame, lock/UAC, HDR or driver behavior. Those remain in final physical validation.

## Evidence and remaining acceptance

[Evidence](../benchmarks/2026-11-24-video-settings-validation.md) includes Core/range/replay/state/pacing/header tests, TLS negotiation, production Host with actual H.264/Deflate decode, real WPF menu/confirmation/cooldown, synthetic audio continuity, static-source refresh and zero native resources. The menu was rendered and inspected. Host logs record generation, target FPS/bitrate, codec and native accepted-setting mask, never content/credentials. Accepted settings/targets are not measured quality or data rate.

Final physical checks: change limits/bitrate on moving text/video and a completely static Home desktop without moving the mouse; confirm no black/old-reference frame, scope change, stuck input or audio interruption. Check Intel/NVIDIA and available AMD, HDR/SDR, rotated/all-screen compatibility, CPU fallback, network congestion, lock/UAC and cancellation during an in-flight change. Record actual measured FPS/rate/stages and visible quality; no MSTSC/physical performance result is claimed. No package was generated.
