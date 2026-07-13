"""预配 厨房/公卫/主卫 3 类到两份 config.json (src + bin/Debug 副本).

策略:
- 厨房: 2个 variant (用户原话 "正铺地砖 300-800 和菱铺" → 正贴 300-800 + 菱贴 300-800)
- 公卫/主卫: 空 list 作 placeholder (目前模板只有 1 行 铺地砖(正贴), 没 variant 可匹配;
  但留 key 在 config 方便以后模板加 variant 时直接在此处填, QuotePanel 自动 ≥2 显示下拉)
"""
import json
from pathlib import Path

EXTRA_SPECS = {
    "厨房": [
        # 正贴 300-800 与 菱贴 300-800 同尺寸但铺法不同, 用 AND-match ["正贴", "300-800MM"] vs ["菱贴"] 避免歧义.
        {"label": "正贴 300-800MM (人工32)", "value": "sp300-800-K", "match": ["正贴", "300-800MM"]},
        {"label": "菱贴 300-800MM (人工48)", "value": "spDiamond-K", "match": ["菱贴"]}
    ],
    "公卫": [],   # placeholder — 等模板加 variant (e.g. 600*1200 / 750*1500) 时填, ≥2 才显示下拉
    "主卫": []    # placeholder
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
    tso = ts.setdefault("TileSpecOptions", {})
    for room, specs in EXTRA_SPECS.items():
        if room in tso and len(tso[room]) >= len(specs):
            print(f"ALREADY has {room} (count={len(tso[room])}): {p}")
            continue
        tso[room] = specs
        print(f"OK set {room} (count={len(specs)}): {p}")
    p.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")

print("DONE")
