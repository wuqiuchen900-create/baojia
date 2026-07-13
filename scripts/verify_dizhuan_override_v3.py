"""
Verify the FULL PHASE A spec-override behaviour for the dizhuan template,
including the new C2 (item name) rewrite feature with diamond size fallback.

Replicates BaoJiaCAD/ExcelExporter.cs:
  - IsFloorItem(config.TemplateSettings.FloorItemKeywords.Any(name.Contains))
  - IdentifyTileSpecMatch (ALL-hit + OrderByDescending length+count)
  - PHASE A in single-mode (floorItemCount<=1):
      - rewrite C5 (material) + C7 (labor) from spec.MaterialPrice/LaborPrice
      - rewrite C2 (item name) via BuildSpecItemName helper (diamond falls back to Label regex)
      - append \u3010\u5df2\u5e94\u7528\u89c4\u683c: <Label>\u3011 to C9 (Regex strips historical markers)
  - Single-mode C3: FloorArea (no blanking)

Run: python scripts/verify_dizhuan_override_v3.py 2>&1 | tee verify_dizhuan_trace.txt
"""
import sys
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import json
import re
from pathlib import Path

CFG = json.loads(Path("BaoJiaCAD/config.json").read_text(encoding="utf-8"))
FLOOR_KW = CFG["TemplateSettings"]["FloorItemKeywords"]
TSO = CFG["TemplateSettings"]["TileSpecOptions"]
MARKER_RE = re.compile(r"\n?\u3010\u5df2\u5e94\u7528\u89c4\u683c:.*?\u3011", re.UNICODE)
LABEL_SIZE_RE = re.compile(r"([0-9]+(?:[\-\*][0-9]+)?MM)")


# ─────────────────────────────────────────────────────────────────
# Helpers replicating the C# code
# ─────────────────────────────────────────────────────────────────
def is_floor_item(name):
    return any(k in name for k in FLOOR_KW)


def identify_item_spec(item_name, room_type):
    specs = TSO.get(room_type) or []
    ordered = sorted(
        [s for s in specs if s.get("match")],
        key=lambda s: (sum(len(m or "") for m in s["match"]), len(s["match"])),
        reverse=True,
    )
    for s in ordered:
        if all(m in item_name for m in s["match"]):
            return s["value"]
    return None


def build_spec_item_name(opt):
    if not opt or not opt.get("match"):
        return None
    prefix = "\u94fa\u5730\u7816"  # 铺地砖
    size = None
    for m in opt["match"]:
        if not m:
            continue
        if "\u83f1\u94fa" in m:                  # 菱铺
            prefix = "\u83f1\u94fa\u5730\u7816"
        elif "\u83f1\u8d34" in m:                # 菱贴
            prefix = "\u83f1\u8d34\u5730\u7816"
        elif "\u6b63\u94fa" in m:                # 正铺
            prefix = "\u6b63\u94fa\u5730\u7816"
        elif "\u6b63\u8d34" in m:                # 正贴
            prefix = "\u6b63\u8d34\u5730\u7816"
        elif "\u94fa\u5730\u7816" in m or m == "\u5730\u7816" or m == "\u5730\u677f":
            prefix = "\u94fa\u5730\u7816"
        else:
            size = m
    if prefix.startswith("\u83f1") and not size and opt.get("label"):
        m = LABEL_SIZE_RE.search(opt["label"])
        if m:
            size = m.group(1)
    if not size:
        return prefix
    swu = size if size.endswith("MM") else size + "MM"
    return f"{prefix}\uff08{swu}\uff09"  # full-width （）


def phase_a_apply(item, room_type, selected_spec):
    changes = {}
    if not selected_spec:
        return changes
    specs = TSO.get(room_type) or []
    opt = next((s for s in specs if s["value"] == selected_spec), None)
    if not opt:
        changes["debug"] = f"selectedSpec={selected_spec} \u672a\u5728 config \u627e\u5230"
        return changes
    if "materialPrice" in opt:
        changes["C5"] = opt["materialPrice"]
    if "laborPrice" in opt:
        changes["C7"] = opt["laborPrice"]
    # NEW: C2 rewrite
    new_name = build_spec_item_name(opt)
    if new_name and (item.get("Name") or "") != new_name:
        changes["C2"] = new_name
    if "C5" in changes or "C7" in changes or "C2" in changes:
        old = MARKER_RE.sub("", item.get("C9") or "").rstrip("\n\r")
        marker = f"\u3010\u5df2\u5e94\u7528\u89c4\u683c: {opt.get('label', selected_spec)}\u3011"
        changes["C9"] = marker if not old else old + "\n" + marker
    return changes


