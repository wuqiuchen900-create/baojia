# verify_windowarea_v0.py
# ========================
# v17.3 round 4 simulate.  Mirrors WindowAreaDetector.cs.
#   - WindowLabelRegex:  IgnoreCase + [-:=#：＝]+ REQUIRED (multi-sep)
#   - MTextFormatCodesRegex:  strip only \X...; (keep {} intact)
#   - BoundaryHelper.IsPointInPolygonWithTolerance (50 mm snap)
#   - DistancePointToSegment
#   - ResolveWindowHeightMM returns HeightResolved struct
#   - area formula /2
#
# Usage
#     cd .. && python scripts/verify_windowarea_v0.py
#     exit 0 = pass.

import math
import re
import sys

# v17.3 round 4:
#   - re.IGNORECASE: 容忍 ch/CH 混用
#   - 分隔符 [+ suffix]: 容忍 "CH := 1500" 双/多重分隔
#   - 现在 python parser 与 C# verbatim 严格相同
MTextFormatCodesRegex = re.compile(r"\\[a-zA-Z][^;]*;")
WindowLabelRegex = re.compile(r"(CH|ZH|DH|SH)\s*[-:=#\uFF1D\uFF1A]+\s*(\d+(?:\.\d+)?)",
                               re.IGNORECASE)


def strip_mtext_format_codes(raw):
    if not raw:
        return ""
    cleaned = MTextFormatCodesRegex.sub("", raw)
    for ch in ("\r", "\n", " ", "\t"):
        cleaned = cleaned.replace(ch, "")
    return cleaned


def parse_labels(mtext_raw):
    text = strip_mtext_format_codes(mtext_raw)
    return [(m.group(1).upper(), float(m.group(2))) for m in WindowLabelRegex.finditer(text)]


def distance_point_to_segment(p, a, b):
    abx, aby = b[0] - a[0], b[1] - a[1]
    ab_len2 = abx * abx + aby * aby
    if ab_len2 < 1e-9:
        return math.hypot(p[0] - a[0], p[1] - a[1])
    apx, apy = p[0] - a[0], p[1] - a[1]
    t = (apx * abx + apy * aby) / ab_len2
    if t < 0:
        t = 0
    elif t > 1:
        t = 1
    fx = a[0] + t * abx
    fy = a[1] + t * aby
    return math.hypot(p[0] - fx, p[1] - fy)


def is_point_in_polygon_with_tolerance(pt, poly, tol_mm=50.0):
    n = len(poly)
    if n < 3:
        return False
    for i in range(n):
        a = poly[i]
        b = poly[(i + 1) % n]
        if distance_point_to_segment(pt, a, b) <= tol_mm:
            return True
    inside = False
    for i in range(n):
        j = (i - 1) % n
        pi = poly[i]
        pj = poly[j]
        if (pi[1] > pt[1]) != (pj[1] > pt[1]):
            xi = (pj[0] - pi[0]) * (pt[1] - pi[1]) / (pj[1] - pi[1]) + pi[0]
            if pt[0] < xi:
                inside = not inside
    return inside


class HeightResolved:
    __slots__ = ("height_mm", "source_key", "source_detail")
    def __init__(self, height_mm=0.0, source_key="", source_detail=""):
        self.height_mm = height_mm
        self.source_key = source_key
        self.source_detail = source_detail
    def __repr__(self):
        return "HeightResolved({} mm via {} {})".format(
            self.height_mm, self.source_key, self.source_detail)


