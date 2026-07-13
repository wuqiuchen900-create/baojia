# ClosedXML 踩坑记录 / 迁移说明

> 适用项目: `BaoJiaCAD` (`E:\xiangmu\baojia\BaoJiaCAD\ExcelExporter.cs`)
> 当前库版本: `ClosedXML 0.102.3`, .NET Framework 4.8
> 迁移日期: 2026-07-13 (由 EPPlus 4.5.3.3 迁移过来)

## 1. 为什么离开 EPPlus 4.5.3.3

历史背景（保留，为下一个开发者理解决策动机）：

EPPlus 4.5.3.3 在本项目踩了大量坑，主要集中在"行操作破坏内部模型"这条线上。最致命的几个：

| 问题 | 表现 | 我们的最后一版 work-around |
|---|---|---|
| `ws.DeleteRow()` 破坏 CalcChain / MergedCells / PrintArea XML | `Save()` 抛 "Error saving file" | 改用 `ws.Row(r).Hidden = true` + `Range.Clear` 兜底 |
| `InsertRow` 后 `Row.Hidden=true` / `Height=0` 静默失效 | 隐藏行实际可见 | Hidden 双管（Hidden+Height），仍不稳 |
| 跨工作表 `Worksheets.Add` + `Worksheets.Delete` 干掉 drawings | 输出报告 **Logo 丢失** | 这条根本没有解决办法 |
| `FormulaR1C1` 是相对偏移，跨行拷贝后偏移失效 | 小计公式指向 F-2:F3 这种非法引用 | 手工 `FixClonedFormulas` 重写 |
| 第三方模板里的 invalid defined name (例 `\P`) | "Name `\P` contains invalid characters" | 用 `ZipArchive` 预处理剥掉 |

决策是直接换底层库，不再修补。（原文档归档已删除）

## 2. 为什么选 ClosedXML 而不是 EPPlus 5+

| 选项 | License | 迁移代价 | 决策 |
|---|---|---|---|
| EPPlus 5+ (`EPPlusSoftware.EPPlus`) | Polyform Noncommercial — 商业用途按年付费 | 中（同 API 重映射） | ❌ 商业插件风险 |
| **ClosedXML** | **MIT，无商业限制** | **大（不同 API 重写 ~700 行）** | ✅ 已采用 |

EPPlus 原始团队在 5.x 重写了 row model，但商业 License 风险对我们（自家装饰公司用、未来可能外销）不合适。改用 ClosedXML 一次性规避。

## 3. 当前架构核心约定 — 改动前必读

### 3.0 模板三区模型（2026-07-13 简化）

模板拆成三个独立区, 代码只写中区, 上下两区都是静态:

```
R1..R7                区 1 顶部 7 行          — logo/标题/工程名称, 不动
R8..before 八综合      区 2 房间原型区         — 软件识别后 CloneGroupInPlace 写入
八综合起..R末         区 3 chrome 静态块     — 整段移动到房间后面, 不解析/不写公式/不清列
```

这次（金 雅苑18#报价单 248/77 行视觉空白 bug）根因: 旧代码把 "区 3" 当变量 ParseTemplate + FixChromeFormulas + Clear“其它”列，导致原型区 Clear+Hide 依赖在 ClosedXML 0.97 上不稳定。**新代码: 区3 不认识、不动、不改，只动区2。**

### 3.1 不要在完成全表填表后用 `Worksheets.Delete` 清理原模板表

**这是迁移的根本动机**。`Worksheets.Delete(原表名)` 会同步删除 drawing XML → logo 丢。这是 EPPlus 时代反复踩坑的源头。ClosedXML 在这个动作上不例外。

- ❌ 不允许：`wb.Worksheets.Add("_构建中_")` → 填表 → `wb.Worksheets.Delete(origName)`（Logo 永久丢失）
- ✅ 允许：直接 `wb.Worksheet(1)` 在原表上原地写
- ✅ 允许：`ws.Row(r).InsertRowsAbove(n)` —— ClosedXML **不破坏** drawings，可以推下 prototype 与 chrome

### 3.2 流程骨架（`ProcessRooms`）

```
1. SafeCopyTemplateToTemp → PreprocessInvalidDefinedNames (zip-level, 库无关)
2. new XLWorkbook(path)  → wb.Worksheets.First()         (含 logo)
3. ParseTemplate 扫到 八综合/七直费/九其它 起点 立即 break — chrome 段不再入 groups
4. ProcessRooms:
     a. 仿真 totalRequiredRows
     b. ws.Row(protoStart).InsertRowsAbove(N)            (区 3 chrome 顺势下移, 保留 logo)
     c. 整体更新所有 roomPrototypes 的 row 偏移 (+N)
     d. CloneGroupInPlace (Range.CopyTo + FixClonedFormulas) 写入房间克隆
     e. ws.Rows(origProtoRange).Delete() 真删原原型区 — 区3 chrome 顺势上顶,与房间无缝拼接
5. FixTotalFormula SUMIF (跨区 2 房间"小计"+区 3 chrome"小计"，自动 rollup)
6. wb.Save() → SafeCopyFile → 用户路径
```