# 🔧 v5 sim-mirror: 与 C# BaoJiaCAD/ExcelExporter.cs 的 isTileishName 保持同一组地砖关键词.
#   在 floorItemCount==0 fallback 防 mudiban 木地板被误改为地砖+价格. 「地板」被故意忽掉.
TILEISH_KEYWORDS = [
    "\u5730\u7816",  # 地砖
    "\u6b63\u94fa",  # 正铺
    "\u6b63\u8d34",  # 正贴
    "\u83f1\u94fa",  # 菱铺
    "\u83f1\u8d34",  # 菱贴
]


def is_tileish_name(name):
    return bool(name) and any(k in name for k in TILEISH_KEYWORDS)


def fill_row(item, room_type, selected_spec, *, group_items=None, item_formulas_key=None):
    # 🔧 v4 fix sim: 真实镜像 C# pre-foreach 计算 — count = 真正能被某个 spec 评中的 floor 行数.
    # 强化地板/成品保护 Row (IsFloorItem=true 但 itemSpec=null) 不贡献 count.
    if group_items is not None:
        count = sum(
            1 for it in group_items
            if is_floor_item(it.get("Name", ""))
            and identify_item_spec(it.get("Name", ""), room_type) is not None
        )
    else:
        count = item.get("__count_floor_in_group", 1) or 1
    # 🔧 v4 另加 防御: isExactSpecRow 免 强化地板/成品保护 被颛 误跳 进 override;
    #   floorItemCount==0 适身 通用单行模板 (item.Name 不 unmendable spec).
    is_exact = identify_item_spec(item.get("Name", ""), room_type) is not None
    # 🔧 C# FillRoomData foreach runs PHASE A 与 indoor_formula 连续: PHASE A 一直 仅写 C5/C7/C2/C9,
    #     不收生变 C3. 后续 hasIndoor 匹配 → C3 = 公式, continue. 所以 PHASE A 看见了不该 early-return.
    #    本 sim 之前的 fill_row 单规格分支 early-return 抢走 C3 权利, 造成 S7 误报. 现在去除 early-return.
    name = item.get("Name", "")
    phaseA = {}
    if (is_floor_item(name)
            and count <= 1
            and bool(selected_spec)
            and (is_exact or (count == 0 and is_tileish_name(name)))):
        phaseA = phase_a_apply(item, room_type, selected_spec)
    # C3 解析 全镜像 C# foreach 顺序: indoor_formula > outdoor_formula > floor area / wall area / multi mód.
    if item_formulas_key and (item.get("_formulas_map") or {}).get(item_formulas_key) is not None:
        return {"action": "indoor_formula", "phaseA": phaseA,
                "C3": item["_formulas_map"][item_formulas_key]}
    if is_floor_item(item["Name"]):
        if count >= 2:
            item_spec = identify_item_spec(item["Name"], room_type)
            if selected_spec and item_spec and item_spec != selected_spec:
                return {"action": "multi-blank", "phaseA": phaseA, "C3": 0}
            return {"action": "multi-keep", "phaseA": phaseA, "C3": item.get("__floor", 18.0)}
        return {"action": "single-mode-fill", "phaseA": phaseA, "C3": item.get("__floor", 18.0)}
    return {"action": "wall-or-other", "phaseA": phaseA}


# ─────────────────────────────────────────────────────────────────
# Scenario runner
# ─────────────────────────────────────────────────────────────────
def banner(t):
    print("\n" + "=" * 70 + f"\n  {t}\n" + "=" * 70)


def show(item, result, room_type, selected):
    pa = result.get("phaseA") or {}
    print(f"    room={room_type}  selectedSpec={selected or '<none>'}")
    print(f"    row orig name : {item['Name']}")
    if "C2" in pa:
        print(f"    C2 -> NEW      : {pa['C2']}")
    if "C5" in pa:
        print(f"    C5 (mat)      : {pa['C5']}")
    if "C7" in pa:
        print(f"    C7 (lab)      : {pa['C7']}")
    if "C9" in pa:
        print(f"    C9 last line  : {pa['C9'].split(chr(10))[-1]}")
    if pa.get("debug"):
        print(f"    debug         : {pa['debug']}")
    if "C3" in result:
        print(f"    C3 (qty)      : {result['C3']}")


def diff_c2(name):
    """Returns the new C2 expected when phase_a applies via sim spec."""
    return name


