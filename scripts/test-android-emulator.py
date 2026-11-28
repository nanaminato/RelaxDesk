#!/usr/bin/env python3
"""Install and exercise the actual Android API 37 UI on a disposable emulator."""
import argparse
import os
import re
import subprocess
import time
import xml.etree.ElementTree as ET

PACKAGE = "com.relaxdesk.viewer.probe"
KEY_BUTTON = "本地设备密钥自检"
KEY_SUCCESS = "签名验证通过：同一密钥可重开，证书指纹变化时拒绝。"


def run(*args: str, timeout: int = 30) -> str:
    return subprocess.check_output(args, text=True, timeout=timeout).strip()


def smoke(adb: str, serial: str, aapt: str, apk: str) -> None:
    if not re.fullmatch(r"emulator-\d+", serial):
        raise ValueError("A local disposable emulator serial is required.")

    def device(*args: str, timeout: int = 30) -> str:
        return run(adb, "-s", serial, *args, timeout=timeout)

    device("wait-for-device", timeout=180)
    deadline = time.monotonic() + 210
    while device("shell", "getprop", "sys.boot_completed") != "1":
        if time.monotonic() > deadline:
            raise TimeoutError("Android did not finish its normal boot")
        time.sleep(2)
    assert device("shell", "getprop", "ro.kernel.qemu") == "1", "Not an emulator"
    assert device("shell", "getprop", "ro.build.version.sdk") == "37", "Wrong Android API"
    assert device("shell", "getprop", "ro.product.cpu.abi") == "x86_64", "Wrong ABI"
    print("PASS Android 17 x86_64 emulator boot", flush=True)

    badging = run(aapt, "dump", "badging", apk)
    start = re.search(r"^launchable-activity: name='([^']+)'", badging, re.MULTILINE)
    if not start or "package: name='" + PACKAGE + "'" not in badging:
        raise ValueError("Unexpected APK launcher or package")
    assert "Success" in device("install", "--no-incremental", "-r", apk, timeout=60)
    print("PASS APK install", flush=True)

    def node(text: str) -> dict:
        deadline = time.monotonic() + 80
        while time.monotonic() < deadline:
            try:
                report = "/sdcard/relaxdesk-ci-layout.xml"
                device("shell", "uiautomator", "dump", report, timeout=15)
                root = ET.fromstring(device("shell", "cat", report, timeout=5))
                for n in root.iter("node"):
                    if n.get("text") == text and n.get("enabled") != "false":
                        return n.attrib
                    if (n.get("text") or "").startswith("设备密钥自检未通过"):
                        raise RuntimeError(n.attrib["text"])
            except (subprocess.SubprocessError, ET.ParseError):
                pass
            time.sleep(1)
        raise TimeoutError(f"Expected UI state not found: {text}")

    device("shell", "input", "keyevent", "82")
    for attempt in range(2):
        device("shell", "am", "force-stop", PACKAGE)
        completed = device("shell", "am", "start", "-W", "-n", f"{PACKAGE}/{start[1]}")
        assert "Status: ok" in completed, "Launcher activity failed"
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", node(KEY_BUTTON)["bounds"]))
        device("shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2))
        node(KEY_SUCCESS)
        print(f"PASS in-app Android Keystore proof after launch {attempt + 1}", flush=True)
    device("shell", "am", "force-stop", PACKAGE)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk")
    parser.add_argument("--serial", default=os.environ.get("ANDROID_SERIAL", "emulator-5554"))
    parser.add_argument("--adb", required=True)
    parser.add_argument("--aapt", required=True)
    args = parser.parse_args()
    smoke(args.adb, args.serial, args.aapt, args.apk)
