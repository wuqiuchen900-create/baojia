"""Add 地面找平 to FloorItemKeywords in both BaoJiaCAD config.json files.

mudiban template has B rows labeled "地面找平" but the existing
FloorItemKeywords = ["地面保护","铺地砖","地砖","地板","正铺"] doesn't contain
"地面找平" \u2014 IsFloorItem() currently returns False, leaving the row at 0.
"""
import json
from pathlib import Path

PATHS = [
    Path(r"E:\xiangmu\baojia\BaoJiaCAD\config.json"),
    Path(r"E:\xiangmu\baojia\BaoJiaCAD\bin\Debug\net48\config.json"),
]

ADD = "\u5730\u9762\u627e\u5e73"   # 地面找平

for path in PATHS:
    if not path.exists():
        print(f"SKIP missing: {path}")
        continue

    cfg = json.loads(path.read_text(encoding="utf-8"))

    ts = cfg.get("TemplateSettings")
    if ts is None:
        print(f"WARN {path}: no TemplateSettings \u2014 skipping")
        continue

    kws = ts.get("FloorItemKeywords")
    if kws is None:
        kws = []
        ts["FloorItemKeywords"] = kws

    if ADD in kws:
        print(f"= already has {ADD!r}: {path}")
        continue

    kws.append(ADD)
    print(f"+ appended {ADD!r} \u2192 FloorItemKeywords = {kws}")

    # Round-trip: preserve indentation (matches the original 2-space indent).
    out = json.dumps(cfg, ensure_ascii=False, indent=2)
    # QuoteConfig reads via Newtonsoft.Json \u2014 indentation is irrelevant to it,
    # but raw byte signature matters: ensure trailing newline is preserved.
    if not out.endswith("\n"):
        out += "\n"
    path.write_text(out, encoding="utf-8")
    print(f"  wrote {path} ({len(out)} bytes)")

print("OK")
