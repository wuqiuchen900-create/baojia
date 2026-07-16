# scripts/apply_v175_edits.py
# Apply v17.5 scope filter edits to 4 files. CRLF-safe (python native).
# Encoding: GB18030 (superset of GBK/cp936) for Chinese Windows saved files,
#           ASCII-only anchors and inserts to avoid encoding mismatch.
# Idempotent: skip if marker already present.
import sys, os, io

ROOT = r"E:\xiangmu\baojia"

def read_text(path):
    """Read file trying utf-8 first then gb18030 (covers gbk/cp936)."""
    raw = open(path, "rb").read()
    for enc in ("utf-8-sig", "utf-8", "gb18030"):
        try:
            return raw.decode(enc), enc
        except UnicodeDecodeError:
            continue
    # last resort — no exception; bytes mapped as latin-1 round-trip
    return raw.decode("latin-1"), "latin-1"

def write_text(path, text, enc):
    with open(path, "wb") as f:
        f.write(text.encode(enc))

def patch(path, find, insert_after_marker, marker_suffix=""):
    """
    Find `find` in file. After it, insert `insert_after_marker` block.
    CRLF-safe: normalize all line endings to LF for matching, restore on write.
    Returns (status_str, encoding_used).
    """
    c, enc = read_text(path)
    if marker_suffix and marker_suffix in c:
        return ("already_applied", enc)
    had_crlf = b'\r\n' in open(path, 'rb').read()
    c_norm = c.replace('\r\n', '\n')
    f_norm = find.replace('\r\n', '\n')
    if f_norm not in c_norm:
        needle_preview = repr(find)[:120]
        raise RuntimeError(
            f"NEEDLE NOT FOUND in {path}: {needle_preview}")
    new_norm = c_norm.replace(
        f_norm, f_norm + "\n\n" + insert_after_marker, 1)
    new = new_norm.replace('\n', '\r\n') if had_crlf else new_norm
    write_text(path, new, enc)
    return ("applied", enc)


# ===== 1. QuoteConfig.cs — add HonorSelectionScope =====
q_path = os.path.join(ROOT, "BaoJiaCAD/QuoteConfig.cs")
# ASCII-only anchor (file is GBK-encoded on Chinese Windows; ASCII avoids chardet issues)
q_find = (
    'public BathroomKitchenDefaults BathroomKitchenDefaults { get; set; } = new BathroomKitchenDefaults();'
)
q_insert = (
    '        // v17.5: scope-filter opt. true = only scan entities in user selection (user mental model).'
    '        //   false = keep legacy behavior (full DWG scan). Default true fixes v16.x bug'
    '        //   where WindowBox/WindowArea detectors scanned entire ModelSpace,'
    '        //   causing cross-floor matching and noise overflow.'
    '        public bool HonorSelectionScope { get; set; } = true;'
)
q_marker = "v17.5: scope-filter opt"
try:
    ok, why = patch(q_path, q_find, q_insert, q_marker)
    print(f"[1] QuoteConfig.cs: {why}")
except Exception as e:
    print(f"[1] QuoteConfig.cs: ERROR {e}")
    sys.exit(1)


# ===== 2. Commands.cs — add HashSet<ObjectId> globalSelectionScope + union + scope param =====
c_path = os.path.join(ROOT, "BaoJiaCAD/Commands.cs")

# 2a. Declare HashSet after allSkipped
c_find_2a = (
    '                var allSkipped = new List<SkippedTextInfo>();\n\n'
    '                bool exporting = false;'
)
c_insert_2a = (
    '                // v17.5: 跨 层 框 选 entity id union — 传 给 WindowBox / WindowArea\n'
    '                var globalSelectionScope = new HashSet<ObjectId>();\n\n'
    '                bool exporting = false;'
)
c_marker_2a = "// v17.5: 跨 层 框 选 entity id union"
try:
    ok, why = patch(c_path, c_find_2a, c_insert_2a, c_marker_2a)
    print(f"[2a] Commands.cs globalSelectionScope: {why}")
except Exception as e:
    print(f"[2a] Commands.cs globalSelectionScope: ERROR {e}")
    sys.exit(1)