def check(label, ok, expected):
    tag = "PASS" if ok else "FAIL"
    print(f"    -> {tag} {label}")
    if not ok:
        print(f"       EXPECTED: {expected}")


# ─────────────────────────────────────────────────────────────────
# Scenarios
# ─────────────────────────────────────────────────────────────────
def s1():
    banner("S1: dizhuan \u5ba2\u9910\u5385 single row + selected sp750-1500 (default)")
    item = {"Name": "\u5ba2\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09",
            "C9": "\u539f\u59cb\u63cf\u8ff0..."}
    r = fill_row(item, "\u5ba2\u9910\u5385", "sp750-1500", group_items=[item])
    show(item, r, "\u5ba2\u9910\u5385", "sp750-1500")
    exp_c2 = "\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09"
    ok = (r.get("phaseA", {}).get("C2") == exp_c2
          and r.get("phaseA", {}).get("C5") == 28
          and r.get("phaseA", {}).get("C7") == 71)
    check("C2/C5/C7", ok, f"C2={exp_c2} C5=28 C7=71")


def s2():
    banner("S2: 客**餐**厅 drift selectedSpec=sp750-1500-MBR (no opt)")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09",
            "C9": "..."}
    r = fill_row(item, "\u5ba2\u9910\u5385", "sp750-1500-MBR", group_items=[item])
    show(item, r, "\u5ba2\u9910\u5385", "sp750-1500-MBR")
    pa = r.get("phaseA", {})
    ok = (pa.get("debug") is not None and "C5" not in pa and "C7" not in pa and "C2" not in pa)
    check("no override", ok, "phaseA.debug + no C2/C5/C7 changes")


def s3():
    banner("S3: 客**餐**厅 no selection")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09", "C9": "..."}
    r = fill_row(item, "\u5ba2\u9910\u5385", None, group_items=[item])
    show(item, r, "\u5ba2\u9910\u5385", None)
    pa = r.get("phaseA", {})
    ok = (pa == {})
    check("no phase A", ok, "phaseA empty, C3=FloorArea")


def s4():
    banner("S4: \u53a8\u623f + selected sp300-800-K (default)")
    item = {"Name": "\u53a8\u623f\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "..."}
    r = fill_row(item, "\u53a8\u623f", "sp300-800-K", group_items=[item])
    show(item, r, "\u53a8\u623f", "sp300-800-K")
    exp_c2 = "\u6b63\u8d34\u5730\u7816\uff08300-800MM\uff09"
    ok = (r.get("phaseA", {}).get("C2") == exp_c2
          and r.get("phaseA", {}).get("C5") == 28
          and r.get("phaseA", {}).get("C7") == 32)
    check("C2 + prices", ok, f"C2={exp_c2} C5=28 C7=32")


def s5():
    banner("S5: \u53a8\u623f + selected sp750-1500-K (size drift)")
    item = {"Name": "\u53a8\u623f\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "..."}
    r = fill_row(item, "\u53a8\u623f", "sp750-1500-K", group_items=[item])
    show(item, r, "\u53a8\u623f", "sp750-1500-K")
    exp_c2 = "\u6b63\u8d34\u5730\u7816\uff08750*1500MM\uff09"
    ok = (r.get("phaseA", {}).get("C2") == exp_c2 and r.get("phaseA", {}).get("C7") == 71)
    check("C2 rewrite + price", ok, f"C2={exp_c2} C7=71")


def s6():
    banner("S6: zhubaojiao multi-variant group (count>=2 so PHASE A does NOT run)")
    items = [
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "..."},
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09", "C9": "..."},
        {"Name": "\u83f1\u94fa\u5730\u7816\uff08300-800MM\uff09",                              "C9": "..."},
    ]
    fail_count = 0
    for it in items:
        r = fill_row(it, "\u5ba2\u9910\u5385", "sp750-1500", group_items=items)
        show(it, r, "\u5ba2\u9910\u5385", "sp750-1500")
        pa = r.get("phaseA", {})
        # Multi-mode: PHASE A skipped (count>1), so C2 unchanged
        if "C2" in pa:
            print("    -> FAIL C2 changed in multi mode!!")
            fail_count += 1
        else:
            ok_keep = it["Name"].endswith("\uff08750*1500MM\uff09") and r["action"] == "multi-keep"
            ok_blank = not it["Name"].endswith("\uff08750*1500MM\uff09") and r["action"] == "multi-blank"
            print(f"    -> {'PASS KEEP' if ok_keep else 'PASS BLANK' if ok_blank else 'FAIL wrong'}")
            if not ok_keep and not ok_blank:
                fail_count += 1
    print(f"  S6 fail_count={fail_count}")


def s7():
    banner("S7: floorItemCount<=1 + ItemFormulas: PHASE A fires for C5/C7/C2, indoor_formula wins for C3 only")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "...",
            "_formulas_map": {"\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09": 12.34}}
    r = fill_row(item, "\u5ba2\u9910\u5385", "sp750-1500", group_items=[item],
                 item_formulas_key=item["Name"])
    show(item, r, "\u5ba2\u9910\u5385", "sp750-1500")
    pa = r.get("phaseA", {})
    # PHASE A runs FIRST (C5/C7 + C2 override). Indoor_formula runs AFTER for this row (C3 only).
    # By design: prices+name always reflect panel selection regardless of formulas.
    ok = (r["action"] == "indoor_formula"
          and r.get("C3") == 12.34
          and pa.get("C5") == 28
          and pa.get("C7") == 71
          and pa.get("C2") == "\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09")
    check("indoor C3 + PHASE A C5/C7/C2", ok,
          "C3=12.34, C5=28 C7=71 C2=\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09")


