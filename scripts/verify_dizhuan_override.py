"""
Verify the new SINGLE+PHASE A price override logic for the dizhuan scenario.

Replicates BaoJiaCAD/ExcelExporter.cs:
  - IsFloorItem(config.TemplateSettings.FloorItemKeywords.Any(name.Contains))
  - IdentifyTileSpecMatch (ALL-hit + OrderByDescending)
  - PHASE A: in single-mode (floorItemCount<=1), override C5(材)+C7(人工) FROM selectedSpec prices,
    append 【已应用规格: <Label>】 marker to C9 (dedup via Regex.Replace).
  - Single-mode C3: FloorArea.
  - Multi-mode C3: blank if itemSpec!=selectedSpec.

Run: python scripts/verify_dizhuan_override.py
"""
import sys
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
import json
import re
from pathlib import Path
from collections import OrderedDict

CFG = json.loads(Path("BaoJiaCAD/config.json").read_text(encoding="utf-8"))
FLOOR_KW = CFG["TemplateSettings"]["FloorItemKeywords"]
TSO = CFG["TemplateSettings"]["TileSpecOptions"]
MARKER_RE = re.compile(r"\n?【已应用规格:.*?】", re.UNICODE)


def asc(s: str) -> str:
    return "".join(c if (32 <= ord(c) < 127) else f"\\u{ord(c):04x}" for c in str(s))


def is_floor_item(name: str) -> bool:
    return any(k in name for k in FLOOR_KW)


def identify_item_spec(item_name: str, room_type: str):
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


def phase_a_apply(item, room_type, selected_spec):
    """Replicates PHASE A. Returns dict of changes."""
    changes = {}
    if selected_spec is None:
        return changes
    specs = TSO.get(room_type) or []
    opt = next((s for s in specs if s["value"] == selected_spec), None)
    if opt is None:
        changes["debug"] = f"selectedSpec={selected_spec} 未在 config 找到, 模板价格保留"
        return changes
    if "materialPrice" in opt:
        changes["C5"] = opt["materialPrice"]
    if "laborPrice" in opt:
        changes["C7"] = opt["laborPrice"]
    if "C5" in changes or "C7" in changes:
        old_marker_free = MARKER_RE.sub("", item["C9"]).rstrip("\n\r")
        new_marker = f"【已应用规格: {opt.get('label', selected_spec)}】"
        new_c9 = new_marker if not old_marker_free else old_marker_free + "\n" + new_marker
        changes["C9"] = new_c9
    return changes


def fill_row(item, room, room_type, selected_spec, item_formulas_key=None):
    """Simulate the relevant foreach-iteration C3/C5/C7/C9 outcome."""
    room_floor = item.get("__floor", 18.0)
    # PHASE A
    phase_a = phase_a_apply(item, room_type, selected_spec)
    # indoor formula
    if item_formulas_key and item_formulas_key in (item.get("_formulas_map") or {}):
        return {"action": "indoor_formula", "C3": item["_formulas_map"][item_formulas_key], "phaseA": phase_a}
    # IsFloorItem
    if is_floor_item(item["Name"]):
        item_spec = identify_item_spec(item["Name"], room_type)
        if (item["__count_floor_in_group"] or 1) <= 1:
            # SINGLE: phase A already applied, just fill C3 = FloorArea
            return {"action": "single-mode-fill", "itemSpec": item_spec, "C3": room_floor, "phaseA": phase_a,
                    "note": f"count_floor={item.get('__count_floor_in_group')} selectedSpec={selected_spec!r}"}
        # MULTI: blank on mismatch
        if selected_spec is not None and item_spec is not None and item_spec != selected_spec:
            return {"action": "multi-blank", "itemSpec": item_spec, "selectedSpec": selected_spec, "C3": 0, "phaseA": phase_a}
        return {"action": "multi-keep", "itemSpec": item_spec, "C3": room_floor, "phaseA": phase_a}
    return {"action": "wall-or-other", "itemSpec": None, "phaseA": phase_a}


# -------------------------------------------------------------------
# SCENARIO BUILDER
# -------------------------------------------------------------------
def lbl(s):
    return asc(s) if isinstance(s, str) else s


def banner(t):
    print("\n" + "=" * 70 + f"\n  {t}\n" + "=" * 70)


