"""JSON-aware patch:
从备份 config_bak_20260718_165538.json 里抽出 TemplateSettings.TileSpecOptions.客餐厅,
写到 bin/Debug/net48/config.json — 不动其他字段 (QuoteItems/RoomTypeFallbackMap/字段
大小写 等用户后续手动改过的内容都会保留)。
"""
import json
import sys
from pathlib import Path

ROOT = Path(r"E:\xiangmu\baojia")
CUR  = ROOT / "BaoJiaCAD/bin/Debug/net48/config.json"
BAK  = ROOT / "BaoJiaCAD/bin/Debug/net48/config_bak_20260718_165538.json"


def main() -> int:
    if not CUR.exists():
        print(f"[ERR] current not found: {CUR}", file=sys.stderr)
        return 2
    if not BAK.exists():
        print(f"[ERR] backup not found: {BAK}", file=sys.stderr)
        return 2

    cur = json.loads(CUR.read_text(encoding="utf-8"))
    bak = json.loads(BAK.read_text(encoding="utf-8"))

    cur_ts  = cur.get("TemplateSettings") or {}
    bak_ts  = bak.get("TemplateSettings") or {}
    cur_tso = cur_ts.get("TileSpecOptions") or {}
    bak_tso = bak_ts.get("TileSpecOptions") or {}

    k = "客餐厅"
    if k not in bak_tso:
        print(f"[ERR] backup has no TileSpecOptions.{k}", file=sys.stderr)
        return 3

    backup_list = bak_tso[k] or []
    if not isinstance(backup_list, list) or len(backup_list) < 1:
        print(f"[ERR] backup TileSpecOptions.{k} not a non-empty list", file=sys.stderr)
        return 4

    before_count = len(cur_tso.get(k) or [])
    cur_tso[k] = backup_list                    # overwrite
    cur_ts["TileSpecOptions"] = cur_tso
    cur["TemplateSettings"] = cur_ts

    # Pretty-print 缩进 2 空格 + 保留中文不转义 (保持当前文件风格)
    out = json.dumps(cur, ensure_ascii=False, indent=2)
    CUR.write_text(out + "\n", encoding="utf-8")

    after_count = len(cur_tso[k])
    print(f"[OK] TileSpecOptions.\u5ba2\u9910\u5385: {before_count} \u9879 \u2192 {after_count} \u9879")
    print(f"[OK] wrote: {CUR}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
