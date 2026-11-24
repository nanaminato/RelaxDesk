# Session access and transport development

## Current session behavior

| Mode | Host decision | Input | Optional features |
| --- | --- | --- | --- |
| Guest | Fresh local consent | Off by default. Host can grant it separately and revoke it during the session. | Clipboard, scoped files and audio use the existing independent local grants. |
| Work | Fresh local consent | Ordinary-desktop input | Existing independent grants and shared display scope |
| Admin | Initial HostShell approval, independent confirmation, local UAC and worker readiness | High-integrity input on the approved Default desktop | Existing independent grants |

HostShell can permit all modes, Guest and Work, Guest only, or Work only. The CLI
`--no-admin` rejects initial Admin requests and omits upgrade negotiation in
Work sessions. The Guest-only CLI option is
`RelaxDesk.Host --listen --guest-only <specific LAN IP> <port> dxgi`.
Use `--work-only` in the same position to accept Work and legacy Work
connections and disable Admin upgrades.
The CLI approves a Guest with ordinary input via `APPROVE_INPUT`; optional
grants retain their existing commands, with `_INPUT` appended for Guest input.
Work uses fresh consent on reconnect. The Viewer can opt in to a persistent
per-Windows-user signing key. The Host verifies possession and shows the
public key fingerprint to the local operator. The Host can remember the exact optional grants for a verified key and revoke
the record from the Work device list.

The v2 hello can carry `Mode`. The authenticated approval echoes the requested
mode and provides `InputAllowed`. An explicit request requires a matching
approval before any Viewer channel attaches. The default Work flow omits both
fields for backward compatibility. An earlier Host cannot silently downgrade a
Guest request into a Work session. The Host rejects unsupported Admin requests, unknown modes and
approval-only fields in a hello. The approved input protocol version remains
independent of the input grant so display readiness and switching still work in
view-only sessions.

Host input authorization takes place before injection under the session gate.
Revocation serializes with an in-flight injection, releases the tracked inputs,
and notifies the Viewer over the authenticated control channel. Subsequent
structurally valid inputs from the previously authorized Guest are discarded
with sequence validation while media and control remain connected. Input from
a Guest that never received a grant is a protocol violation. Re-enabling
Guest input requires a fresh approval.

The coordinator refuses real sharing if its effective process token is
elevated, above medium integrity, or has UIAccess, or if it is impersonating
when consent completes. The guard also applies to legacy connections. A
per-input desktop check remains in the Windows input path. Windows can reject
input directed at a higher-integrity window via UIPI.

