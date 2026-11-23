"""Summarize recorded data only; does not run or invent benchmark samples."""
import json
import pathlib
import re
import statistics
import argparse

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("root", nargs="?", default="benchmarks/latest-renderers")
parser.add_argument("--modes", nargs="+", choices=("deflate", "software", "hardware-cpu", "gpu"), default=("deflate", "software", "hardware-cpu", "gpu"))
arguments = parser.parse_args()
if len(set(arguments.modes)) != len(arguments.modes):
    parser.error("modes must be distinct")
root = pathlib.Path(arguments.root)
result = {}
for mode in arguments.modes:
    folder = root / mode
    lines = (folder / "video.txt").read_text(encoding="utf-8-sig").splitlines()

    def values(prefix):
        line = next(line for line in lines if line.startswith(prefix))
        return {key: float(value) for key, value in re.findall(r"([a-zA-Z_][a-zA-Z0-9_]*)=(-?[0-9]+(?:\.[0-9]+)?)", line)}

    processes = json.loads((folder / "processes.json").read_text(encoding="utf-8-sig"))
    samples = json.loads((folder / "resources.json").read_text(encoding="utf-8-sig"))
    if isinstance(processes, dict):
        processes = [processes]
    completion_path = folder / "result.json"
    completion = json.loads(completion_path.read_text(encoding="utf-8-sig")) if completion_path.exists() else None
    if completion is not None and (completion.get("Status") != "PASS" or completion.get("ExitCode") != 0):
        raise ValueError("Benchmark did not complete successfully")
    if completion is None and any("Role" in process for process in processes):
        raise ValueError("New benchmark evidence lacks terminal result")
    if isinstance(samples, dict):
        samples = [samples]
    resources = {}
    for process in processes:
        # Explicit role works with both developer DLLs and packaged EXEs.
        # Preserve old recorded evidence, but never guess unknown records as Viewer.
        role = process.get("Role")
        if role is None:
            command = process.get("CommandLine", "")
            role = "host" if re.search(r"RelaxDesk\.Host\.(?:dll|exe)(?:[\s\"]|$)", command, re.I) else "viewer" if re.search(r"RelaxDesk\.Viewer\.(?:dll|exe)(?:[\s\"]|$)", command, re.I) else None
        if role not in ("host", "viewer") or role in resources:
            raise ValueError("Unknown/duplicate benchmark process role")
        own = [row for row in samples if row["ProcessId"] == process["ProcessId"] and ("StartedUtcTicks" not in process or row.get("StartedUtcTicks") == process["StartedUtcTicks"])]
        cpu = [row["CpuPercent"] for row in own if row["CpuPercent"] is not None]
        engines = {}
        for row in own:
            for engine in row.get("GpuEngines", []):
                name = engine["Instance"].split("_engtype_")[-1]
                engines[name] = max(engines.get(name, 0), engine["UtilizationPercent"])
        resources[role] = {
            "samples": len(own),
            "cpu_mean_percent": statistics.mean(cpu) if cpu else None,
            "working_set_peak_bytes": max((row["WorkingSetBytes"] for row in own), default=None),
            "gpu_engine_peak_percent_by_driver_label": engines,
            "gpu_shared_peak_bytes": max((sum(item["SharedBytes"] or 0 for item in row["GpuMemory"]) for row in own if row.get("GpuMemory")), default=None),
            "gpu_dedicated_peak_bytes": max((sum(item["DedicatedBytes"] or 0 for item in row["GpuMemory"]) for row in own if row.get("GpuMemory")), default=None),
            "sampling_errors": list({row["GpuError"] for row in own if row.get("GpuError")}),
        }
    result[mode] = {"viewer": values("PASS WPF"), "host": values("media "), "encoder": values("encode_stages "), "resources": resources, "terminal_result": "PASS_EXIT_0" if completion else "LEGACY_EXIT_NOT_RECORDED"}

destination = root / "summary.json"
destination.write_text(json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
print(destination)
for mode, data in result.items():
    viewer, host = data["viewer"], data["host"]
    print(f"{mode}: encode P50/P95={host['encode_p50_ms']}/{host['encode_p95_ms']} ms; "
          f"decode={viewer['decode_p50_ms']}/{viewer['decode_p95_ms']} ms; "
          f"FPS={viewer['fps']}; readback bytes={viewer['full_frame_readback_bytes']:.0f}; "
          f"drops={viewer['dropped']:.0f}; received={viewer['received_frames']:.0f}")
