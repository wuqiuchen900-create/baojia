"""Replace section 5.7 (合计公式策略) content in docs/ClosedXML踩坑记录.md.

Workaround: str_replace str_match is unreliable for CJK content; this script uses
unambiguous ASCII anchor (the section header `## 5.7` and the next section header
`## 6`) to locate the section, then replaces the strategy text.
"""
from pathlib import Path

ROOT = Path(r"E:\xiangmu\baojia")
TARGET = ROOT / "docs" / "ClosedXML踩坑记录.md"

src = TARGET.read_text(encoding="utf-8")

# Anchors (ASCII, byte-stable across runs)
S57_START = "## 5.7"
S6_START = "## 6."

start = src.find(S57_START)
if start < 0:
    raise SystemExit("ERROR: §5.7 header not found")

end = src.find(S6_START, start)
if end < 0:
    raise SystemExit("ERROR: §6 header not found (used as upper bound for §5.7)")

# Walk backward from end to find the divider `---\n` before §6 (so we replace up
# to and including the divider that separates 5.7 from 6)
div_idx = src.rfind("\n---\n", start, end)
if div_idx < 0:
    raise SystemExit("ERROR: `\\n---\\n` divider before §6 not found")

slice_start = start
slice_end = div_idx  # exclusive; we keep the divider

# New §5.7 content: multi-合计 enumeration + chunked SUM chain + reviewer caveats.
# We use English word "Contains" wrapped in backticks so that the Chinese parens
# don't blow up script transport.
NEW_S57 = '''## 5.7 合计公式策略 (2026-07-13 修订 — multi 合计 enum + chunked SUM chain)

### 背景 (为什么不能纯 SUMIF 通配)

旧版用了 `=SUMIF(B8:Bn, "*小计*", F8:Fn)` 一句话 处理: 模板里不管几个『合计』行, 都靠 SUMIF 通配小计文本动态汇总. 这有两个根本问题:

1. 用户实测发现 R159 的 F 列 **仍然** 是手写烂公式 `=F158+F110×7` (8 个 term, F110 重复 7次). 原因是: 旧 `FixTotalFormula` 从下往上扫、首个 含"合计" 行就 `break`, 错把 R198 当唯一合计, **R159 完全没被覆盖**.
2. 多个『合计』行 (主计 + 总造价) 必须分别处理: 主计=主材小计, 总造价=全部.

### 现在的策略 (multi 合计 enum)

```csharp
// 1. 一次性扫表: 收集所有 小计 行 + 所有 合计 行
var subtotalRows = new List<int>();
var totalRows    = new List<int>();
for (int row = 8; row <= maxRow; row++)
{
    var c2 = CellString(ws.Cell(row, 2));
    if (string.IsNullOrEmpty(c2)) continue;
    if (c2.Contains("小计")) subtotalRows.Add(row);
    if (c2.Contains("合计")) totalRows.Add(row);
}

// 2. 遍历每个『合计』行, 公式只累加 严格位于它上方 的小计行
foreach (var totalRow in totalRows)
{
    var validSubtotals = subtotalRows.Where(r => r < totalRow).ToList();

    // 分块 (chunkSize=150, 远低于 Excel 255 args 上限) 构建 分块 SUM 链
    // 公式形态: "=SUM(F10,F20,...)+SUM(F180,...)+..."
    ...
}
```

### 「真小计行」判定 (单一锚点)

| 检查项 | 含义 |
|---|---|
| `c2.Contains("小计")` | col2 文本包含子串"小计" |

之前的双锚点 (c1 空 + c2 EndsWith 小计) 被用户实测驳回: 某些变体 里 col1 不严格空 (CloneGroupInPlace 把 col1 带过来非空 / chrome 子小计被填充 进 col1), 把 真实小计行 错杀.

退回 Contains 唯一锚点的代价: 备注行 ("本部分无小计" / "小计鉴定标准" / "无小计项目") 也会被 放进累加列表 — 但 SUM() 自动忽略文本/空 cell, 总和不受污染.

### 分块 SUM 链 (chunkSize=150)

| 字段 | 取值 | 由来 |
|---|---|---|
| chunkSize | 150 | Excel 单 SUM 函数 args 上限 255, 留 105 的 headroom |
| F 与 H 镜像 | 是 | F (材料合计) 与 H (人工合计) 必须同步, 否则 B 列稍改 文本 就会脱节 |

### Reviewer 留的 一个 caveat (非 blocking)

若模板里 多个『合计』 都是 *最后一类 合计* (即 R158 = chrome 末小计, R159 = 主计, R158+R159 都在 chrome 子小计 之后), R159.validSubtotals == R198.validSubtotals == 全集. 在 *业务语义* 上 如果『主计 ≠ 总造价』, 请 在两个『合计』 之间 插一行空白 marker, 让 chrome 子小计 落在 第二个『合计』之前.

### 历史

- 2026-07-13 改: SUMIF 通配 → multi 合计 enum + chunked SUM chain. 同步修 R159 手写烂公式 不被覆盖 的 bug.
- 早期曾尝试 `c1=="" + c2.EndsWith("小计")` 双锚点, 被用户实测 veto 后退到 单一 `Contains`.

'''

new_src = src[:slice_start] + NEW_S57 + src[slice_end:]

# Sanity: section 5.7 still exists in new
if "## 5.7" not in new_src:
    raise SystemExit("ERROR: §5.7 header missing after replace")
# Sanity: §6 immediately follows our new §5.7 content
gap_idx = new_src.find("## 6.", new_src.find("## 5.7"))
if gap_idx < 0 or gap_idx < 200:
    raise SystemExit("ERROR: §6 not properly positioned after §5.7")
# Sanity: KEY markers present
markers = [
    "multi 合计 enum",
    "chunked SUM chain",
    "foreach (var totalRow in totalRows)",
    "r => r < totalRow",
    "## 5.7",
]
for m in markers:
    if m not in new_src:
        raise SystemExit(f"ERROR: missing marker: {m!r}")

# Sanity: byte delta reasonable (new is moderately longer than old)
delta = len(new_src) - len(src)
print(f"OLD bytes: {len(src.encode('utf-8'))}")
print(f"NEW bytes: {len(new_src.encode('utf-8'))}")
print(f"Bytes delta: {delta:+d}")

TARGET.write_text(new_src, encoding="utf-8")
print("OK \u2014 \u00a75.7 \u4ee3\u66ff\u4e3a new content.")