On the v2 ordinary-input route, a partial or rejected Windows input batch
is discarded. The Host retries releasing any accepted held input before
injecting the next action. It continues video and reliable control while the
ordinary desktop remains eligible. The elevated worker retains its strict
failure and revocation path.
[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
[Token information](https://learn.microsoft.com/windows/win32/api/securitybaseapi/nf-securitybaseapi-gettokeninformation)

## Remaining Windows milestones

1. The Elevated Interactive Agent for the Default desktop is implemented.
   Its protected deployment, session-bound local endpoint, separate Admin
   approval, revocation and worker-loss behavior have automated coverage.
   A repaired installation and a physical UAC consent run are required to
   verify the actual elevated worker and its window input.
2. Validate standard and high-integrity windows on Windows Home. Check
   environment variables, elevated CMD and PowerShell, Registry Editor and
   Computer Management. Exercise revoke/exit while keys or buttons are held,
   agent crash, and reconnect without an inherited Admin grant.
3. Validate UAC secure desktop, lock, sign-in and WTS session switching
   independently. A local UAC transition pauses capture and input, and the
   same session can resume on its original Default desktop. Lock and console
   session changes end the grant. The LocalService broker tracks authenticated
   leases and health in Session 0. Windows documents LocalSystem desktop image
   access for secure-desktop DXGI duplication and isolates services from user
   desktops. Windows also limits ordinary synthetic input by integrity level.
   Direct secure-desktop capture and input, remote login and unattended Admin
   authorization are outside the implemented path.
   The Remote Assistance UIAccess policy moves eligible prompts onto the
   interactive desktop. RelaxDesk keeps the configured secure-desktop policy.
   WTSQueryUserToken provides a logged-on user's token to a highly trusted
   service with SeTcbPrivilege. The current LocalService broker manages session
   leases and does not acquire user tokens.
   [DXGI DuplicateOutput](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgioutput1-duplicateoutput)
   [Interactive Services](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)
   [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
   [UIAccess elevation policy](https://learn.microsoft.com/en-us/windows/security/threat-protection/security-policy-settings/user-account-control-allow-uiaccess-applications-to-prompt-for-elevation-without-using-the-secure-desktop)
   [WTSQueryUserToken](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken)
4. Work device grants use authenticated keys and a revocable Host list.
   While sharing from an eligible desktop, the operator can separately
   preapprove the next Work connection for one selected device within 15 minutes.
   The grant is held in memory and permits viewing and ordinary keyboard/mouse
   input. The operator can cancel it, and it ends on use, sharing stop, device
   revocation or observed desktop loss. Boot-time and pre-sign-in access
   remain unimplemented.

## Transport milestones

The TCP/TLS video frame channel now sits behind the interface used by the
producer and renderer-facing receiver. The current media binding and framing,
control channel, audio, clipboard and file paths retain their TCP/TLS behavior.
Viewer statistics report the actual video channel.

The candidate for unreliable media is MsQuic DATAGRAM. It provides authenticated
encryption, path handling and congestion control. The default Windows Schannel build requires TLS 1.3,
which ships in Windows 11 and Windows Server 2022. Windows 10 needs an
OpenSSL-backed MsQuic deployment or the TCP/TLS path. The .NET QUIC API documents
reliable streams; a native DATAGRAM integration needs Windows deployment and ABI
validation. DTLS protects datagrams but would leave application-level pacing
and congestion control to RelaxDesk. A custom encrypted UDP wire format would
also require independent review of key establishment, replay protection and
endpoint binding. Prefer the native MsQuic candidate after deployment checks.
[.NET QUIC API](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview)
[MsQuic API](https://github.com/microsoft/msquic/blob/main/docs/API.md)
[MsQuic platforms](https://github.com/microsoft/msquic/blob/main/docs/Platforms.md)
[QUIC DATAGRAM](https://www.rfc-editor.org/rfc/rfc9221.html)
[DTLS 1.3](https://www.rfc-editor.org/rfc/rfc9147.html)

The release candidate's runtime contains Schannel MsQuic 2.5.12. In an isolated
Windows 11 comparison, the self-contained datagram probe passed with that
library. Microsoft and NuGet signatures validated for the OpenSSL 2.5.13
package. Its x64 DLL returned `0x80070057` while opening the Host listener
with the current certificate path. The explicit UDP Viewer also reported
that the Host did not offer UDP. The result rules out a DLL-only replacement.
In-memory PKCS#12 is a documented OpenSSL credential option. Qualification
requires a successful pinned handshake, wrong-certificate rejection, and
real Windows 10 coverage before redistribution.
[Microsoft OpenSSL package](https://www.nuget.org/packages/Microsoft.Native.Quic.MsQuic.OpenSSL/2.5.13)
[MsQuic certificate API](https://github.com/microsoft/msquic/blob/v2.5.13/docs/api/QUIC_CREDENTIAL_CONFIG.md)

Use the existing authenticated reliable control path to negotiate and bind
each media attachment. Establish a path and measure it before switching.
Give each video transport epoch bounded packet and frame sequence tracking,
MTU-aware fragmentation, expiry and reassembly limits. Require a decoder
keyframe on discontinuity. Keep reliable input and optional feature traffic on TLS.
Exercise initial refusal and mid-session media recovery under delay, loss,
reordering, jitter and a shaped link with concurrent files. Auto uses a
15-second minimum cooldown between recovery attempts. Use the same workloads to compare static
traffic, queue age, text fidelity and interaction latency.

QUIC DATAGRAM traffic uses QUIC congestion control and carries no automatic
retransmission. A selective retry would need the feedback delay, sender queue
delay, and return trip to fit within the frame's remaining 150 ms reassembly
window. FEC could repair limited loss without waiting for feedback, at the
cost of parity traffic and decoding work. Neither repairs a completely
unavailable UDP path. The current application recovers through a fresh
keyframe, adjusts the encoder target under measured loss, and uses a new
TCP keyframe when Auto falls back. [RFC 9221](https://www.rfc-editor.org/rfc/rfc9221.html#section-5)

Loopback shaping exercised a 512 KiB/s budget, 8/20 ms delay pulses and
packet reordering while video advanced. A separate loss case reduced the
encoder target from 8 to 6 Mbit/s and recovered it to 6.8 Mbit/s. An
incomplete-frame case delivered a TCP keyframe after 3.06 s with the
original TLS control connection. These cases verify adaptation and recovery.
They do not establish whether parity or a selective retry would improve a
matched dual-PC workload. Keep both disabled pending a comparison of total
traffic, discarded assemblies, time to first decodable frame, and physical
input-to-photon latency across the same isolated and burst-loss traces.

## Verification

`pwsh -NoProfile -File scripts/verify.ps1 -Graphics -Wpf -Quic -LargeFiles -SkipPhysicalGraphics -SkipInteractiveInput`
builds and tests the non-physical source path. The session access smoke uses
real Host TLS connections, synthetic frames and non-injecting input callbacks.
Run `RelaxDesk.Host --input-token-diagnose` from ordinary and elevated
terminals to check the actual token without injecting input. Expected exit
codes are 0 and 2, respectively. On a physical Windows Home console, pair two devices and exercise Guest view,
Guest input grant and revoke, Work input and optional grants, then repeat with
an elevated Host and confirm the coordinator refuses sharing. Record UAC,
lock, sign-in, WTS switching and real window behavior as separate results.

| Scope | Verified on the current development branch | Remaining evidence |
| --- | --- | --- |
| Guest and Work | Production TLS and WPF tests verified fresh approvals, independent capability grants, revocation, device trust and denial of unapproved Admin. | Real keyboard and mouse on ordinary and elevated windows, including a Host started with an elevated token. |
| Elevated input | WPF/TLS tests exercised two Admin transitions and revocation through protected, non-injecting test pipes. Real Windows IPC and ordinary-token rejection passed. | Update the existing service installation with local consent, then use the actual elevated worker against high-integrity windows. |
| Desktop transitions | The Host sent separate terminal reasons for injected lock, session switch and input-desktop loss. WPF displayed the matching messages and stopped reconnecting. A simulated local UAC pause preserved TLS and resumed video. | Local consent, cancel and recovery on the UAC desktop, physical lock/unlock, sign-in and two-user switching. Direct secure-desktop control, remote sign-in and unattended Admin remain unimplemented. |
| Media | Production QUIC loopback verified repeated TCP/UDP recovery and fallback. A real DXGI, H.264 and D3D11 console loopback switched from TCP to UDP without full-frame readback. | Two-machine LAN and physical input-to-display latency measurements. |
| File transfer | Scoped TLS transfer, SHA-256 verification and interrupted transfers over 1 GiB passed. A separate 8 MiB transfer retained the same file connection while Auto switched video from TCP to UDP. | Two-machine LAN measurements and physical Explorer drag-and-drop. |
| Distribution | Source Release builds and package-verifier syntax passed. The package verifier requires packaged WPF Auto recovery, desktop termination and Admin lifecycle evidence. | Generate and verify the final independent Host and Viewer installers after the remaining acceptance work. |

`RelaxDesk.Host --admin-input-diagnose` checks the ordinary coordinator token,
interactive desktop and current user's protected worker installation. It exits
0 once those prerequisites pass or 2 if one is unavailable. It never launches
the worker or triggers UAC.

Run `RelaxDesk.Host --session-diagnose-watch 90` in a separate ordinary Host
console during local transition tests. It writes a timestamped JSON snapshot
when Windows session, lock, or desktop state changes, including the native
error if the input desktop cannot be opened. It does not inspect desktop
content or attempt to capture, move, or inject input on another desktop.

| Interactive case | Acceptance evidence to collect |
| --- | --- |
| Ordinary and elevated windows | Use the same ordinary editor and an elevated command prompt for Guest, Work and approved Admin. Record which windows receive input, then revoke Admin while a key is held and verify the release and return to Work. |
| UAC consent | From Work request Admin. On the Host separately deny the app request, cancel Windows consent, and approve Windows consent. Record video pause and recovery, control heartbeat continuity, and whether the worker activates only after approval. |
| Lock and unlock | Lock the Host at the console during Work and Admin. Record whether media and input stop, whether the grant ends, and whether reconnect after unlock requires fresh approval. |
| Windows sign-in | Start the installed broker before sign-in. Record broker status, absence of a usable interactive grant, then whether signing in enables explicit approval on the new desktop. |
| Windows session switch | Switch between two signed-in users during streaming. Record the original session ending, worker cleanup and fresh approval for any new session. |

A local-console `desktop-gpu` smoke on an RTX 5060 Ti at 2560 × 1440
captured real DXGI frames, used the NVIDIA H.264 encoder and presented three
D3D11 Viewer frames with zero full-frame readback bytes. The short run recorded
capture P50 0.46 ms, encode P50 3.25 ms, and zero live native resources after
shutdown. This loopback result covers the ordinary desktop pipeline. The
interactive cases above require separate evidence.

### Local synthetic measurements

The TLS impairment fixture capped video forwarding at 131,072 bytes/s and
received 37 control replies during the 4.01 s observation window. Peak control
RTT in that window was 1.63 ms. Exact synthetic pixels decoded again once the
shaping was removed. In the unread-media fixture, the Host assigned the
failure to the media sender, closed the session in 2.17 s and reported zero
live native resources. The concurrent scoped file test delivered 207 synthetic
video frames and sampled 268 control replies, with P95 RTT of 0.88 ms.

These are single-run local fixtures. The test was changed from low-entropy
H.264 to Deflate to fill the TCP socket buffers in the unread-media case.
Office workloads, physical latency, weak Wi-Fi and a matched MSTSC comparison
remain separate measurement tasks.

### Administrator worker deployment

New Host package builds stage the independent worker and its runtime under
`ElevatedInput/`. Machine setup verifies the worker manifest and copies the
payload into the protected release as part of its existing transaction. The
registered interactive owner receives read and execute access to the worker
and deployment metadata. Write access remains limited to trusted principals.
The runtime resolver checks the current release, registered owner, ACLs,
reparse points, file inventory and hashes. The worker verifies its executable
path against that resolver before connecting to the coordinator. Previous
packages still support broker setup without administrator input.

Automated checks exercise protected file creation, owner mismatch and rejection
of extra runtime files in a fixture with real ACLs and simulated SCM. Actual
machine installation, UAC launch and administrator-window input are separate
verification steps.

The coordinator launch entry resolves the installed worker, creates the grant
pipe, and invokes the Windows shell `runas` verb after local approval. The
worker disables managed startup hooks. Cancellation closes the rendezvous so
a worker that starts after revocation cannot acquire the grant. A mock shell
launch verifies argument binding and cleanup after local UAC cancellation.
The initial Admin flow completes local UAC before attaching media and starting
the active-session desktop watcher. It uses the monitor scope selected on the
Host. Disconnect or worker failure ends the grant. During a live Admin session,
the Host or Viewer can revoke elevated input and return to Work. The Host
drops input while the worker releases held keys and buttons and acknowledges
shutdown, then resumes ordinary input after the Viewer confirms the change.
The existing media and control attachments are retained during a successful
transition. Automatic reconnect after Admin requests Work with fresh consent.
The live Work-to-Admin implementation uses the bounded desktop pause described
below. Its real UAC and elevated-window acceptance remains pending.
[Windows shell elevation](https://learn.microsoft.com/en-us/windows/win32/shell/launch)

The datagram video frame channel now fragments existing media records into
transport-sized messages and reassembles at most four frames at a time.
Current Viewers offer the optional `PointerBrotli` request flag when negotiating media. A Host can
then send independent lossless Brotli pointer shapes when they shrink the
metadata. Each frame carries its own complete shape and the decoder bounds
the decompressed length. Viewers that omit the offer retain the original
metadata layout.
It expires incomplete frames after 150 ms and can deliver a newer complete
frame across a gap. The authenticated datagram transport supplies the
effective payload budget between 512 and 1,200 bytes. The fixture exercises
real UDP loopback plus deterministic loss, duplication, malformed fragments
and reordering. Production now attaches encrypted UDP before media starts and uses TCP/TLS if Auto cannot attach it.

The Windows MsQuic endpoints use the DLL bundled with the running .NET
runtime. A real encrypted loopback carries an 8,200-byte video frame through
the fragmenter and receiver at the negotiated data budget, exercises the
reverse path on the same connection and rejects a different certificate.
Native send and receive buffers are bounded. Production session negotiation
uses the authenticated TCP/TLS control channel. To reproduce, build the native DLL and invoke
`RelaxDesk.Smoke --quic-datagram-probe <absolute-path-to-RelaxDesk.Native.dll>`.
[MsQuic certificate callback](https://github.com/microsoft/msquic/blob/main/docs/api/QUIC_CONNECTION_EVENT.md)

The QUIC attachment protocol accepts a fresh, single-use media ticket on a
reliable stream belonging to the QUIC connection that will carry video
datagrams. It validates the request before consuming the ticket and selects
the lower of the configured receive budget and the transport send budget.
Tests cover a foreign ticket, replay, expiration and malformed requests.
An encrypted, certificate-pinned QUIC loopback verifies the attachment
exchange. Production uses an equivalent one-use binding exchange on the already
certificate-pinned native DATAGRAM connection with selection confirmed by
TCP/TLS. This allows the same connection to prove bidirectional datagram
reachability before carrying media.


The Viewer defaults to Auto. Explicit TCP uses the legacy video attachment
without offering QUIC. Explicit UDP requires a successful QUIC attachment.
Auto sends a bounded UDP probe and uses the existing TCP/TLS media
attachment if it fails. Audio, files, clipboard and input retain their
existing channels. The statistics panel labels the active media transport
and fallback reason. An established Auto session keeps a separately pinned
and ticket-bound TCP video stream in standby. If UDP receive fails, the
Viewer requests fallback on that stream. The Host can also switch on a
UDP send failure or six consecutive sample windows of at least 25% estimated
loss, with at least 16 packets each. A window can combine up to four one-second
samples so lower-motion sessions can recover bitrate when traffic is healthy.
Completely idle windows do not increase bitrate. The encoder can reduce its target before
the switch. The next TCP H.264 video frame is an independently
decodable key frame. When both peers negotiate UDP recovery, the Host offers
a new pinned QUIC path through the original TLS control connection after at
least 15 seconds on TCP. Each attempt uses a fresh, expiring one-use ticket.
TCP keeps carrying video during probing. The Host switches at an independently
decodable frame, and the Viewer accepts the new UDP path after it receives such
a frame newer than its last TCP frame. Failed attempts and later fallbacks
restart the cooldown. Peers without this capability stay on TCP after fallback.

Run the supported-platform production loopback with
`RelaxDesk.Smoke <Host-path> --quic-production`. The fixture verifies the
normal Host/SessionClient negotiation, a delivered synthetic frame and
the independent control heartbeat. It injects an initial UDP failure to
exercise Auto fallback and strict UDP refusal. An active-path receive failure
fixture measures time to the next TCP key frame and verifies a subsequent
heartbeat on the original control socket. The same fixture checks repeated
TCP to UDP recovery, fresh tickets and sender statistics, and another TCP
fallback on failure of the recovered path.

The UDP video sender emits a small authenticated heartbeat every 750 ms
while its frame queue is idle. The Viewer allows three seconds without an
authenticated heartbeat or complete frame before requesting the
existing TCP fallback. The synthetic static screen stayed on UDP for four
seconds without a new frame. Suppressing video DATAGRAM delivery while QUIC
and TLS remained connected recovered a decodable TCP key frame in 2,849 ms
on one loopback run. This includes media silence detection. It does not
measure physical-display latency or an actual WAN outage.

The statistics panel samples native QUIC smoothed RTT and its variation,
native receive packet and reorder counts, and local receive-queue drops.
The frame gap percentage counts holes between delivered sequence numbers
and therefore includes Host-side frame skips. It is not a packet loss
measurement. Current UDP statistics disappear after the session falls back
to TCP. Local production tests validate live native statistics and the
counter transition, and a WPF fixture checks the labels.
[MsQuic statistics schema](https://github.com/microsoft/msquic/blob/main/src/inc/msquic.h)

The Host adds its current send packet totals and loss estimate to TLS
heartbeat replies during UDP video. The estimate subtracts subsequently
acknowledged packets from those initially marked lost by MsQuic. Viewer
labels this as a cumulative Host QUIC packet loss estimate, separate from
frame gaps. An encrypted loopback relay discarded 56 host-to-viewer UDP
datagrams in one run. The Host reported 55 estimated lost QUIC packets
among 293 sent at the sampling instant. QUIC packets and UDP datagrams are
different units, and loss detection can lag the relay count.

UDP H.264 sessions sample the Host's QUIC sender counters once a second.
Two consecutive windows with at least 16 packets and 5% estimated loss reduce
the encoder target by 25%. Six sufficiently sampled windows at or below 1%
loss raise the target toward the configured ceiling. The target stays at or
above 1 Mbit/s. TCP fallback removes the UDP cap. The encoder applies changes
on its owning thread through the Media Foundation dynamic mean bit rate
property and logs whether the encoder accepted the change.

With synthetic video and encrypted UDP packet drops, the in-box software H.264
encoder accepted a target change from 8 to 6 Mbit/s during the same session.
That result is a confirmed encoder setting. Achieved throughput, hardware
encoder behavior and real weak-network quality still require measurement.
[Media Foundation H.264 encoder](https://learn.microsoft.com/en-us/windows/win32/medfound/h-264-video-encoder)

UDP video serialization now fills datagram buffers directly from the shared
binary media header, metadata and encoded payload. A deterministic test
compares the concatenated fragment bodies with the TCP media bytes across
metadata and payload boundaries. For a 4 MiB payload, the previous staging
serialization allocated 4,194,568 managed bytes and the current fragment
writer allocated 1,320 bytes, measured on the sending thread with a
synchronously completed test sink. Native MsQuic send copies and physical
network performance are outside this measurement.


### Work device identity

The Viewer offers a P-256 public key only when the Work identity checkbox is
selected. Windows CNG retains its signing key with private-key export disabled.
The Host challenges the key before displaying the local consent request.
The signature binds a fresh challenge to the SHA-256 fingerprint of the
server certificate from this connection. Guest and Admin do not use this
identity. A verified identity still requires fresh local approval, including
on reconnect.


The Host operator may save the verified fingerprint and the current optional
feature grants while approving Work. The next connection with the same key
can prefill the exact covered choices. A new clipboard format is unchecked
unless all requested formats were saved. File prefill also requires the same
Host-selected directory. The device list can revoke a record and disconnect
a matching live session. A subsequent request still needs local approval
with the default unchecked optional grants. The WPF/TLS fixture uses an
isolated preference file and checks all of these transitions.

### Local desktop transition pause

A bounded pause can be armed from the current authorized Default desktop.
The capture worker drops its graphics resources and acknowledges that it has
parked. Media and input are held during the pause. The existing TLS control
channel remains available. The eligibility watcher tolerates temporary
input-desktop unavailability only within that pause and in the same active
Windows session. Resume requires the original Default desktop and starts
video with a new key frame. Session loss and unarmed secure desktop
transitions retain their normal denial behavior.

The watcher uses the WTS lock flag when available. A reported lock ends the
session even during the bounded UAC pause. An unavailable flag remains
unknown and desktop eligibility checks still apply. The broker stays in
Session 0 and the interactive inputs are owned by processes in the user
session. [WTS session flags](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ns-wtsapi32-wtsinfoex_level1_w) [Interactive services](https://learn.microsoft.com/en-us/windows/win32/services/interactive-services)

When sharing begins on the physical console, the watcher pins its session ID.
A different console ID or a detached console terminates the sharing grant,
including during an expected desktop pause. [Console session ID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-wtsgetactiveconsolesessionid)

In a negotiated Work session, the Viewer can retry an Admin request after
denial or after returning to Work. A Host-side revocation disables further
elevation for that connection. Each attempt requires a fresh local approval.
Host pauses remote input and releases held keys and buttons before
showing its local approval panel. Approval starts the desktop pause and the
Windows UAC launch. A ready worker waits up to five seconds for Viewer
activation. Denial restores Work. Timeout, revocation, or a display selection
ends the worker grant before Work input resumes. A display selection during
worker startup invalidates that pending grant.

The loopback smoke checks verify that mismatched local approvals are ignored,
that input is discarded while approval is pending, and that Work media and
control survive denial. A separate synthetic desktop check verifies media
pause, control heartbeats and a decodable key frame after resume. Real UAC
launch, administrator-window input and display switching with an active
elevated worker require interactive Windows acceptance.