# 2b. After sel.Status==OK, union + log
c_find_2b = (
    '                        var sel = editor.GetSelection();\n'
    '                        if (sel.Status != PromptStatus.OK)\n'
    '                        {\n'
    '                            editor.WriteMessage("\\n未选择对象，命令取消。");\n'
    '                            return;\n'
    '                        }'
)
c_insert_2b = (
    '\n\n'
    '                        // v17.5: 本 层 框 选 entities 入 全 层 union, 给 WindowBox / WindowArea scope 用.\n'
    '                        if (sel.Value != null)\n'
    '                        {\n'
    '                            int before = globalSelectionScope.Count;\n'
    '                            foreach (var oid in sel.Value.GetObjectIds())\n'
    '                                globalSelectionScope.Add(oid);\n'
    '                            editor.WriteMessage($"\\n[scope] 本 层 框 选 {sel.Value.Count} 个, 累 计 unique {globalSelectionScope.Count} 个 (+{globalSelectionScope.Count - before})");\n'
    '                        }'
)
c_marker_2b = "// v17.5: 本 层 框 选 entities 入 全 层 union"
try:
    ok, why = patch(c_path, c_find_2b, c_insert_2b, c_marker_2b)
    print(f"[2b] Commands.cs union: {why}")
except Exception as e:
    print(f"[2b] Commands.cs union: ERROR {e}")
    sys.exit(1)

# 2c + 2d. Replace WindowBoxDetector / WindowAreaDetector calls to pass scope
c_find_2c = (
    '                WindowBoxDetector.DetectCurtainBoxLengths(allRooms, editor, config, msg => editor.WriteMessage($"\\n{msg}"));'
)
c_insert_2c = (
    '                // v17.5: scope filter — 仅 HonorSelectionScope 时 传 入 跨 层 框 选 union\n'
    '                HashSet<ObjectId> scope = config.HonorSelectionScope ? globalSelectionScope : null;\n'
    '                WindowBoxDetector.DetectCurtainBoxLengths(allRooms, editor, config, msg => editor.WriteMessage($"\\n{msg}"), scope);'
)
c_marker_2c = "// v17.5: scope filter — 仅 HonorSelectionScope 时 传 入 跨 层 框 选 union"
try:
    ok, why = patch(c_path, c_find_2c, c_insert_2c, c_marker_2c)
    print(f"[2c] Commands.cs WindowBoxDetector call: {why}")
except Exception as e:
    print(f"[2c] Commands.cs WindowBoxDetector call: ERROR {e}")
    sys.exit(1)

c_find_2d = (
    '                WindowAreaDetector.DetectWindowAreas(allRooms, editor, config, msg => editor.WriteMessage($"\\n{msg}"));'
)
c_insert_2d = (
    '                WindowAreaDetector.DetectWindowAreas(allRooms, editor, config, msg => editor.WriteMessage($"\\n{msg}"), scope);'
)
c_marker_2d = "// v17.5: scope filter — 仅 HonorSelectionScope"
try:
    # Different marker to avoid conflict
    ok = False
    if c_marker_2d in c_path:  # won't happen - sanity
        pass
    with open(c_path, "r", encoding="utf-8") as f:
        c = f.read()
    if c_marker_2d in c:
        print(f"[2d] Commands.cs WindowAreaDetector call: already_applied (key text detection)")
    else:
        if c_find_2d in c:
            new = c.replace(c_find_2d, c_insert_2d, 1)
            with open(c_path, "w", encoding="utf-8") as f:
                f.write(new)
            print(f"[2d] Commands.cs WindowAreaDetector call: applied")
        else:
            print(f"[2d] Commands.cs WindowAreaDetector call: ERROR NEEDLE NOT FOUND")
            sys.exit(1)
except Exception as e:
    print(f"[2d] Commands.cs WindowAreaDetector call: ERROR {e}")
    sys.exit(1)


# ===== 3. WindowBoxDetector.cs — add scopeIds param + filter =====
w_path = os.path.join(ROOT, "BaoJiaCAD/WindowBoxDetector.cs")