def s8():
    banner("S8: re-run with different spec -- marker de-dup, C2 rewrites correctly")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09",
            "C9": "\u539f\u59cb\u63cf\u8ff0\n\u3010\u5df2\u5e94\u7528\u89c4\u683c: \u6b63\u94fa 300-800MM (\u4eba\u5de532)\u3011"}
    r = fill_row(item, "\u5ba2\u9910\u5385", "sp750-1500", group_items=[item])
    pa = r.get("phaseA", {})
    new_c9 = pa.get("C9", "")
    n_markers_before = item["C9"].count("\u3010")
    n_markers_after = new_c9.count("\u3010")
    print(f"    before-run marker count: {n_markers_before}")
    print(f"    after-run marker count : {n_markers_after}")
    show(item, r, "\u5ba2\u9910\u5385", "sp750-1500")
    ok = (n_markers_after == 1
          and pa.get("C2") == "\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09"
          and "300-800" not in new_c9)
    check("single marker + new C2", ok, "exactly 1 marker, C2=正铺地砖（750*1500MM）")


def s9():
    banner("S9: diamond spec spDiamond-LR (Match=[\u83f1\u94fa], no size in Match) -> Label fallback")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08300-800MM\uff09",
            "C9": "..."}
    r = fill_row(item, "\u5ba2\u9910\u5385", "spDiamond-LR", group_items=[item])
    show(item, r, "\u5ba2\u9910\u5385", "spDiamond-LR")
    exp_c2 = "\u83f1\u94fa\u5730\u7816\uff08300-800MM\uff09"
    ok = (r.get("phaseA", {}).get("C2") == exp_c2
          and r.get("phaseA", {}).get("C5") == 28
          and r.get("phaseA", {}).get("C7") == 48)
    check("diamond with size from Label", ok, f"C2={exp_c2} C5=28 C7=48")


def s10():
    banner("S10: diamond spec spDiamond-K (\u53a8\u623f \u83f1\u8d34)")
    item = {"Name": "\u53a8\u623f\u94fa\u5730\u7816\uff08300-800MM\uff09",
            "C9": "..."}
    r = fill_row(item, "\u53a8\u623f", "spDiamond-K", group_items=[item])
    show(item, r, "\u53a8\u623f", "spDiamond-K")
    exp_c2 = "\u83f1\u8d34\u5730\u7816\uff08300-800MM\uff09"
    ok = (r.get("phaseA", {}).get("C2") == exp_c2
          and r.get("phaseA", {}).get("C7") == 48)
    check("diamond with size from Label", ok, f"C2={exp_c2} C7=48")


