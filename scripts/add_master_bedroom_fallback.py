"""为 RoomTypeFallbackMap 加「主卧 → 客餐厅」 entry - 模板 缺主卧 group 时 查 客餐厅 prototype.

幂等: 已存在 entry 会 skip.
"""
import json
from pathlib import Path

PATHS = [
    Path("BaoJiaCAD/config.json"),
    Path("BaoJiaCAD/bin/Debug/net48/config.json"),
]

for p in PATHS:
    if not p.exists():
        print(f"SKIP missing: {p}")
        continue
    cfg = json.loads(p.read_text(encoding="utf-8"))
    ts = cfg.setdefault("TemplateSettings", {})
    fm = ts.setdefault("RoomTypeFallbackMap", {})
    if "主卧" in fm:
        print(f"ALREADY has 主卧 fallback: {p} ({fm['主卧']})")
        continue
    fm["主卧"] = "客餐厅"
    print(f"OK added 主卧 → 客餐厅 to RoomTypeFallbackMap: {p}")
    p.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")

print("DONE")
