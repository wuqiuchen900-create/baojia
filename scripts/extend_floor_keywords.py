"""Extend FloorItemKeywords with 4 new entries across both config.json files.

Inventory audit across all 4 templates found these items that semantically
represent the room's floor area but were not matched by the existing 6 keywords:

- "自流平"        \u2192 match "自流平找平处理"
- "整体找平"      \u2192 match "地暖后整体找平"
- "成品保护"      \u2192 match the protection film / item at end of construction
- "地台"          \u2192 match "阳台木制地台", "水泥地台", etc.

NOT added (deliberately):
- 过门石 / 门槛大理石铺贴 / 淋浴基石安装 \u2014 per-piece / per-meter items,
  need a different calculation rule (NOT room.FloorArea).
"""
import json
from pathlib import Path

PATHS = [
    Path(r"E:\xiangmu\baojia\BaoJiaCAD\config.json"),
    Path(r"E:\xiangmu\baojia\BaoJiaCAD\bin\Debug\net48\config.json"),
]

NEW_KW = [
    "\u81ea\u6d41\u5e73",       # 自流平
    "\u6574\u4f53\u627e\u5e73",  # 整体找平
    "\u6210\u54c1\u4fdd\u62a4",  # 成品保护
    "\u5730\u53f0",              # 地台
]

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

    added = [k for k in NEW_KW if k not in kws]
    if not added:
        print(f"= already has all 4 new keywords: {path}")
        print(f"    current: {kws}")
        continue

    kws.extend(added)
    out = json.dumps(cfg, ensure_ascii=False, indent=2)
    if not out.endswith("\n"):
        out += "\n"
    path.write_text(out, encoding="utf-8")
    print(f"+ appended {added} \u2192 {path}")
    print(f"    new FloorItemKeywords = {kws}")

print("OK")