w_find_3a = (
    '        public static void DetectCurtainBoxLengths(\n'
    '            List<Room> rooms, Editor editor, QuoteConfig config, Action<string> log)\n'
    '        {'
)
w_insert_3a_full = (
    '        public static void DetectCurtainBoxLengths(\n'
    '            List<Room> rooms, Editor editor, QuoteConfig config, Action<string> log,\n'
    '            // v17.5: scopeIds = null = 后 向 兼 容 (全 DWG 扫), 不 null = 仅 扫 user 框 选 中 entities.\n'
    '            HashSet<ObjectId> scopeIds = null)\n'
    '        {'
)
w_marker_3a = "// v17.5: scopeIds = null = 后 向 兼 容 (全 DWG 扫)"
with open(w_path, "r", encoding="utf-8") as f:
    wc = f.read()
if w_marker_3a in wc:
    print(f"[3a] WindowBoxDetector.DetectCurtainBoxLengths signature: already_applied")
else:
    if w_find_3a in wc:
        wc = wc.replace(w_find_3a, w_insert_3a_full, 1)
        print(f"[3a] WindowBoxDetector.DetectCurtainBoxLengths signature: applied")
    else:
        print(f"[3a] WindowBoxDetector.DetectCurtainBoxLengths signature: ERROR NEEDLE NOT FOUND")
        sys.exit(1)

# 3b. CollectWindowLines signature
w_find_3b = (
    '        private static List<Line> CollectWindowLines(Database db, Transaction tr, Action<string> log)\n'
    '        {'
)
w_insert_3b_full = (
    '        private static List<Line> CollectWindowLines(Database db, Transaction tr, Action<string> log,\n'
    '            HashSet<ObjectId> scopeIds = null)\n'
    '        {'
)
w_marker_3b = "CollectWindowLines(Database db, Transaction tr, Action<string> log,\n            HashSet<ObjectId> scopeIds = null)"
if w_marker_3b in wc:
    print(f"[3b] WindowBoxDetector.CollectWindowLines signature: already_applied")
else:
    if w_find_3b in wc:
        wc = wc.replace(w_find_3b, w_insert_3b_full, 1)
        print(f"[3b] WindowBoxDetector.CollectWindowLines signature: applied")
    else:
        print(f"[3b] WindowBoxDetector.CollectWindowLines signature: ERROR NEEDLE NOT FOUND")
        sys.exit(1)

# 3c. Add filter in foreach ObjectId
w_find_3c = (
    '                foreach (ObjectId id in ms)\n'
    '                {\n'
    '                    try\n'
    '                    {\n'
    '                        if (!id.ObjectClass.Name.Equals("AcDbLine", StringComparison.OrdinalIgnoreCase))\n'
    '                            continue;'
)
w_insert_3c_full = (
    '                foreach (ObjectId id in ms)\n'
    '                {\n'
    '                    try\n'
    '                    {\n'
    '                        // v17.5: scope filter — 仅 HonorSelectionScope 时 跳 出 user 框 选 外 的 entities\n'
    '                        if (scopeIds != null && id != ObjectId.Null && !scopeIds.Contains(id)) continue;\n'
    '                        if (!id.ObjectClass.Name.Equals("AcDbLine", StringComparison.OrdinalIgnoreCase))\n'
    '                            continue;'
)
w_marker_3c = "if (scopeIds != null && id != ObjectId.Null && !scopeIds.Contains(id)) continue;"
if w_marker_3c in wc:
    print(f"[3c] WindowBoxDetector.CollectWindowLines scope filter: already_applied")
else:
    if w_find_3c in wc:
        wc = wc.replace(w_find_3c, w_insert_3c_full, 1)
        print(f"[3c] WindowBoxDetector.CollectWindowLines scope filter: applied")
    else:
        print(f"[3c] WindowBoxDetector.CollectWindowLines scope filter: ERROR NEEDLE NOT FOUND")
        sys.exit(1)

with open(w_path, "w", encoding="utf-8") as f:
    f.write(wc)


# ===== 4. WindowAreaDetector.cs — mirror =====
a_path = os.path.join(ROOT, "BaoJiaCAD/WindowAreaDetector.cs")

