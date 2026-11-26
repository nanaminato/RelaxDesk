# Password sessions and automatic reconnect

The Host starts sharing from its ordinary Windows user session and binds only the
selected local address. It remains available between connections until the
operator stops sharing or closes the window. One controller is admitted at a time.

The Viewer verifies the Host TLS certificate before sending the configured
username and password. The Host verifies the password and challenges the Viewer
to prove possession of its device key. The operator must approve a new device
before the Host opens a desktop, input or auxiliary channel. Approving
records that device for the complete desktop profile. A recognized device still
requires the correct password. Passwords are not written to Viewer history.
Changing the Host password clears device approvals.

## Local controls

Local IPC commands name the current session. Delayed commands cannot approve,
revoke or disconnect another session. Disconnect ends the current session and
returns to listening. Stop Sharing closes the listener. Revoking a device ends
its active session and removes its unattended approval. File copy, clipboard and system audio have no separate approval or revoke commands.

## Recovery

After a successful session, network EOF, reset or control silence triggers up to
five retries within a 120-second outage window. Delays are 1, 2, 4, 8 and 8
seconds with jitter. A stable session with a presented frame and 30 seconds of
connection time resets the outage budget. Retry uses the same verified
certificate, password and device identity. It never opens a new approval
prompt. Invalid credentials, revoked devices, certificate changes, operator
cancellation and a received Host termination notice stop retry.

Each attempt uses fresh media and data tickets. The Viewer releases input,
clears its old queue and requests a keyframe on reconnection. Interrupted copies must be copied again after reconnecting. Internal byte resume
requires a current advertised scope. See [file resume](FILE-RESUME.md)
and [display topology](DISPLAY-TOPOLOGY.md).

## Validation

Run `RelaxDesk.HostShell --password-login-contracts <Host.exe> <output>` for
local TLS and WPF coverage of password validation, first approval, unattended
return, reconnect and device revocation. The fixture uses synthetic video.
Physical Windows Home input, LAN outages and a secure-desktop transition
require separate tests. Unattended access requires the original Windows
desktop to stay signed in, unlocked and shared.