def show(label, item, result, room_type, selected):
    print(f"  room={lbl(room_type)}  selectedSpec={lbl(selected) or '<none>'}")
    print(f"  row: name={lbl(item['Name'])}")
    print(f"    action  : {result['action']}")
    if result.get("itemSpec") is not None:
        print(f"    itemSpec: {lbl(result.get('itemSpec'))}")
    if "C3" in result:
        print(f"    C3 (qty): {result['C3']!r}")
    pa = result.get("phaseA") or {}
    if "C5" in pa:
        print(f"    C5 (mat): {pa['C5']!r}   <-- PHASE A override!")
    if "C7" in pa:
        print(f"    C7 (lab): {pa['C7']!r}   <-- PHASE A override!")
    if "C9" in pa:
        new_c9 = pa["C9"]
        print(f"    C9 (note + marker stripped):")
        print(f"      " + lbl(new_c9.split("\n")[-1]))
    if pa.get("debug"):
        print(f"    debug   : {lbl(pa['debug'])}")


# -------------------------------------------------------------------
# SCENARIOS
# -------------------------------------------------------------------
def s1():
    banner("S1: dizhuan 客**餐**厅 single floor row + selected sp750-1500 default")
    item = {"Name": "客\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09",
            "Unit": "M2", "C9": "\u5f3a\u531632.5MPa\u6c34\u6ce5\u6c99\u6444\u8bbe\u5907\u53ca\u4eba\u5de5\u8d39...",
            "__count_floor_in_group": 1}
    r = fill_row(item, None, "\u5ba2\u9910\u5385", "sp750-1500")
    print(f"  per-room floorItemCount = {item['__count_floor_in_group']}")
    show("S1", item, r, "\u5ba2\u9910\u5385", "sp750-1500")
    # Expected: SINGLE -> C5=28, C7=71, marker appended, C3=FloorArea
    exp_c5 = next((s["materialPrice"] for s in TSO["\u5ba2\u9910\u5385"] if s["value"] == "sp750-1500"), None)
    exp_c7 = next((s["laborPrice"] for s in TSO["\u5ba2\u9910\u5385"] if s["value"] == "sp750-1500"), None)
    print(f"  EXPECTED: action=single-mode-fill C5={exp_c5} C7={exp_c7} & \u3010\u5df2\u5e94\u7528 marker")
    ok = (r["action"] == "single-mode-fill" and r.get("phaseA", {}).get("C5") == exp_c5
          and r.get("phaseA", {}).get("C7") == exp_c7
          and "\u3010" in r.get("phaseA", {}).get("C9", ""))
    print(f"  PASS={ok}")


def s2():
    banner("S2: drift case -- selected sp750-1500-MBR (mb daemon) against 客**餐**厅  config")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09",
            "C9": "\u539f\u59cb\u63cf\u8ff0...",
            "__count_floor_in_group": 1}
    r = fill_row(item, None, "\u5ba2\u9910\u5385", "sp750-1500-MBR")
    show("S2", item, r, "\u5ba2\u9910\u5385", "sp750-1500-MBR")
    print(f"  EXPECTED: phaseA.debug \u274c not override")
    ok = (r["action"] == "single-mode-fill" and r.get("phaseA", {}).get("debug") is not None
          and "C5" not in r.get("phaseA", {}) and "C7" not in r.get("phaseA", {}))
    print(f"  PASS={ok}")


def s3():
    banner("S3: no selection")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09", "C9": "...",
            "__count_floor_in_group": 1}
    r = fill_row(item, None, "\u5ba2\u9910\u5385", None)
    show("S3", item, r, "\u5ba2\u9910\u5385", None)
    print(f"  EXPECTED: SINGLE no-override; C3 = FloorArea; NO marker")
    ok = r["action"] == "single-mode-fill" and "C5" not in r.get("phaseA", {}) and "C7" not in r.get("phaseA", {})
    print(f"  PASS={ok}")


def s4():
    banner("S4: \u53a8\u623f + selected sp300-800-K (default)")
    item = {"Name": "\u53a8\u623f\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "...",
            "__count_floor_in_group": 1}
    r = fill_row(item, None, "\u53a8\u623f", "sp300-800-K")
    show("S4", item, r, "\u53a8\u623f", "sp300-800-K")
    exp_c5 = next((s["materialPrice"] for s in TSO["\u53a8\u623f"] if s["value"] == "sp300-800-K"), None)
    exp_c7 = next((s["laborPrice"] for s in TSO["\u53a8\u623f"] if s["value"] == "sp300-800-K"), None)
    print(f"  EXPECTED: C5={exp_c5} C7={exp_c7}")
    ok = (r.get("phaseA", {}).get("C5") == exp_c5 and r.get("phaseA", {}).get("C7") == exp_c7)
    print(f"  PASS={ok}")


