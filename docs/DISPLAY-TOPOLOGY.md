# Live display topology recovery

Viewer can keep the approved session while a known display changes resolution, nominal DPI, rotation or position, disappears, or returns. A missing selected screen pauses its image and input; Viewer can explicitly choose a surviving authorized screen or wait for the original target. It never silently redirects to an unrelated primary screen. No display settings, desktop permissions, Windows policies or RDP components are changed.

## Scope and identity

The initial Host scope remains authoritative. Windows `QueryDisplayConfig` / `DisplayConfigGetDeviceInfo` map a GDI display name to its monitor device-instance path. Only an uppercase SHA-256 digest of that path enters display metadata; raw paths, friendly identifiers and EDID serials are not sent. Names such as `DISPLAY1` can be reused and do not establish identity.

The digest identifies the Windows device instance, not cryptographic hardware identity. Port/driver changes can change it, and mirror/ambiguous sources may not provide a unique mapping. If the initial authorized devices cannot be uniquely identified, Host grants fixed-topology display version 1 instead. Changed identity then requires fresh session consent. Do not interpret the digest as hardware attestation.

An initial single-screen grant remains that device. An initial all-screen grant contains only the devices present and authorized at approval. New identities are excluded. When an unapproved screen is present, the GDI all-screen union is removed from Viewer choices: its bounding rectangle could otherwise reveal pixels outside the original set. An active all-screen view pauses in that case and the user can choose an authorized single screen. The all-screen choice returns when the current aggregate is wholly within the original grant and frame bounds. Original known devices may rejoin the same session scope when they return; a reused GDI name with a different digest cannot.

This limits captured screen pixels and pointer coordinates, not applications or Windows privileges. Keyboard control still acts on the approved shared physical desktop; this is not an isolated RDP session or an application sandbox.

## Negotiation and ordering

New Viewer requests `DisplayVersion: 2`. Host may acknowledge version 2, version 1, or no display extension; grants cannot exceed the request. Initial approval still requires a valid available selection at generation 1. The advanced option **保持屏幕配置变化时的连接** can be disabled to request version 1 from an older Host that rejects new versions. There is no automatic retry with weaker identity/pinning or implicit consent downgrade.

`display-topology-v2` carries validated current metadata, allowed IDs, selected ID, generation and an `Unavailable` flag. An unavailable selection is empty; allowed choices may remain, or be empty if no authorized source is usable. Empty metadata may also represent a query that temporarily cannot be trusted, not proof of physical removal. Updates are consecutive generations and are checked against the immutable original identity set. A concurrent Viewer selection can be superseded by a topology update; stale-generation requests are ignored while their shared replay sequence is still consumed. Current out-of-scope or malformed requests fail closed.

Host suspends input and releases held state under the same generation lock before replacing geometry. Its capture/encode owner thread disposes old resources, re-enumerates, applies the refreshed selection and sends the new state. Version 2 then waits for `display-configured-v2` from Viewer before producing media for that generation. This avoids losing a static desktop's only keyframe when control and media sockets deliver in different orders. The initial approval is already known before media attachment, so generation 1 needs no extra configuration ACK.

Viewer clears unavailable CPU pixels, hides the native surface, closes committed-text input, releases local held state and shows a paused-screen message. Its display menu can select surviving choices even without a current frame. Available topology or explicit reselection restores the surface, resets local pan, acknowledges configuration and waits for a matching decoded/presented frame. Only then does `display-ready-v2` reopen Host input. Old/unready input and old-generation queued/retained frames never use the new coordinates. Decoder generation resets even at unchanged dimensions. Viewport zoom remains local and independent.

During an unavailable state, the expected-frame timer is stopped; quiet video does not imply broken control. Existing authenticated heartbeats continue. Topology pause itself does not revoke independent file/clipboard grants. When a target becomes available, the existing 15-second frame deadline resumes. Explicit stop, transport/security failure or exhausted recovery still ends the session normally.

## Recovery bounds

Physical geometry and identity are checked before capture/input using supported read-only Windows APIs. These snapshots are not atomic with concurrent OS changes. No global hooks or hidden service are introduced.

At most 12 topology transitions may commit within 10 seconds. A transient monitor-metadata query failure pauses output; persistent enumeration failure beyond a three-second budget ends the attempt rather than inventing usable coordinates. A legitimately unavailable display can remain paused while metadata is readable and control stays alive. Missing identities also keep capture unavailable until the original scope is reliably known.

DXGI output-not-found/unsupported during initialization may trigger re-enumeration under the existing three-attempt, non-replenishing pipeline recovery budget, with 250 ms waits. Device loss keeps its existing bounded recovery. **Access denied is excluded**: there is no retry on another desktop or permission bypass. Original/terminal errors remain diagnostic evidence.

## Validation and physical acceptance

[Recorded validation](../benchmarks/2026-11-24-topology-validation.md) includes real Windows identity queries, pure authorization/budget tests, actual Host/TLS/H264 and actual WPF lifecycle with synthetic topology changes. The test backend is loopback-only and does not change real monitor configuration.

Final two-PC Home acceptance must unplug/reconnect the selected monitor, change resolution/rotation/DPI, disconnect all monitors where possible, attach an unapproved monitor, and verify both visible image and actual pointer/key release. Confirm that all-screen capture never includes the new device, files/heartbeats survive a readable unavailable state, explicit reselection removes the pause overlay, and known-device return retains the intended target. Record edition, driver, device-instance digests, mode changes, media generations and failure logs. Secure desktop, physical GPU visibility, driver races and long soak remain unverified here. No package is produced for this slice.

Sources: [QueryDisplayConfig](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig), [buffer-size races](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getdisplayconfigbuffersizes), [monitor device-instance path](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_target_device_name).