def s11():
    """v4 regression-guard: dizhuan-like multi-IsFloorItem group.
    Three IsFloorItem rows (强化地板 / 正铺地砖 / 成品保护) but only ONE TileSpec-eligible (正铺地砖).
    Only 正铺地砖 should enter PHASE A; the other two are IsFloorItem=true but itemSpec=null so
    isExactSpecRow=false gate keeps them out of the override.
    """
    banner("S11: REGRESSION-GUARD \u2014 multi-IsFloorItem rows in dizhuan, only tile row overridden")
    rows = [
        {"Name": "\u5f3a\u5316\u5730\u677f",  # 强化地板 (matches FloorKeywords 「地板」)
         "C5": 70, "C7": 25, "C9": "...", "expected_C5": 70, "expected_C7": 25, "_formulas_map": {}},
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08600*1200MM\uff09",  # \u5339 sp600-1200-LR
         "C5": 28, "C7": 48, "C9": "...", "expected_C5": 28, "expected_C7": 71, "expected_C2": "\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09", "_formulas_map": {}},
        {"Name": "\u6210\u54c1\u4fdd\u62a4",  # 成品保护 (matches FloorKeywords 「成品保护」)
         "C5": 12, "C7": 5, "C9": "...", "expected_C5": 12, "expected_C7": 5, "_formulas_map": {}},
    ]
    fail_count = 0
    for r_dict in rows:
        r_dict["__floor"] = 18.0
        result = fill_row(r_dict, "\u5ba2\u9910\u5385", "sp750-1500", group_items=rows)
        pa = result.get("phaseA", {})
        is_actual_override = "C5" in pa or "C2" in pa  # presence of any phase A change
        print(f"  row name={r_dict['Name']!r}")
        print(f"    expected_C5={r_dict['expected_C5']!r}  expected_C7={r_dict['expected_C7']!r}"
              + (f"  expected_C2={'C2 overridden' if r_dict.get('expected_C2') else 'C2 unchanged'}"))
        ok_override_correct = (is_actual_override == bool(r_dict.get("expected_C2")))
        ok_price_match = True
        if r_dict.get("expected_C2"):
            ok_price_match = (pa.get("C5") == r_dict["expected_C5"] and pa.get("C7") == r_dict["expected_C7"])
        ok = ok_override_correct and ok_price_match
        if not ok:
            fail_count += 1
            print(f"    -> FAIL! actual: phaseA={pa}")
        else:
            tag = "OVERRIDE" if is_actual_override else "SKIPPED"
            print(f"    -> PASS ({tag} \u2014 {'touched C5/C7/C2' if is_actual_override else 'template values preserved'})")
    print(f"  S11 fail_count={fail_count}  expected=0")


def s12():
    """v5 regression-guard: mudiban bedroom-like group with only 地面找平 / 自流平 / 成品保护 rows.
    None of these rows contain '地砖' / 正铺 / 正贴 / 菱铺 / 菱贴 keyword → floorItemCount==0 in v5.
    AND isTileishName=false → v5 fallback (floorItemCount==0 && isTileishName) → FALSE → PHASE A skipped.
    All rows must keep their template values (C5/C7/C2 unchanged). v4 logic would have misfired here.
    """
    banner("S12: REGRESSION-GUARD v5 — mudiban bedrooms (地面找平/自流平/成品保护) keep template values, NOT rewritten")
    rows = [
        {"Name": "\u5730\u9762\u627e\u5e73",  # 地面找平 (IsFloorItem=true via 地面找平 keyword)
         "C5": 14, "C7": 9, "C9": "\u539f\u6587\u672c\u627e\u5e73\u8bf4\u660e...", "expected_C5": 14, "expected_C7": 9, "_formulas_map": {}},
        {"Name": "\u81ea\u6d41\u5e73",          # 自流平 (IsFloorItem=true via 自流平 keyword)
         "C5": 18, "C7": 12, "C9": "..."  , "expected_C5": 18, "expected_C7": 12, "_formulas_map": {}},
        {"Name": "\u6210\u54c1\u4fdd\u62a4",   # 成品保护 (IsFloorItem=true via 成品保护 keyword)
         "C5": 8,  "C7": 4,    "C9": "...",   "expected_C5": 8,  "expected_C7": 4,    "_formulas_map": {}},
    ]
    fail_count = 0
    for r_dict in rows:
        r_dict["__floor"] = 18.0
        result = fill_row(r_dict, "\u5367\u5ba4", "sp300-800-BR", group_items=rows)
        pa = result.get("phaseA", {})
        is_actual_override = "C5" in pa or "C2" in pa
        print(f"  row name={r_dict['Name']!r}")
        print(f"    expected_C5={r_dict['expected_C5']!r}  expected_C7={r_dict['expected_C7']!r}  expected_C2=C2 unchanged")
        ok = (is_actual_override is False)
        if not ok:
            fail_count += 1
            print(f"    -> FAIL! actual phaseA={pa} (mudiban non-tile row got rewritten!)")
        else:
            print(f"    -> PASS (SKIPPED — mudiban non-tile row preserved, no v4-style misfire)")
    print(f"  S12 fail_count={fail_count}  expected=0")


for fn in (s1, s2, s3, s4, s5, s6, s7, s8, s9, s10, s11, s12):
    fn()
