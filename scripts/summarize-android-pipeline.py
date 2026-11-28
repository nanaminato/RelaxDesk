"""Summarize one approved foreground session per Android logcat capture.

Use the same source, host and manual settings for all captures. FPS uses
counter differences; stage percentiles are the final bounded recent window.
"""
import argparse
import json
from pathlib import Path


def summarize(path, skip, intervals):
    rows = []
    perf = None
    for line in path.read_text(encoding="utf-8").splitlines():
        if "RelaxDeskPerf: " in line:
            perf = json.loads(line.split("RelaxDeskPerf: ", 1)[1])
        if "RelaxDeskPipeline: " in line:
            sample = json.loads(line.split("RelaxDeskPipeline: ", 1)[1])
            if rows and sample["Seconds"] <= rows[-1][0]["Seconds"]:
                raise ValueError(f"{path}: multiple sessions or non-monotonic samples")
            rows.append((sample, perf))
    if len(rows) <= skip + intervals:
        raise ValueError(f"{path}: need {skip + intervals + 1} pipeline samples")
    (before, old_perf), (after, new_perf) = rows[skip], rows[skip + intervals]
    if old_perf is None or new_perf is None:
        raise ValueError(f"{path}: missing matching drop telemetry")
    seconds = after["Seconds"] - before["Seconds"]
    received = after["Received"] - before["Received"]
    output = after["Output"] - before["Output"]
    drops = new_perf["TotalDropped"] - old_perf["TotalDropped"]
    if seconds <= 0 or min(received, output, drops) < 0:
        raise ValueError(f"{path}: invalid or reset counters")
    return {
        "seconds": seconds,
        "received": received,
        "output": output,
        "rxFps": received / seconds,
        "outputFps": output / seconds,
        "drops": drops,
        "knownDropPercent": 100 * drops / received if received else None,
        "applicationMbps": (after["EncodedBytes"] - before["EncodedBytes"]) * 8 / seconds / 1e6,
        "keyFrames": after["KeyFrames"] - before["KeyFrames"],
        "burstArrivals": after["BurstArrivals"] - before["BurstArrivals"],
        "inputWaits": after["InputWaits"] - before["InputWaits"],
        "codecRecoveries": after["CodecRecoveries"] - before["CodecRecoveries"],
        "expired": new_perf.get("DroppedExpired", 0) - old_perf.get("DroppedExpired", 0),
        "peakQueueDepth": after["PeakQueueDepth"],
        "rttSmoothedMs": new_perf["RttMs"],
        "stages": {stage["Name"]: {
            "p50": stage["P50Milliseconds"], "p95": stage["P95Milliseconds"], "samples": stage["Samples"]
        } for stage in after["Stages"]},
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path, nargs="+")
    parser.add_argument("--output", type=Path)
    parser.add_argument("--skip-samples", type=int, default=2)
    parser.add_argument("--intervals", type=int, default=12)
    args = parser.parse_args()
    if args.skip_samples < 0 or args.intervals < 1:
        parser.error("sample skip must be non-negative and intervals must be positive")
    if len({path.stem for path in args.logs}) != len(args.logs):
        parser.error("capture filenames must have distinct stems")
    result = {path.stem: summarize(path, args.skip_samples, args.intervals) for path in args.logs}
    text = json.dumps(result, indent=2, ensure_ascii=False)
    if args.output:
        args.output.write_text(text + "\n", encoding="utf-8")
    else:
        print(text)


if __name__ == "__main__":
    main()