def s5():
    banner("S5: \u53a8\u623f + selected sp750-1500-K (size drift; row still says 300-800MM)")
    item = {"Name": "\u53a8\u623f\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "...",
            "__count_floor_in_group": 1}
    r = fill_row(item, None, "\u53a8\u623f", "sp750-1500-K")
    show("S5", item, r, "\u53a8\u623f", "sp750-1500-K")
    exp_c5 = next((s["materialPrice"] for s in TSO["\u53a8\u623f"] if s["value"] == "sp750-1500-K"), None)
    exp_c7 = next((s["laborPrice"] for s in TSO["\u53a8\u623f"] if s["value"] == "sp750-1500-K"), None)
    print(f"  EXPECTED: C5={exp_c5} C7={exp_c7} -- row NAME stays as 300-800MM but prices reflect 750*1500 pick")
    ok = (r.get("phaseA", {}).get("C5") == exp_c5 and r.get("phaseA", {}).get("C7") == exp_c7)
    print(f"  PASS={ok}")


def s6():
    banner("S6: zhubaojiao multi-variant group, select sp750-1500-LR, expect keep matching + blank others")
    items = [
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "...", "__count_floor_in_group": 6},
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08600*1200MM\uff09", "C9": "...", "__count_floor_in_group": 6},
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08750*1500MM\uff09", "C9": "...", "__count_floor_in_group": 6},
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08800*1600MM\uff09", "C9": "...", "__count_floor_in_group": 6},
        {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08900*1800MM\uff09", "C9": "...", "__count_floor_in_group": 6},
        {"Name": "\u83b1\u94fa\u5730\u7816\uff08300-800MM\uff09",                              "C9": "...", "__count_floor_in_group": 6},
    ]
    for it in items:
        r = fill_row(it, None, "\u5ba2\u9910\u5385", "sp750-1500-LR")
        exp_keep = "750*1500" in it["Name"]
        print(f"  row: {lbl(it['Name'])}")
        print(f"    action={r['action']}  itemSpec={lbl(r.get('itemSpec'))}  selectedSpec=sp750-1500-LR")
        if "C3" in r:
            print(f"    C3 = {r['C3']!r}")
        ok_keep = (r["action"] == "multi-keep" if exp_keep else r["action"] == "multi-blank")
        print(f"    {'\u2713 KEEP' if exp_keep and r['action']=='multi-keep' else '\u2713 BLANK' if not exp_keep and r['action']=='multi-blank' else '\u2717 WRONG'}")


def s7():
    banner("S7: row matches ItemFormulas + multi-mode group: indoor path wins, PHASE A does NOT run (group is multi)")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09", "C9": "...",
            "__count_floor_in_group": 6,
            "_formulas_map": {"\u5ba2\u5385\u53ca\u9910\u5385\u6b63\u94fa\u5730\u7816\uff08300-800MM\uff09": 12.34}}
    r = fill_row(item, None, "\u5ba2\u9910\u5385", "sp750-1500-LR", item_formulas_key=item["Name"])
    show("S7", item, r, "\u5ba2\u9910\u5385", "sp750-1500-LR")
    print(f"  EXPECTED: action=indoor_formula, C3=12.34, NO phaseA override (multi gate)")
    ok = (r["action"] == "indoor_formula" and r.get("C3") == 12.34
          and "C5" not in r.get("phaseA", {}))
    print(f"  PASS={ok}")


def s8():
    banner("S8: re-run with different spec -- marker de-dup via Regex.Replace")
    item = {"Name": "\u5ba2\u5385\u53ca\u9910\u5385\u94fa\u5730\u7816\uff08600*1200MM\uff09",
            "C9": "\u539f\u59cb\u63cf\u8ff0\n\u3010\u5df2\u5e94\u7528\u89c4\u683c: \u6b63\u94fa 300-800MM (\u4eba\u5de532)\u3011",
            "__count_floor_in_group": 1}
    # Round 1 already wrote marker; now run with sp750-1500
    r = fill_row(item, None, "\u5ba2\u9910\u5385", "sp750-1500")
    new_c9 = r.get("phaseA", {}).get("C9", "")
    n_markers_before = item["C9"].count("\u3010")
    n_markers_after = new_c9.count("\u3010")
    print(f"  before-run marker count: {n_markers_before}")
    print(f"  after-run marker count : {n_markers_after}")
    show("S8", item, r, "\u5ba2\u9910\u5385", "sp750-1500")
    print(f"  EXPECTED: exactly 1 marker in C9 (\u3010\u5df2\u5e94\u7528\u89c4\u683c: \u6b63\u94fa 750*1500MM\uff08\u4eba\u5de571\uff09\u3011); no leftover old marker")
    ok = (n_markers_after == 1 and "750*1500" in new_c9 and "300-800" not in new_c9)
    print(f"  PASS={ok}")


for fn in (s1, s2, s3, s4, s5, s6, s7, s8):
    fn()
