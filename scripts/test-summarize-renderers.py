"""Regression fixtures for summary integrity; all numbers here are invented test data."""
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).with_name("summarize-renderers.py")


class SummaryContracts(unittest.TestCase):
    def collect(self, processes, samples, expect_success=True, completion=True):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            folder = root / "deflate"
            folder.mkdir()
            (folder / "video.txt").write_text("PASS WPF fps=30 decode_p50_ms=1 decode_p95_ms=2 full_frame_readback_bytes=0 dropped=0 received_frames=60\nmedia encode_p50_ms=1 encode_p95_ms=2\nencode_stages frames=60\n", encoding="utf-8")
            (folder / "processes.json").write_text(json.dumps(processes), encoding="utf-8")
            (folder / "resources.json").write_text(json.dumps(samples), encoding="utf-8")
            if completion is not None:
                (folder / "result.json").write_text(json.dumps({"Status": "PASS" if completion else "FAIL", "ExitCode": 0 if completion else 1}), encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), str(root), "--modes", "deflate"], capture_output=True, text=True, timeout=10)
            if not expect_success:
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse((root / "summary.json").exists())
                return None
            self.assertEqual(result.returncode, 0, result.stderr)
            return json.loads((root / "summary.json").read_text())["deflate"]["resources"]

    def test_empty_samples_are_unknown(self):
        result = self.collect([{"Role": "host", "ProcessId": 1}, {"Role": "viewer", "ProcessId": 2}], [])
        self.assertIsNone(result["host"]["cpu_mean_percent"])
        self.assertIsNone(result["host"]["working_set_peak_bytes"])
        self.assertEqual(result["viewer"]["samples"], 0)

    def test_reused_pid_is_excluded_and_scalar_legacy_sample_supported(self):
        result = self.collect({"Role": "host", "ProcessId": 1, "StartedUtcTicks": 10}, {"ProcessId": 1, "StartedUtcTicks": 11, "CpuPercent": 99, "WorkingSetBytes": 100})
        self.assertEqual(result["host"]["samples"], 0)

    def test_packaged_legacy_roles_and_one_sample(self):
        result = self.collect([{"CommandLine": '"C:\\with space\\RelaxDesk.Host.exe"', "ProcessId": 1}, {"CommandLine": '"C:\\with space\\RelaxDesk.Viewer.exe" --render-smoke', "ProcessId": 2}], {"ProcessId": 1, "CpuPercent": None, "WorkingSetBytes": 100})
        self.assertEqual(result["host"]["working_set_peak_bytes"], 100)
        self.assertEqual(result["viewer"]["samples"], 0)

    def test_unknown_role_is_rejected(self):
        self.collect([{"CommandLine": "something-else.exe", "ProcessId": 1}], [], expect_success=False)

    def test_duplicate_role_is_rejected(self):
        self.collect([{"Role": "host", "ProcessId": 1}, {"Role": "host", "ProcessId": 2}], [], expect_success=False)

    def test_new_evidence_without_exit_result_is_rejected(self):
        self.collect([{"Role": "host", "ProcessId": 1}], [], expect_success=False, completion=None)

    def test_failed_result_is_rejected(self):
        self.collect([{"Role": "host", "ProcessId": 1}], [], expect_success=False, completion=False)


if __name__ == "__main__":
    unittest.main()
