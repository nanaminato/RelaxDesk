"""Audit UI resource coverage without inspecting or translating user content."""
import json
import re
from pathlib import Path

root = Path(__file__).resolve().parent.parent
packs = {
    path.stem: json.loads(path.read_text(encoding="utf-8"))["strings"]
    for path in (root / "src/RelaxDesk.Core/Localization/Locales").glob("*.json")
}
keys = set(packs["en"])
assert set(packs) == {"en", "zh-CN", "ja", "fr", "es"}
for code, entries in packs.items():
    assert set(entries) == keys, f"Resource keys differ: {code}"
    for key, text in entries.items():
        assert text.strip(), (code, key)
        slots = lambda value: sorted(re.findall(r"(?<!\{)\{\d+(?:,[^:}]+)?(?::[^}]+)?\}", value))
        assert slots(text) == slots(packs["en"][key]), (code, key, "format placeholders")

areas = ["RelaxDesk.Viewer", "RelaxDesk.HostShell", "RelaxDesk.Windows", "RelaxDesk.Core", "RelaxDesk.Service", "RelaxDesk.ElevatedInput", "SharedUI"]
files = 0
for area in areas:
    for path in (root / "src" / area).rglob("*"):
        if path.suffix not in {".cs", ".xaml"} or "obj" in path.parts or "Verification" in path.parts:
            continue
        if "Localization" in path.parts:
            continue
        source = path.read_text(encoding="utf-8-sig")
        files += 1
        for number, line in enumerate(source.splitlines(), 1):
            if not re.search(r"[\u4e00-\u9fff]", line):
                continue
            # Fixed multilingual sample content is a codec benchmark input, not UI copy.
            if path.name == "OfficeTextQuality.cs" and "editor_0123456789" in line:
                continue
            raise AssertionError(f"Unlocalized CJK text: {path.relative_to(root)}:{number}")
        references = re.findall(r'\{ui:Loc ([\w.-]+)\}|Strings\.(?:Get|Format)\("([\w.-]+)"', source)
        for pair in references:
            key = pair[0] or pair[1]
            assert key in keys, (path, "missing resource", key)
assert "实际提交帧率与媒体流量见连接统计" not in (root / "src/RelaxDesk.Viewer/Session/SessionToolbar.xaml.cs").read_text(encoding="utf-8")
assert "码率是目标参数；更高目标通常增加带宽" not in (root / "src/RelaxDesk.Viewer/Session/SessionToolbar.xaml.cs").read_text(encoding="utf-8")
print(f"PASS localization audit: {files} source files, {len(keys)} messages in five languages; no unlocalized CJK UI text or missing literal resource keys")
