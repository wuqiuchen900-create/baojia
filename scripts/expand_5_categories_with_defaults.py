"""扩 TileSpecOptions → 5 类别 (客餐厅 / 主卧 / 卧室 / 厨房 / 卫生间), 每类 6 个 spec, 显式 isDefault.

User 2025-07-13 spec:
  - 客餐厅 默认 sp750-1500
  - 主卧   默认 sp750-1500
  - 卧室   默认 sp300-800  (次卧/客房 走小砖)
  - 厨房   默认 sp300-800
  - 卫生间 默认 sp300-800

同时改 RoomTypeMaps 在「卧室」前插入「主卧」entry (ClassifyRoom first-match-wins, 位置很关键).
"""
import json
from pathlib import Path

# 5 类 × 6 spec, IsDefault 显式 (与 JSON 顺序无关, 依赖 QuotePanel.IsDefault 字段读)
TILE_SPEC_FINAL = {
    "客餐厅": [
        {"label": "正铺 750*1500MM (人工71)",  "value": "sp750-1500",   "match": ["正铺", "750*1500"],  "isDefault": True},
        {"label": "正铺 300-800MM (人工32)",   "value": "sp300-800-LR", "match": ["正铺", "300-800MM"]},
        {"label": "正铺 600*1200MM (人工48)",  "value": "sp600-1200-LR","match": ["正铺", "600*1200"]},
        {"label": "正铺 800*1600MM (人工81)",  "value": "sp800-1600-LR","match": ["正铺", "800*1600"]},
        {"label": "正铺 900*1800MM (人工91)",  "value": "sp900-1800-LR","match": ["正铺", "900*1800"]},
        {"label": "菱铺 300-800MM (人工48)",   "value": "spDiamond-LR", "match": ["菱铺"]}
    ],
    "主卧": [
        # 主卧 与 客餐厅 同用手「正铺」naming assumption; 若后 模块使用「正铺」+ size 主卧 rows, 会自动命中.
        {"label": "正铺 750*1500MM (人工71)",  "value": "sp750-1500-MBR","match": ["正铺", "750*1500"], "isDefault": True},
        {"label": "正铺 300-800MM (人工32)",   "value": "sp300-800-MBR", "match": ["正铺", "300-800MM"]},
        {"label": "正铺 600*1200MM (人工48)",  "value": "sp600-1200-MBR","match": ["正铺", "600*1200"]},
        {"label": "正铺 800*1600MM (人工81)",  "value": "sp800-1600-MBR","match": ["正铺", "800*1600"]},
        {"label": "正铺 900*1800MM (人工91)",  "value": "sp900-1800-MBR","match": ["正铺", "900*1800"]},
        {"label": "菱铺 300-800MM (人工48)",   "value": "spDiamond-MBR", "match": ["菱铺"]}
    ],
    "卧室": [
        # 次卧 默认 300-800, 未来模板加 tile row 后可 nav.
        {"label": "正铺 300-800MM (人工32)",   "value": "sp300-800-BR",  "match": ["正铺", "300-800MM"], "isDefault": True},
        {"label": "正铺 600*1200MM (人工48)",  "value": "sp600-1200-BR", "match": ["正铺", "600*1200"]},
        {"label": "正铺 750*1500MM (人工71)",  "value": "sp750-1500-BR", "match": ["正铺", "750*1500"]},
        {"label": "正铺 800*1600MM (人工81)",  "value": "sp800-1600-BR", "match": ["正铺", "800*1600"]},
        {"label": "正铺 900*1800MM (人工91)",  "value": "sp900-1800-BR", "match": ["正铺", "900*1800"]},
        {"label": "菱铺 300-800MM (人工48)",   "value": "spDiamond-BR",  "match": ["菱铺"]}
    ],
    "厨房": [
        # 厨房 row 命名 「铺地砖（正贴）」 — match 「正贴」与「菱贴」区分正菱 spacing.
        {"label": "正贴 300-800MM (人工32)",   "value": "sp300-800-K",   "match": ["正贴", "300-800MM"], "isDefault": True},
        {"label": "正贴 600*1200MM (人工48)",  "value": "sp600-1200-K",  "match": ["正贴", "600*1200"]},
        {"label": "正贴 750*1500MM (人工71)",  "value": "sp750-1500-K",  "match": ["正贴", "750*1500"]},
        {"label": "正贴 800*1600MM (人工81)",  "value": "sp800-1600-K",  "match": ["正贴", "800*1600"]},
        {"label": "正贴 900*1800MM (人工91)",  "value": "sp900-1800-K",  "match": ["正贴", "900*1800"]},
        {"label": "菱贴 300-800MM (人工48)",   "value": "spDiamond-K",   "match": ["菱贴"]}
    ],
    "卫生间": [
        # 卫生间 同「铺地砖（正贴）」pattern; RoomType-scoped 防 cross-contamination.
        {"label": "正贴 300-800MM (人工32)",   "value": "sp300-800-G",   "match": ["正贴", "300-800MM"], "isDefault": True},
        {"label": "正贴 600*1200MM (人工48)",  "value": "sp600-1200-G",  "match": ["正贴", "600*1200"]},
        {"label": "正贴 750*1500MM (人工71)",  "value": "sp750-1500-G",  "match": ["正贴", "750*1500"]},
        {"label": "正贴 800*1600MM (人工81)",  "value": "sp800-1600-G",  "match": ["正贴", "800*1600"]},
        {"label": "正贴 900*1800MM (人工91)",  "value": "sp900-1800-G",  "match": ["正贴", "900*1800"]},
        {"label": "菱贴 300-800MM (人工48)",   "value": "spDiamond-G",   "match": ["菱贴"]}
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
    rt_maps = cfg.setdefault("RoomTypeMaps", [])

    # 1) Insert 主卧 RoomTypeMap BEFORE 卧室 (first-match-wins 顺序很关键).
    insert_idx = None
    for i, m in enumerate(rt_maps):
        if m.get("RoomType") == "卧室":
            insert_idx = i
            break
    if insert_idx is None:
        print(f"WARN: no 卧室 entry found in {p}, skipping 主卧 insert")
    else:
        # 检查是否已经有 "主卧" RoomTypeMap (幂等)
        ya = any(m.get("RoomType") == "主卧" for m in rt_maps)
        if not ya:
            rt_maps.insert(insert_idx, {
                "Keywords": ["主卧", "主人房", "主人卧室"],
                "RoomType": "主卧"
            })
            print(f"OK inserted 主卧 RoomTypeMap (idx={insert_idx}): {p}")
        else:
            print(f"ALREADY has 主卧 RoomTypeMap: {p}")

    # 2) 重写 TileSpecOptions 为 5 类版本.
    ts = cfg.setdefault("TemplateSettings", {})
    ts["TileSpecOptions"] = TILE_SPEC_FINAL
    print(f"OK set 5 类别 TileSpecOptions: {p}")

    p.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")

print("DONE")