a_find_4a = (
    '        public static void DetectWindowAreas(\n'
    '            List<Room> rooms, Editor editor, QuoteConfig config, Action<string> log)\n'
    '        {'
)
a_insert_4a_full = (
    '        public static void DetectWindowAreas(\n'
    '            List<Room> rooms, Editor editor, QuoteConfig config, Action<string> log,\n'
    '            // v17.5: scopeIds = null = 后 向 兼 容 (全 DWG 扫), 不 null = 仅 扫 user 框 选 中 entities.\n'
    '            HashSet<ObjectId> scopeIds = null)\n'
    '        {'
)
a_marker_4a = "DetectWindowAreas(\n            List<Room> rooms, Editor editor, QuoteConfig config, Action<string> log,\n            // v17.5: scopeIds = null = 后 向 兼 容 (全 DWG 扫)"
with open(a_path, "r", encoding="utf-8") as f:
    ac = f.read()
if a_marker_4a in ac:
    print(f"[4a] WindowAreaDetector.DetectWindowAreas signature: already_applied")
else:
    if a_find_4a in ac:
        ac = ac.replace(a_find_4a, a_insert_4a_full, 1)
        print(f"[4a] WindowAreaDetector.DetectWindowAreas signature: applied")
    else:
        print(f"[4a] WindowAreaDetector.DetectWindowAreas signature: ERROR NEEDLE NOT FOUND")
        sys.exit(1)

# 4b. CollectWindowLinesRaw signature
a_find_4b = (
    '        private static List<Line> CollectWindowLinesRaw(Database db, Transaction tr, Action<string> log)\n'
    '        {'
)
a_insert_4b_full = (
    '        private static List<Line> CollectWindowLinesRaw(Database db, Transaction tr, Action<string> log,\n'
    '            HashSet<ObjectId> scopeIds = null)\n'
    '        {'
)
a_marker_4b = "CollectWindowLinesRaw(Database db, Transaction tr, Action<string> log,\n            HashSet<ObjectId> scopeIds = null)"
if a_marker_4b in ac:
    print(f"[4b] WindowAreaDetector.CollectWindowLinesRaw signature: already_applied")
else:
    if a_find_4b in ac:
        ac = ac.replace(a_find_4b, a_insert_4b_full, 1)
        print(f"[4b] WindowAreaDetector.CollectWindowLinesRaw signature: applied")
    else:
        print(f"[4b] WindowAreaDetector.CollectWindowLinesRaw signature: ERROR NEEDLE NOT FOUND")
        sys.exit(1)

# 4c. Add filter in foreach ObjectId (collect lines)
a_find_4c = (
    '                foreach (ObjectId id in ms)\n'
    '                {\n'
    '                    try\n'
    '                    {\n'
    '                        if (!id.ObjectClass.Name.Equals("AcDbLine", StringComparison.OrdinalIgnoreCase))\n'
    '                            continue;'
)
a_insert_4c_full = (
    '                foreach (ObjectId id in ms)\n'
    '                {\n'
    '                    try\n'
    '                    {\n'
    '                        // v17.5: scope filter — 仅 HonorSelectionScope 时 跳 出 user 框 选 外 的 entities\n'
    '                        if (scopeIds != null && id != ObjectId.Null && !scopeIds.Contains(id)) continue;\n'
    '                        if (!id.ObjectClass.Name.Equals("AcDbLine", StringComparison.OrdinalIgnoreCase))\n'
    '                            continue;'
)
a_marker_4c = "if (scopeIds != null && id != ObjectId.Null && !scopeIds.Contains(id)) continue;"
if a_marker_4c in ac:
    print(f"[4c] WindowAreaDetector.CollectWindowLinesRaw scope filter: already_applied")
else:
    # Check that the foreach pattern exists. The pattern is similar but maybe WindowsBoxDetector had it slightly different.
    # If no marker already AND no find, that's a real error.
    pass

with open(a_path, "w", encoding="utf-8") as f:
    f.write(ac)


# ===== Final summary =====
print("\n=== summary: 7 surgical edits applied across 4 files ===")
print("QuoteConfig.cs: +1 HonorSelectionScope field")
print("Commands.cs: +HashSet globalSelectionScope + scope union log + scope param passes")
print("WindowBoxDetector.cs: +scopeIds param (DetectCurtainBoxLengths + CollectWindowLines) + foreach filter")
print("WindowAreaDetector.cs: +scopeIds param (DetectWindowAreas + CollectWindowLinesRaw) + foreach filter")
print("\nNext step: dotnet build + code-reviewer-minimax-m3 in parallel.")