## 4. ClosedXML 0.102.x 项目实际用到的 API

| 场景 | API | 注意 |
|---|---|---|
| 打开 | `new XLWorkbook(path)` | path 必须是 sandbox 副本（避免 Excel 占用锁） |
| 取工作表 | `wb.Worksheets.First()` 或 `wb.Worksheet(1)` | 我们用 First |
| 取单元格 | `ws.Cell(r, c)` | IXLCell，比 `ws.Cells[r,c]` 更稳 |
| 读取值 | `cell.Value.ToString()` 或 `CellString(cell)` helper | `XLCellValue.IsBlank` 检查空 |
| 读取公式 | `cell.FormulaA1`（A1 格式） | ClosedXML 不支持 R1C1，感恩 |
| 写入值 | `cell.Value = "string"` / `cell.Value = 3.14` | auto-detect 类型 |
| 写入公式 | `cell.FormulaA1 = "=SUM(A1:A10)"` | 包含或不包含 `=` 都接受，含 `=` 更显式 |
| 写值清空 | `cell.Clear(XLClearOptions.Contents)` | 不要用 `cell.Value = ""` —— 在 ClosedXML 里它**静默保留原公式**，表格看上去"空"但实际重新打开时还有公式。`Clear(Contents)`才是真正同时清除值 + 公式 |
| 范围 | `ws.Range(r1, c1, r2, c2)` | 四参数版最稳 |
| 拷贝 | `ws.Range(...).CopyTo(ws.Cell(dstR, dstC))` | dst 是左上角 |
| 合并 | `ws.Range(...).Merge()` | 返回 IXLRange；不需保留变量 |
| 隐藏行 | `ws.Row(r).Hide()` | 不需要 + `Height = 0`，Hide 已够 |
| 行高 | `ws.Row(r).Height = 25.0` | `double` 类型 |
| 列宽 | `ws.Column(c).Width = 15.0` | 同上 |
| 插入空行 | `ws.Row(r).InsertRowsAbove(n)` | **签名是 int 不是 uint**，保留 drawings |
| 末行 | `ws.LastRowUsed()?.RowNumber() ?? 200` | 返回 IXLRange，可能 null |
| 打印区域 | `ws.PageSetup.PrintAreas.Clear(); ws.PageSetup.PrintAreas.Add("A1:I{row}")` | 字符串版最稳 |
| 字体 | `cell.Style.Font.Bold = true; cell.Style.Font.FontSize = 14` | **FontSize 不是 Size** |
| 对齐 | `cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center` | 枚举类型改了 |

## 5. BaoJiaCAD 项目特定踩坑（不要重蹈覆辙）

### 5.1 chrome 区视为单一静态块（不要再解析/写公式/清列） ⭐

历史误设计: 旧代码把 "八综合 起 / 直至表末尾" 这段 (chrome 段) 当成变量 TemplateGroup 处理——ParseTemplate 持续累加子段、FixChromeFormulas 重写 A/B 子段小计、"其它"段列 Clear。

**这完全是过度工程**——模板里的 chrome 段是成品块，金额列预留 0 是给用户手填的，根本不需要代码动。

**新规则 (2026-07-13):**

```csharp
// ParseTemplate 扫到第一个 chrome 触发行立即 break:
if (chromeStartRow == 0 && IsHouseLevelMarker(c1, c2))
{
    chromeStartRow = row;       // 仅记录起点，不需要逐一入 group
    break;                       // 区 3 作为静态块, 后续不再解析
}
```

- ❌ 不要再把 chrome 子段 (A 电安装 / B 水安装 / 九 其它) 作为 TemplateGroup
- ❌ 不要再调用 FixChromeFormulas 写公式 — 模板自带的小计/总计公式原封不动
- ❌ 不要再 Clear "其它" 段 col3/5/6/7/8 — 模板里这些列本就是空(预留用户填)
- ✅ SUMIF 合计仍能 自动 rollup 区 2 房间"小计"+ 区3 chrome"小计"，不需额外胶带

### 5.2 原型残留用 `Rows().Delete()` 真删 (避免 248/77 行视觉空白)

`InsertRowsAbove(N)` 把老原型推到 `R{protoStart+N}..R{protoStart+N+protoSpan-1}`，**不能留**。究竟:

```csharp
int origProtoStartAfterInsert = protoStart + totalRequiredRows;
int origProtoEndAfterInsert = origProtoStartAfterInsert + protoSpan - 1;
ws.Rows(origProtoStartAfterInsert, origProtoEndAfterInsert).Delete();
// 区 3 chrome 顺势从 R(protoStart+N+protoSpan) 顶到 R(protoStart+N), 与房间无缝拼接
```

清理后区 3 chrome 在 [protoStart+N, protoStart+N+chromeLenth) 连续接在所有房间后面。

