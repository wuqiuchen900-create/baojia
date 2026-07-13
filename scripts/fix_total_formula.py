"""Replace FixTotalFormula method in ExcelExporter.cs by file-level bytes.

Workaround: str_replace str_match is unreliable for CJK content; this script uses
unambiguous ASCII anchor (method signature + next-section header) to locate the
method block, then replaces the whole block.
"""
from pathlib import Path

ROOT = Path(r"E:\xiangmu\baojia")
TARGET = ROOT / "BaoJiaCAD" / "ExcelExporter.cs"

src = TARGET.read_text(encoding="utf-8")

SIG = "private void FixTotalFormula(IXLWorksheet ws)\n        {"
NEXT_HDR = "        // ====================================================================\n        // 填房间数据"

start = src.find(SIG)
if start < 0:
    raise SystemExit("ERROR: cannot find FixTotalFormula signature")

hdr_idx = src.find(NEXT_HDR, start)
if hdr_idx < 0:
    raise SystemExit("ERROR: cannot find next section header after FixTotalFormula")

end_close = src.rfind("        }\n\n", start, hdr_idx)
if end_close < 0:
    raise SystemExit("ERROR: cannot find closing '}' of FixTotalFormula")

slice_start = start
slice_end = end_close + len("        }")

NEW_BODY = '''private void FixTotalFormula(IXLWorksheet ws)
        {
            int maxRow = GetLastRow(ws);

            // 1. 一次性扫表: 收集所有 小计行 + 所有 合计行
            //   - 不再依赖 templateGroups (chrome-static 重构后 templateGroups 只含区 2 房间原型)
            //   - 用户实测报告: 模板里有 多个『合计』行 (e.g. R159 主计 + R198 总造价),
            //     旧版本从下往上扫、遇到首个就 break, 会跳过其它『合计』行, 让 R159 保留模板
            //     里手写的烂公式 (例 =F158+F110+F110+F110...) 直到天荒地老.
            //   - 现在枚举全部『合计』, 每个都单独重写.
            var subtotalRows = new List<int>();
            var totalRows = new List<int>();
            for (int row = 8; row <= maxRow; row++)
            {
                var c2 = CellString(ws.Cell(row, 2));
                if (string.IsNullOrEmpty(c2)) continue;
                // 用 Contains 而非 c1=="" + EndsWith 双锚点:
                //   - 区 3 chrome 子小计行 在模板调整后被加了编号或填充内容进 col1
                //   - 区 2 房间小计行 CloneGroupInPlace 偶尔 col1 被带过来非空白
                // 都会被 旧 的 c1=="" 检查 错判为 非小计行 然后被丢.
                // 误判备注行 (如 "本部分无小计" / "小计鉴定标准") 后果可控 — SUM() 自动忽略.
                if (c2.Contains("小计")) subtotalRows.Add(row);
                if (c2.Contains("合计")) totalRows.Add(row);
            }

            if (totalRows.Count == 0)
            {
                Debug("  ⚠ FixTotalFormula 找不到『合计』行, 跳过");
                return;
            }

            // 2. 遍历每个『合计』行, 公式只累加 *严格位于它上方* 的小计行.
            //   - R159 (主计) 累加 R8..R158 中所有 "小计"
            //   - R198 (总造价) 累加 R8..R197 中所有 "小计"
            //   - 这种层级语义 完美对应 真实报表: 主计 = 直接费小计, 总造价 = 主计 + 综合费等
            const int chunkSize = 150;
            foreach (var totalRow in totalRows)
            {
                var validSubtotals = subtotalRows.Where(r => r < totalRow).ToList();

                if (validSubtotals.Count == 0)
                {
                    ws.Cell(totalRow, 6).FormulaA1 = "=0";
                    ws.Cell(totalRow, 8).FormulaA1 = "=0";
                    Debug($"  ⚠ R{totalRow} 合计行上方无小计行, 置 0");
                    continue;
                }

                var fChunks = new List<string>();
                var hChunks = new List<string>();
                for (int i = 0; i < validSubtotals.Count; i += chunkSize)
                {
                    var chunk = validSubtotals.Skip(i).Take(chunkSize).ToList();
                    fChunks.Add("SUM(" + string.Join(",", chunk.Select(r => $"F{r}")) + ")");
                    hChunks.Add("SUM(" + string.Join(",", chunk.Select(r => $"H{r}")) + ")");
                }

                // 公式形态: "=SUM(F10,F20,...)+SUM(F180,...)+..."
                string finalFormulaF = "=" + string.Join("+", fChunks);
                string finalFormulaH = "=" + string.Join("+", hChunks);

                ws.Cell(totalRow, 6).FormulaA1 = finalFormulaF;
                ws.Cell(totalRow, 8).FormulaA1 = finalFormulaH;

                Debug($"  合计公式已更新 R{totalRow}: 含 {validSubtotals.Count} 个小计, 分 {fChunks.Count} 块");
            }
        }'''

new_src = src[:slice_start] + NEW_BODY + src[slice_end:]

# Sanity 1: NEW_BODY must have matched braces (incl. interpolated string braces).
new_opens = NEW_BODY.count("{")
new_closes = NEW_BODY.count("}")
print(f"NEW_BODY braces: open={new_opens} close={new_closes}")
if new_opens != new_closes:
    raise SystemExit("ERROR: NEW_BODY brace count unbalanced")

# Sanity 2: file-level after must be balanced.
opens_before = src.count("{")
closes_before = src.count("}")
opens_after = new_src.count("{")
closes_after = new_src.count("}")
print(f"FILE braces before: open={opens_before} close={closes_before}")
print(f"FILE braces after:  open={opens_after}  close={closes_after}")
if (opens_before != closes_before) or (opens_after != closes_after):
    raise SystemExit("ERROR: file-level brace imbalance")

# Sanity 3: critical markers must be present in new_src.
checks = [
    "private void FixTotalFormula(IXLWorksheet ws)",
    "foreach (var totalRow in totalRows)",
    "r => r < totalRow",
    "subtotalRows.Add(row)",
    "totalRows.Add(row)",
]
for needle in checks:
    if needle not in new_src:
        raise SystemExit(f"ERROR: missing expected marker: {needle!r}")

# Sanity 4: NEW_BODY must NOT contain the old logic of "scan bottom-up".
forbidden = [
    "for (int row = maxRow; row >= 8; row--)",  # old bottom-up scan
    "var totalRow = 0;",                         # old single-total logic
]
for needle in forbidden:
    if needle in NEW_BODY:
        raise SystemExit(f"ERROR: NEW_BODY still contains old logic marker: {needle!r}")

print(f"OLD bytes: {len(src.encode('utf-8'))}")
print(f"NEW bytes: {len(new_src.encode('utf-8'))}")
print(f"Bytes delta: {len(new_src) - len(src):+d}")

TARGET.write_text(new_src, encoding="utf-8")
print("OK \u2014 FixTotalFormula replaced.")