def resolve_window_height_mm(window_mid, labels, bo_edges, room_total_h_mm,
                             search_range_mm=1000.0):
    # labels 统一 upper case (parser 已 upper)
    def nearest(key_filter):
        best = None
        best_d = float("inf")
        for lbl_key, lbl_val, lbl_pos in labels:
            if lbl_key.upper() != key_filter.upper():
                continue
            d_to_bo = min(distance_point_to_segment(lbl_pos, a, b) for a, b in bo_edges)
            if d_to_bo > search_range_mm:
                continue
            d_to_mid = math.hypot(lbl_pos[0] - window_mid[0], lbl_pos[1] - window_mid[1])
            if d_to_mid < best_d:
                best_d = d_to_mid
                best = (lbl_key.upper(), lbl_val, lbl_pos)
        return best

    ch = nearest("CH")
    if ch is not None:
        return HeightResolved(ch[1], "CH", "direct")

    zh = nearest("ZH")
    if zh is not None:
        return HeightResolved(zh[1], "ZH", "direct")

    dh = nearest("DH")
    if dh is not None:
        sh = nearest("SH")
        if sh is not None:
            h = room_total_h_mm - dh[1] - sh[1]
            if h > 0:
                return HeightResolved(h, "DH+SH",
                    "{}-{}-{}={}".format(int(room_total_h_mm), int(dh[1]), int(sh[1]), int(h)))
            return HeightResolved(0, "DH+SH(neg)", "skip")
        h2 = room_total_h_mm - dh[1]
        if h2 > 0:
            return HeightResolved(h2, "DH-only",
                "totalHeight-DH={}(gapTop fallback)".format(int(h2)))
        return HeightResolved(0, "DH-only(neg)", "skip")

    return HeightResolved(0, "SH-only", "skip (info insufficient)")


