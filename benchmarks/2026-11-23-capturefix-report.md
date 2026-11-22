# v0.3.2 capture-frame follow-up

Operator trace: ten frames captured/encoded by NVIDIA, then capture-frame failed with DXGI_ERROR_UNSUPPORTED. This rules out encoder submission as the initial failing stage in this trace. The old combined dimensions/format message does not disclose which texture field was unsupported, so an exact NVIDIA/HDR cause remains unproven.

Changes: classify mouse-only notifications using DXGI frame info; reuse the last owned desktop without querying/copying unrelated image resources, while preserving pointer metadata and exactly-once ReleaseFrame. Before any image is available, release and wait. Prefer DuplicateOutput1 with explicit BGRA8-only formats, capability-only fallback to classic DuplicateOutput. Permission errors never trigger API fallback/retry. No GDI/CPU conversion added. Unsupported actual image dimensions, format or layout have separate messages with width/height/format/expected format/mips/array/samples/present+mouse QPC/metadata/duplication mode/API. Resolution/mode changes still request bounded capture+encoder recreation. Existing protocol pixel limits are retained. OS format conversion is not a verified HDR tone mapper.

[24 native contracts](2026-11-23-capture-contracts.txt) pass with real WARP textures: mismatching 1x1 RGBA on pointer-only update does not replace or alter 4x4 cached pixels; same image update rejects it with dimensions/format 28/expected 87; HDR format 10 is explicitly rejected; initial no-image, mode changes, pixel caps and exact-once release tested. Native resource counters return zero. These are native GPU/software-device tests, not captured NVIDIA desktop reproduction.

Solution Release 0 warnings/0 errors, 253 Core/TLS and 15 ABI/metadata/pointer assertions, all five refusal cases and authenticated Host→WPF video pass. Final WPF run submitted 60/received 82/dropped 22, a functional test only, not performance improvement evidence. [Raw transport/UI run](2026-11-23-capturefix-video.txt).

One [actual Desktop Duplication attempt](2026-11-23-capture-attempt.txt) is denied with E_ACCESSDENIED in the current RDP environment; no repeat-until-success attempt. NVIDIA physical desktop, actual new API negotiation/format conversion and HDR correctness still need the operator's PC. Mouse-only resource handling is a confirmed robustness fix, not confirmation that the logged NVIDIA fault had that exact source.

Delivery: `build/delivery-capture/RelaxDesk-0.3.2-Capture-Fix-win-x64.zip`.
Version **0.3.2-capturefix.20261123-002114.g48b63be**, clean source commit 48b63be1929810697a8b6902b536eba5f36c27a0.
SHA256 **07345246A77C07D77002EAB6F9B5A409BB3B00CF135A14AC59877544ADCA8310**.

Fresh ZIP extraction with no SDK/dotnet CLI in PATH verifies 1041 hashes, new capture contracts, Host/Viewer version equality, safety and UI tests. Collector correctly returns NOT_ACCEPTED_ENVIRONMENT for RDP, not a Home pass. [Package result](2026-11-23-capturefix-package.txt). Both Host and Viewer must be replaced, including native DLLs. If capture still fails, return the entire new producer_error containing width/height/format/dupl_format/api and state whether Host HDR is on. Do not send pairing credentials.

Primary references: [DXGI pointer-only frame semantics](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ns-dxgi1_2-dxgi_outdupl_frame_info), [explicit duplication format negotiation](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_5/nf-dxgi1_5-idxgioutput5-duplicateoutput1).
