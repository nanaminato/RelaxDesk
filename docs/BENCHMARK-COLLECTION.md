# Reproducible synthetic collection

These tools collect software/API and process-resource evidence from generated images. They do not drive real office applications or certify visible desktop correctness, input-to-photon, NIC throughput or matched MSTSC performance. Physical workflow remains [Home acceptance](PHYSICAL-ACCEPTANCE.md).

From a Release solution build (use a fresh output directory for every run):

```powershell
./scripts/benchmark-video.ps1 -Frames 300 -ArtifactsDirectory build/my-artifacts -OutputDirectory benchmarks/my-video
./scripts/benchmark-renderers.ps1 -Frames 120 -ArtifactsDirectory build/my-artifacts -OutputDirectory benchmarks/my-renderers -Modes deflate,software,hardware-cpu
python scripts/summarize-renderers.py benchmarks/my-renderers --modes deflate software hardware-cpu
```

Run the source office acceptance suite against the same Release build:

```powershell
dotnet build/my-artifacts/bin/RelaxDesk.Smoke/release/RelaxDesk.Smoke.dll --office-suite-source build/my-artifacts build/my-office-results
```

The suite saves a per-case stdout and stderr log alongside `result.json`. It exercises file integrity during Auto switching, WPF media recovery and statistics after fallback, and saves software text-quality measurements from a generated editor frame. It uses the loopback network and synthetic desktop sources and does not request Windows administrator consent.

Omit `-ArtifactsDirectory` for conventional project build paths. For a final self-contained bundle, use `-PackageRoot <extracted-bundle>` instead; it launches EXEs without a globally installed SDK/runtime. Default renderer modes also include `gpu`, which requires actual supported window/Present conditions; explicitly select runnable modes when blocked. Selected modes are recorded and never presented as a complete four-mode comparison. `Frames` is 60–1800; renderer tests use a fixed received-media budget, not equal wall-clock duration or guaranteed submitted/displayed FPS.

The Smoke driver reports only `Role`, PID and process creation UTC ticks for its owned Host/Viewer. Discovery does not require WMI process enumeration and does not collect command lines, pins, tokens or inputs. Sampling verifies PID plus creation time, skips mismatched identities, resets CPU deltas across identity changes and disposes process handles. Each direction of video/control remains production code with normal authentication/approval. New diagnostic lines are emitted only by Smoke, with no production protocol change.

`run.json` records source type, selected modes/budget, UTC, OS version, logical processors, available GPU names/drivers (or query failure), and SHA-256 of applications plus adjacent RelaxDesk DLLs, including native code. `processes.json` records roles/identities. `resources.json` is always an array, including zero or one sample. CPU is percent of all logical processors; its first sample is unknown. GPU engine labels are separate driver counters, not an aggregate utilization percentage. Absent resource/GPU data remains unknown rather than invented zero. GPU query failures are recorded without preventing CPU/memory collection. Queries request a two-second provider timeout; sampling and child waits are bounded, and only owned driver trees are terminated on failure.

`result.json` records explicit PASS/FAIL and actual child exit code. Windows PowerShell 5.1 requires retaining the process handle before exit; a missing exit code is a failure. New summaries require PASS and exit 0. Historical command-line-based records remain readable with `LEGACY_EXIT_NOT_RECORDED`; unknown/duplicate roles fail. New role records without a terminal result fail. Source-exact stream tests and high-speed physical camera measurements remain separate evidence.

Tool verification (Windows PowerShell 5.1 and PowerShell 7 supported):

```powershell
./scripts/test-benchmark-tools.ps1
python scripts/test-summarize-renderers.py
```

The first uses its own real process for identity/CPU/memory contracts without desktop/input operations. Python uses invented fixtures to verify summary integrity; those values are not benchmark samples. Record each run's version, exit status and limitations. Current measurements are listed in the [benchmark index](../benchmarks/README.md).