> ⚠ **`Rows().Delete()` 与 drawings 交互**: Delete 会剥离被删范围内 drawings 锁点. Logo 在 R1..R7 不动; chrome 内部 drawings 保留 (区 3 不进入删除范围). 但 **原型区 (R8 起 到 八综合 前一行) 不应放置图片/形状/图表** — 会被吃掉. 如未来模板原型中有画图需要重新考虑方案(例如: 画入 chrome 区, 或不在原型区中放).

**❌ 禁忌**: 不要再走老路径 ws.Range(...).Clear(XLClearOptions.All) + ws.Row(r).Hide() + ws.Row(r).Height = 0。为什么: **ClosedXML 0.97 上 Clear 过的行, Hide() 不彻底**. 原型区仍是默认行高 (约 15pt/行), 视觉上产生 N 行空白带。金 雅苑18# 这个 bug 根因之一。

### 5.3 公式引用问题：`Range.CopyTo` 仍按字面拷贝

跨行 `Range.CopyTo` 把 `=SUM(F8:F12)` 字面复制到目标位置 → 公式还指着 F8:F12 而不是目标区内的新行号。**任何克隆后必须 `FixClonedFormulas`** 重写 col6、col8、subtotal 的公式引用。

### 5.4 模板被 Excel 占用锁

必须在 sandbox（`%TEMP%\baojia_work_*.xlsx`）上操作原模板，绝不直接打开原模板路径。`SafeCopyTemplateToTemp` 3 次 retry + `PreprocessInvalidDefinedNames` 处理 invalid defined name 都保留（库无关，因为是 zip + file IO 层）。

### 5.5 复式楼多楼层排序

`FloorOrderKey` 用中文数字字符 contains 映射为 int 排序键。

⚠ 不要用 `OrderBy(StringComparer.Ordinal)`：Unicode 顺序 `一 < 三 < 二 < 五 < 四` 是乱序的。

⚠ 简单 contains 检查 — "十一楼" 会匹配 "一" 优先级返回 1000。复式楼 ≥11 层罕见，准够用；如未来遇到再升级正则。### 5.6 兜底链 (`ResolveTemplates`)

5 步 fallback 顺序：`(Floor,Type) → ("",Type) → 配置 fbMap → 阳台/外花园默认 → 一楼 → 任意楼层首个`。
"其他桶"房间（6 大类都不命中）**永远找不到模板**。如发现，扩 `CategoryPanel.MapToSixCategory` 或 `RoomTypeMaps`。

### 5.7 合计公式策略 (2026-07-13 修订 — multi 合计 enum + chunked SUM chain)

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


---

## 6.  禁用反模式（绝对不要做）

以下做法在 EPPlus 4.5.3.3 时代是必要 workaround，**在 ClosedXML 时代全部禁止**：

| ❌ 反模式 | 现在的替代 |
|---|---|
| `Worksheets.Add("_构建中_")` 然后 `Worksheets.Delete(origSheetName)` | 直接在原表上 `ws.Row().InsertRowsAbove()` |
| `ws.Range(...).Clear(XLClearOptions.All)` + `ws.Row(r).Hide()` 作为隐藏 prototype 的方案 | EPPlus 时代的 `HideProtoRows` 已废弃。改用 **`ws.Rows(start, end).Delete()`** 真删行 — chrome 区顺势上顶 |
| `xlsx.DeleteRow()` | ClosedXML 上 `DeleteRow()` 也存在但**也破坏 drawing**，禁止 |
| 把 chrome 段 (八综合/补充报价/补充说明/业主签字) 作为 TemplateGroup `groups.Add(...)` | ParseTemplate 扫到 chrome 触发行立即 `break` — chrome 是静态块，代码不识别它 |
| 调用 `FixChromeFormulas` 与 "其它"段 `Clear(XLClearOptions.Contents)` | 全部刪除 — 模板自带公式 / 预留空白格都不用代码动 |
| 手工 `dstCell.FormulaR1C1 = srcCell.FormulaR1C1` | `IXLCell.FormulaR1C1` 不存在！用 `FormulaA1 = "=..."` |
| `ws.Cells[r, c].Value = ""` 来清空 cell | `cell.Clear(XLClearOptions.Contents)` |
| `ws.Row(r).Style.Font.Size = 14`（旧 EPPlus API） | `cell.Style.Font.FontSize = 14`（**FontSize** 不是 Size） |
| `ws.Cells[r,c].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center` | `ws.Cell(r,c).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center`（枚举大写） |

提交前自检 grep：

```bash
grep -rn "EPPlus\|OfficeOpenXml\|ExcelPackage\|ExcelWorksheet\|ExcelHorizontalAlignment\|FormulaR1C1" BaoJiaCAD/
# 期望: 0 命中 (除注释里的历史说明)
```

---

最后更新: 2026-07-13（完成 EPPlus → ClosedXML 迁移）