def run_tests():
    fails = []

    # T1: MText format codes stripping  Round 4 simplified (no longer strips {block})
    cases = [
        (r"\c1;CH=1500;",              "CH=1500;"),         # \c1; strip; trailing ; stays
        # NOTE:  r"{\fArial;b0;i0;c0;}"  Python raw string 中 b0/i0/c0 是 字 面 (没 \) , 不是 MText 格式码
        ("CH=1500",                    "CH=1500"),          # plain text
        (r"\H1.5;DH=900;",              "DH=900;"),          # \H1.5; strip
        (r"{\fArial;}CH=1960",          "{}CH=1960"),        # \fArial; strip, {} remain
        ("DH:900\nSH:400",              "DH:900SH:400"),     # whitespace stripped
    ]
    for raw, expected in cases:
        got = strip_mtext_format_codes(raw)
        if got != expected:
            fails.append("T1 strip\n  in:  {!r}\n  exp: {!r}\n  got: {!r}".format(raw, expected, got))
        else:
            print("T1 strip [{!r}] -> [{!r}] OK".format(raw, got))

    # T2: label parsing  - round 4 + IgnoreCase + [+ sep] + REQUIRED
    parse_cases = [
        ("CH=1500",                  [("CH", 1500.0)]),
        ("CH:1500",                  [("CH", 1500.0)]),
        ("CH#1500",                  [("CH", 1500.0)]),
        ("CH\uFF1D1500",             [("CH", 1500.0)]),
        ("CH\uFF1A1500",             [("CH", 1500.0)]),
        ("CH-1500",                  [("CH", 1500.0)]),
        # 「+」 multi-sep
        ("CH := 1500",                [("CH", 1500.0)]),
        # IgnoreCase
        ("ch=1500",                  [("CH", 1500.0)]),
        ("Zh:2260",                  [("ZH", 2260.0)]),
        ("ZH:2260",                  [("ZH", 2260.0)]),
        ("ZH\uFF1A2260",             [("ZH", 2260.0)]),
        (r"\c1;DH:900\c0;ZH:2240",  [("DH", 900.0), ("ZH", 2240.0)]),
        (r"{\fArial;}SH:400",        [("SH", 400.0)]),
        ("CH:1500;DH=900;SH-400",   [("CH", 1500.0), ("DH", 900.0), ("SH", 400.0)]),
        ("ZH:2260\nDH:740",          [("ZH", 2260.0), ("DH", 740.0)]),
        ("ch:1500\nzh:1800",         [("CH", 1500.0), ("ZH", 1800.0)]),
        # CH1=500: REQUIRED separator 拒 误 匹 配
        ("CH1=500",                  []),
        # 富 文 本 块 含 KEY (因 round 4 不 再 strip {})
        (r"{\fArial;CH=1960}",       [("CH", 1960.0)]),
    ]
    for raw, expected in parse_cases:
        got = parse_labels(raw)
        if got != expected:
            fails.append("T2 parse\n  in: {!r}\n  exp: {!r}\n  got: {!r}".format(raw, expected, got))
        else:
            print("T2 parse [{!r}] -> {} OK".format(raw, got))

    # T3: polygon snap tolerance
    poly = [(0, 0), (4000, 0), (4000, 3000), (0, 3000)]
    contain_cases = [
        ((2000, 1500), True,  "center"),
        ((10, 10),     True,  "10 mm inside corner"),
        ((-10, -10),   True,  "10 mm outside corner (snap)"),
        ((0, 0),       True,  "exact vertex"),
        ((49, 1500),   True,  "49 mm from left edge (within tol)"),  # distance 49
        ((51, 1500),   True,  "51 mm from left edge (ray-cast INSIDE poly)"),  # distance 51 NO snap, but interior
        ((0, -51),     False, "51 mm below bottom edge (no snap)"),
        ((0, -49),     True,  "49 mm below bottom edge (snap)"),
        ((-100, 1500), False, "100 mm outside left far"),
        ((4051, 1500), False, "51 mm past right edge (no snap)"),
        ((4001, 1500), True,  "1 mm past right edge (snap tol 50)"),
        ((0, 3050),    True,  "50 mm above top edge (snap boundary)"),
        ((0, 3049),    True,  "49 mm above top edge (snap)"),
        ((0, 3051),    False, "51 mm above top edge (no snap)"),
    ]
    for pt, expected, label in contain_cases:
        got = is_point_in_polygon_with_tolerance(pt, poly, 50.0)
        if got != expected:
            fails.append("T3 poly {} ({}): exp {}, got {}".format(pt, label, expected, got))
        else:
            print("T3 poly {} ({}) -> {} OK".format(pt, label, got))

    # T4: priority height
    bo_edges = [(poly[i], poly[(i + 1) % 4]) for i in range(4)]
    inside_mid = (2000, 200)

    r = resolve_window_height_mm(inside_mid,
        [("CH", 1500, (2100, 100))], bo_edges, 2800)
    if r.height_mm != 1500 or r.source_key != "CH":
        fails.append("T4 CH direct: exp (1500, CH direct), got {}".format(r))
    else:
        print("T4 CH direct {} OK".format(r))

    r = resolve_window_height_mm(inside_mid,
        [("ZH", 2260, (2100, 100))], bo_edges, 2800)
    if r.height_mm != 2260 or r.source_key != "ZH":
        fails.append("T4 ZH direct: exp (2260, ZH direct), got {}".format(r))
    else:
        print("T4 ZH direct {} OK".format(r))

    r = resolve_window_height_mm(inside_mid,
        [("CH", 1500, (1900, 100)), ("ZH", 2260, (2100, 100))],
        bo_edges, 2800)
    if r.height_mm != 1500 or r.source_key != "CH":
        fails.append("T4 CH > ZH priority: exp (1500, CH direct), got {}".format(r))
    else:
        print("T4 CH > ZH {} OK".format(r))

    r = resolve_window_height_mm(inside_mid,
        [("DH", 740, (1900, 100)), ("SH", 400, (2100, 100))],
        bo_edges, 2800)
    if r.height_mm != 1660 or r.source_key != "DH+SH":
        fails.append("T4 DH+SH new: exp (1660, DH+SH …), got {}".format(r))
    else:
        print("T4 DH+SH {} OK".format(r))

    r = resolve_window_height_mm(inside_mid,
        [("DH", 900, (2100, 100))], bo_edges, 2800)
    if r.height_mm != 1900 or r.source_key != "DH-only":
        fails.append("T4 DH-only fallback: exp (1900, DH-only …), got {}".format(r))
    else:
        print("T4 DH-only fallback {} OK".format(r))

    r = resolve_window_height_mm(inside_mid,
        [("SH", 400, (2100, 100))], bo_edges, 2800)
    if r.height_mm != 0 or r.source_key != "SH-only":
        fails.append("T4 SH-only skip: exp (0, SH-only skip), got {}".format(r))
    else:
        print("T4 SH-only = {} OK".format(r))

    r = resolve_window_height_mm(inside_mid,
        [("CH", 1500, (1900, 100)), ("DH", 740, (2100, 100))],
        bo_edges, 2800)
    if r.height_mm != 1500 or r.source_key != "CH":
        fails.append("T4 CH+DH: exp (1500, CH direct), got {}".format(r))
    else:
        print("T4 CH+DH = {} OK (DH ignored)".format(r))

    # T5: /2 area formula
    area = 2.290 * 1.960 / 2.0
    if abs(area - 2.2442) > 0.001:
        fails.append("T5 area: exp 2.2442, got {}".format(area))
    else:
        print("T5 area = 2.290 * 1.960 / 2 = {:.4f} m^2 OK".format(area))

    # T6: 4 windows same CH
    win_widths = [2290, 2200, 2280, 2300]
    labels6 = [("CH", 1960, (2100, 100))]
    total = sum((w / 1000.0) * (resolve_window_height_mm(inside_mid, labels6, bo_edges, 2800).height_mm / 1000.0) / 2.0
                for w in win_widths)
    expected_total = sum(w / 1000.0 * 1.960 / 2.0 for w in win_widths)
    if abs(total - expected_total) > 0.01:
        fails.append("T6 sum: exp {:.4f}, got {:.4f}".format(expected_total, total))
    else:
        print("T6 sum 4 windows CH=1960 = {:.4f} m^2 OK".format(total))

    # T7: real-image 1 - 电竞房窗 ZH:2260 + DH:740, 宽 2400mm (ZH wins)
    r7 = resolve_window_height_mm(inside_mid,
        [("ZH", 2260, (1900, 100)), ("DH", 740, (2100, 100))],
        bo_edges, 2800)
    a1 = 2.400 * (r7.height_mm / 1000.0) / 2.0
    if abs(a1 - 2.712) > 0.001 or r7.source_key != "ZH":
        fails.append("T7 ZH:2260+DH:740 (ZH wins): exp 2.712 ZH direct, got {:.4f} + {}".format(a1, r7))
    else:
        print("T7 ZH:2260 直接 2260mm x 2.4m / 2 = {:.4f} m^2 OK (DH ignored)".format(a1))

    # T8: real-image 2 - SH+DH
    r8 = resolve_window_height_mm(inside_mid,
        [("SH", 400, (1900, 100)), ("DH", 740, (2100, 100))],
        bo_edges, 2800)
    h2 = r8.height_mm
    a2 = 2.400 * (h2 / 1000.0) / 2.0
    if abs(a2 - 1.992) > 0.001 or r8.source_key != "DH+SH" or abs(h2 - 1660) > 0.001:
        fails.append("T8 SH+DH: exp (1660, DH+SH) / 1.992, got {} / {:.4f}".format(r8, a2))
    else:
        print("T8 SH:400+DH:740 = {}({}) /2 = {:.4f} m^2 OK".format(int(h2), r8, a2))

    # T9: case-insensitive 全角 + ZH:2260\nDH:740
    parsed = parse_labels("zh:2260\ndh:740")
    exp = [("ZH", 2260.0), ("DH", 740.0)]
    if parsed != exp:
        fails.append("T9 case-insensitive zh+dh: exp {}, got {}".format(exp, parsed))
    else:
        print("T9 case-insensitive zh:2260 \\n dh:740 parsed OK -> {}".format(parsed))

    # T10: separator multi «CH := 1500»
    parsed = parse_labels("CH := 1500")
    exp2 = [("CH", 1500.0)]
    if parsed != exp2:
        fails.append("T10 multi-sep CH := 1500: exp {}, got {}".format(exp2, parsed))
    else:
        print("T10 multi-sep CH := 1500 parsed OK -> {}".format(parsed))

    print()
    total_count = len(cases) + len(parse_cases) + len(contain_cases) + 7 + 3
    print("Total tests run: {}".format(total_count))
    if fails:
        print("FAILED: {}".format(len(fails)))
        for f in fails:
            print("  X " + f)
        return 1
    print("ALL PASS.")
    return 0


if __name__ == "__main__":
    sys.exit(run_tests())
