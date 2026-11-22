# Actual IP/port approval and fresh-package identity — 2026-11-22

Implemented default actual-network flow: IP/port → TLS control connection → Host endpoint confirmation/APPROVE → one-use bound media TLS → desktop/input session. Viewer no longer requires copied pins/tokens/invitations. Form hides only after media approval. Host with no arguments prompts for a specific local interface IP/port; no wildcard or unattended listener was introduced. Preview entry removed from the primary form (old local UI test helper is not the connection path).

New request-v2 contains a random 128-bit nonce and bounded capabilities. It is explicitly a consent request, not proof of a known peer identity. Host confirmation is mandatory; capture/media/input are inaccessible before approval. First-contact cert is trusted for this attempt, then pinned for media. TLS 1.2/1.3, one-use media ticket, authorization gate/sequence checking remain. This first-contact mode lacks out-of-band MITM identity verification and is intended for trusted LANs. Advanced strict pinned mode remains and never silently falls back on failed pin/token checks. No trust-store/global TLS/security policy was changed.

## Fresh artifact

`build/delivery-ip/RelaxDesk-0.3.0-IP-Port-win-x64.zip`

Version **0.3.0-ipapproval.20261122-233720.gad43369**; clean commit **ad433699b6f43f528713b5f1deee38974de1cd04**.
FileVersion **0.3.1122.2337** in both apphosts. ZIP SHA256:

```
AB0330C014F4F6282106DD2502E7CC9FD40F53EBC82C549D9792935E26D75A1A
```

New output folder was created, never overwritten. Both binaries were republished self-contained with explicit informational/FileVersion properties. UI title/toolbar, CLI --version, deployment-info, BUILD-INFO.txt and package.json identify the build. Unchanged .NET/native runtime files can preserve SDK/build timestamps; timestamps alone are not a version check. Both Host and Viewer must be replaced from the same new package. Do not launch an old shortcut or merge directories.

The actual ZIP was freshly extracted to `build/IP Approval Fresh Extraction/`. Development tools/dotnet were removed from PATH and DOTNET_ROOT was nonexistent. All 1041 listed hashes pass. Packaged collector verifies Host/Viewer version equality against manifest, runtime locality, security/core/UI/native contracts. [Raw collector result](2026-11-22-ip-approval-package.txt). Environment still Server/RDP, therefore **NOT_ACCEPTED_ENVIRONMENT**, never Home acceptance.

## Executed checks

- Solution Release build: 0 warnings/0 errors; four win-x64 self-contained publishes succeeded.
- **249 Core/TLS assertions**, including first-contact hello and rejection of changed media-channel certificate.
- Original wrong token, wrong fingerprint, Host refusal and wrong media-ticket tests pass.
- New production Host test: client receives no fingerprint/token, request stays pending before approval; DENY closes cleanly without capture/media/input.
- Real separately launched packaged Host + WPF Viewer: no fingerprint/token/invitation supplied; Host APPROVE then H.264 decoding/CPU submission of 60 frames. Native resources 0; Host exit after Viewer ~92ms. [Actual run](2026-11-22-ip-approval-video.txt).
- Published WPF interface test/snapshot confirms visible IP+port, collapsed optional credentials and visible stamped version. This is not the old offline-preview feature or only a mocked interface.

Frames used for automatic tests are explicitly synthetic. The network/process/authorization/codec/UI chain is real; two physical computers sharing Home desktop and actual input remain manual validation. Existing DXGI/HDR/long-disconnect acceptance limits are unchanged. No GPU/HDR/physical input success or MSTSC target is claimed.

## Run

Freshly extract ZIP to a new folder on each PC. Double-click Host/RelaxDesk.Host.exe and enter local LAN IP/port; Viewer/RelaxDesk.Viewer.exe needs only that IP/port. Host confirms the expected source with APPROVE. No copied long strings. Check the stamped 0.3.0-ipapproval version, not SDK runtime timestamps. See [operator instructions](../packaging/RUN-ME.zh-CN.md).
