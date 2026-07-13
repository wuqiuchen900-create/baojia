"""
Add materialPrice + laborPrice to every TileSpecOption entry by deriving from
spec.Match keywords. Same labor numbers appear across all 5 room types per
the current config labels (正铺/正贴/菱铺/菱贴 all use 32/48/71/81/91/48).

Idempotent: skips entries that already have materialPrice + laborPrice set.
Re-run-safe.
"""
import json
import re
from pathlib import Path
import sys

CONFIG_FILES = [
    Path("BaoJiaCAD/config.json"),
    Path("BaoJiaCAD/bin/Debug/net48/config.json"),
]


# Match-keyword → (material, labor). 菱铺/菱贴 → 48 labor (matches spec label)
SIZE_PRICE = {
    "菱":     (28.0, 48.0),
    "300-800": (28.0, 32.0),
    "600*1200": (28.0, 48.0),
    "750*1500": (28.0, 71.0),
    "800*1600": (28.0, 81.0),
    "900*1800": (28.0, 91.0),
}


def derive_prices(match_list, label):
    if not match_list:
        return None
    # 菱铺/菱贴 are tagged by single keyword ("菱铺" or "菱贴") — check first
    if any("菱" in m for m in match_list if isinstance(m, str)):
        return SIZE_PRICE["菱"]
    for size_key in ("300-800", "600*1200", "750*1500", "800*1600", "900*1800"):
        if any(size_key in m for m in match_list if isinstance(m, str)):
            return SIZE_PRICE[size_key]
    # fallback: parse (人工N) from label
    m = re.search(r"\((\u4eba\u5de5|\u4eba\u5de5\s*)(\d+)\u5143?\)", label or "", re.UNICODE)
    if not m:
        m = re.search(r"\u4eba\u5de5(\d+)", label or "")
    return (28.0, float(m.group(1))) if m else None


def patch_one(path: Path) -> dict:
    cfg = json.loads(path.read_text(encoding="utf-8"))
    tso = cfg.get("TemplateSettings", {}).get("TileSpecOptions", {})
    summary = {"added": 0, "skipped": 0, "missing": []}
    for room, specs in tso.items():
        for spec in specs:
            label = spec.get("label", "")
            value = spec.get("value", "")
            if "materialPrice" in spec and "laborPrice" in spec:
                summary["skipped"] += 1
                continue
            prices = derive_prices(spec.get("match", []), label)
            if prices is None:
                summary["missing"].append(f"{room}/{value}")
                continue
            spec["materialPrice"] = prices[0]
            spec["laborPrice"] = prices[1]
            summary["added"] += 1
    path.write_text(json.dumps(cfg, ensure_ascii=False, indent=2), encoding="utf-8")
    return summary


def main():
    for p in CONFIG_FILES:
        if not p.exists():
            print(f"MISS {p}")
            continue
        s = patch_one(p)
        print(f"{p}  added={s['added']} skipped={s['skipped']} missing={s['missing']}")


if __name__ == "__main__":
    main()
