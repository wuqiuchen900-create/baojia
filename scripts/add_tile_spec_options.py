"""将 TileSpecOptions section 插入两份 config.json (源 + bin/Debug 副本).

客餐厅 6 项 variant 配置:
- sp300-800  → 「正铺」AND「300-800MM」 (与菱铺 同尺寸 不歧义)
- sp600-1200 → 「正铺」AND「600*1200」
- sp750-1500 → 「正铺」AND「750*1500」
- sp800-1600 → 「正铺」AND「800*1600」
- sp900-1800 → 「正铺」AND「900*1800」
- spDiamond  → 「菱铺」 (单 keyword)

锚点: 「"Templates": { ... }, " 后面插入 TileSpecOptions。
"""
import json
from pathlib import Path

TILE_SPEC = {
    "客餐厅": [
        {"label": "正铺 300-800MM (人工32)",  "value": "sp300-800",  "match": ["正铺", "300-800MM"]},
        {"label": "正铺 600*1200MM (人工48)", "value": "sp600-1200", "match": ["正铺", "600*1200"]},
        {"label": "正铺 750*1500MM (人工71)", "value": "sp750-1500", "match": ["正铺", "750*1500"]},
        {"label": "正铺 800*1600MM (人工81)", "value": "sp800-1600", "match": ["正铺", "800*1600"]},
        {"label": "正铺 900*1800MM (人工91)", "value": "sp900-1800", "match": ["正铺", "900*1800"]},
        {"label": "菱铺 300-800MM (人工48)",  "value": "spDiamond",  "match": ["菱铺"]}
    ]
}

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
    if "TileSpecOptions" in ts and "客餐厅" in ts["TileSpecOptions"] and len(ts["TileSpecOptions"]["客餐厅"]) >= 6:
        print(f"ALREADY has TileSpecOptions[客餐厅]: {p}")
        continue
    ts["TileSpecOptions"] = TILE_SPEC
    p.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"OK injected TileSpecOptions -> {p}")
