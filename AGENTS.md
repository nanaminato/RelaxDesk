# Codex / coding agent instructions

## Mission

Build **RelaxDesk**, a standalone, high-performance, pixel-level Windows 10/11 Home remote-control host and Windows viewer. Shared **physical desktop**, not independent RDP session. Aim to approach MSTSC office responsiveness, verified by benchmarks; do not claim “80%” as achieved without data.

Read `README.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, and `docs/BENCHMARKS.md` before changing architecture.

## Working principles

1. Work toward usable end-to-end milestones. Avoid repeatedly stopping after small scaffolding changes; continue through the current milestone within the available execution.
2. Keep UI, protocol, transport, capture, encode/decode, session/security and diagnostics separated. Host core has **no UI dependency** and no RelaxKonOS dependency.
3. Prefer latest **stable** .NET SDK installed/supported in the environment; current design targets .NET 11 and C# 15. If unavailable, report the limitation rather than silently inventing test success or switching to prerelease.
4. WPF is the initial viewer. C++20 is permitted for measurable Windows graphics/codec interop; no mandatory native implementation where managed code suffices.
5. Keep privileged service small and isolated; session agent belongs to the correct interactive session. Never conflate elevated admin with access to every secure desktop.
6. LAN MVP must still use authentication, encryption and explicit host authorization. Never embed shared passwords/secrets, ship unrestricted listener or disable UAC.
7. Do not modify Windows RDP components, licensing checks or system policy to enable Home RDP host. No stealth, persistence without user consent or hidden control.
8. Benchmark alternatives before implementing complex hybrid video/image transport. Instrument first, optimize based on actual measurements.
9. Unit-test core state, protocol parsing and access decisions. Add executable Windows integration/perf tests and describe hardware-required/manual coverage.
10. Use conventional, readable names and small verified commits. Add docs when behavior, protocol or permissions change.

## Execution/reporting contract

At the start of a work session, inspect the repository and choose the next unmet **phase exit criterion**. Implement as much of that milestone as reasonably possible before stopping. Build and test whatever is runnable. Include a truthful completion report: files/commits, how to run, automated test results, manual test status, performance measurements and remaining blockers. Do not describe an untested UAC or encoder path as verified. If blocked by missing Windows hardware, implement reproducible contracts, mocks/tests and document exact validation needed on Windows.

## First assignment

Start with Phase 0 then produce a minimal authenticated LAN vertical slice: a host and viewer exchanging a real desktop image and input on Windows Home using supported capture APIs, with simple secure pairing/approval, timing instrumentation, and clean disconnect. Prefer a functioning bounded baseline over an ambitious all-at-once feature list.

## Licensing

Do not choose or commit a software license without owner approval. Preserve third-party notices/licenses and avoid copying third-party GPL code into the repository without explicit direction.
