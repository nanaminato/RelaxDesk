#!/usr/bin/env python3
"""Install and exercise the actual Android API 37 UI on a disposable emulator."""
import argparse
import os
import re
import subprocess
import time
import tempfile
from pathlib import Path
import xml.etree.ElementTree as ET

PACKAGE = "com.relaxdesk.viewer.probe"
KEY_BUTTON = "本地设备密钥自检"
KEY_SUCCESS = "签名验证通过：同一密钥可重开，证书指纹变化时拒绝。"


def run(*args: str, timeout: int = 30) -> str:
    return subprocess.check_output(args, text=True, timeout=timeout).strip()


def smoke(adb: str, serial: str, aapt: str, apk: str, fixture: tuple[str, str] | None) -> None:
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

    def wait_node(predicate, label: str) -> dict:
        deadline = time.monotonic() + 80
        while time.monotonic() < deadline:
            try:
                report = "/sdcard/relaxdesk-ci-layout.xml"
                device("shell", "uiautomator", "dump", report, timeout=15)
                root = ET.fromstring(device("shell", "cat", report, timeout=5))
                for n in root.iter("node"):
                    if predicate(n.attrib) and n.get("enabled") != "false":
                        return n.attrib
                    if (n.get("text") or "").startswith("设备密钥自检未通过"):
                        raise RuntimeError(n.attrib["text"])
            except (subprocess.SubprocessError, ET.ParseError):
                pass
            time.sleep(1)
        raise TimeoutError(f"Expected UI state not found: {label}")

    def node(text: str) -> dict:
        return wait_node(lambda n: n.get("text") == text, text)

    def tap(view: dict) -> None:
        x1, y1, x2, y2 = map(int, re.findall(r"\d+", view["bounds"]))
        device("shell", "input", "tap", str((x1 + x2) // 2), str((y1 + y2) // 2))

    def launch() -> None:
        device("shell", "am", "force-stop", PACKAGE)
        completed = device("shell", "am", "start", "-W", "-n", f"{PACKAGE}/{start[1]}")
        assert "Status: ok" in completed, "Launcher activity failed"

    def enter(label: str, value: str) -> None:
        assert re.fullmatch(r"[A-Za-z0-9.]+", value), "Unsafe synthetic input"
        tap(node(label))
        try:
            device("shell", "input", "text", value)
        except subprocess.SubprocessError:
            raise RuntimeError(f"Could not enter fixture value in {label}") from None

    def app_lan() -> None:
        def permission(allow: bool) -> None:
            suffix = "/permission_allow_button" if allow else "/permission_deny_button"
            tap(wait_node(lambda n: (n.get("resource-id") or "").endswith(suffix),
                          "Android LAN permission dialog"))

        def open_form(pin: str) -> None:
            launch()
            tap(node("局域网首帧连接探针"))
            enter("局域网 IP 地址", "10.0.2.2")
            enter("端口", port)
            enter("64 位 SHA-256 证书指纹", pin)
            enter("被控端登录用户名", user)

        def lines() -> list[str]:
            return path.read_text().splitlines()

        def count(line: str) -> int:
            return lines().count(line)

        def await_line(line: str, n: int) -> None:
            deadline = time.monotonic() + 45
            while time.monotonic() < deadline:
                if count(line) == n:
                    return
                if host.poll() is not None:
                    raise RuntimeError("Synthetic host exited unexpectedly")
                time.sleep(0.25)
            raise TimeoutError(f"Fixture did not emit milestone {line}")

        def connect() -> None:
            enter("被控端登录密码", password)
            tap(node("连接并试解首帧"))

        def no_socket_after_callback() -> None:
            time.sleep(2)
            assert count("ACCEPT control") == 0, "Permission callback dialed without explicit approval"
            node("被控端登录密码")

        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "fixture.log"
            with path.open("w") as stream:
                host = subprocess.Popen(["dotnet", fixture[0], "--serve-probe", fixture[1]], stdout=stream)
            try:
                deadline = time.monotonic() + 10
                while not lines() or not lines()[0].startswith("READY "):
                    if time.monotonic() > deadline or host.poll() is not None:
                        raise RuntimeError("Synthetic host did not start")
                    time.sleep(0.15)
                _, port, pin, user, password = lines()[0].split()
                wrong_pin = ("1" if pin[0] == "0" else "0") + pin[1:]
                open_form(wrong_pin)
                connect()
                permission(False)
                node("未获局域网权限，未连接。")
                no_socket_after_callback()
                print("PASS permission denied, no TCP or retained password", flush=True)

                connect()
                permission(True)
                node("局域网权限已授权，请重新输入密码并点击连接。")
                no_socket_after_callback()
                print("PASS permission allowed, no automatic TCP or retained password", flush=True)

                connect()
                node("认证失败，请检查被控端状态及证书指纹。")
                await_line("ACCEPT control", 1)
                assert count("RECV login-request") == 0, "Wrong pin sent a login request"
                print("PASS wrong pin dials TLS but sends no login", flush=True)

                for attempt in range(1, 3):
                    open_form(pin)
                    connect()
                    outcome = wait_node(lambda n: "向 Surface 提交 1 帧" in (n.get("text") or "") or
                                        (n.get("text") or "").startswith("首帧探针未通过："), "first-frame result")["text"]
                    await_line("VALID device-proof-and-password", attempt)
                    await_line("VALID attachments", attempt)
                    await_line("SENT first-avc-frame", attempt)
                    await_line("VALID control-closed", attempt)
                    if "向 Surface 提交 1 帧" in outcome:
                        print(f"PASS emulator submits one AVC frame after launch {attempt}", flush=True)
                    elif outcome == "首帧探针未通过：NotSupportedException":
                        print("SKIP decoder acceptance: emulator has no qualifying AVC decoder", flush=True)
                    else:
                        raise RuntimeError(f"Unexpected Android first-frame outcome: {outcome}")
                    print(f"PASS pinned Android TLS, device proof and all bindings after launch {attempt}", flush=True)
                await_line("VALID same-device-fingerprint", 1)
                assert count("INFO different-device-fingerprint") == 0
                print("PASS same authenticated Android device identity across force-stop", flush=True)
            finally:
                host.terminate()
                try:
                    host.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    host.kill()
                    host.wait(timeout=5)

    device("shell", "input", "keyevent", "82")
    for attempt in range(2):
        launch()
        tap(node(KEY_BUTTON))
        node(KEY_SUCCESS)
        print(f"PASS in-app Android Keystore proof after launch {attempt + 1}", flush=True)
    if fixture:
        app_lan()
    device("shell", "am", "force-stop", PACKAGE)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("apk")
    parser.add_argument("--serial", default=os.environ.get("ANDROID_SERIAL", "emulator-5554"))
    parser.add_argument("--adb", required=True)
    parser.add_argument("--aapt", required=True)
    parser.add_argument("--fixture", nargs=2, metavar=("TEST_DLL", "SYNTHETIC_RDM2"))
    args = parser.parse_args()
    smoke(args.adb, args.serial, args.aapt, args.apk, args.fixture)
