using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

// =======================================================================
// ClosedXML (MIT) 路线 — 在原模板工作表上原地写，绝不删/加工作表。
//   ✅ Logo 与 drawings 天然保留: InsertRowsAbove 不会破坏 drawings XML
//   ✅ InsertRowsAbove 稳定 (不像 EPPlus 4.5.3.3 让 Hidden/Height=0 失效)
//   ✅ Range.CopyTo + FormulaA1 干净 (无 R1C1 相对偏移噩梦)
//
// 流程:
//   ① SafeCopyTemplateToTemp + PreprocessInvalidDefinedNames (zip-level, 库无关)
//   ② new XLWorkbook(path).Worksheets.First() — 直接打开原模板 (含 logo)
//   ③ SetProjectName + ParseTemplate (元数据提取, 不修改表)
//   ④ ProcessRooms:
//        ParseTemplate 解析区 2 房间原型 — 扫到 chrome 起点(八综合 等)立即 break
//        仿真 totalRequiredRows
//        ws.Row(protoStart).InsertRowsAbove(totalRequiredRows) -> 拉空位(区 3 chrome 顺势下移)
//        CloneGroupInPlace (Range.CopyTo + FixClonedFormulas) 写入房间克隆
//        ws.Rows(origProtoRange).Delete() 真删旧原型 — chrome 静态块顶到房间后无缝拼接
//   ⑤ FixTotalFormula SUMIF 公式 (跨区 2 房间"小计" + 区 3 chrome"小计"，自动 rollup)
//   ⑥ wb.Save() → 沙盒 → SafeCopyFile 到用户路径
//   (chrome 段视为单一静态块: 不解析 / 不写公式 / 不清"其它"列 — 避免 ClosedXML 0.97 Hide 失效)
// =======================================================================

namespace BaoJiaCAD
{
    public class ExcelExporter
    {
        public Action<string> Log { get; set; }

        private void Debug(string msg) { Log?.Invoke(msg); }

        /// <param name="primaryTemplatePath">v7: 1F 模板的 xlsx 路径. 复制后作为输出 wb 的背景 (含 logo, chrome 静态块, 原型区). 单层模式 (floorTemplatePaths 空) 走原 v6 路径, 仅用此参数.</param>
        /// <param name="primaryTemplateName">v7: 1F 模板的名字 (e.g. "dizhuan"). 用于给主模板的 prototype groups 标 SourceTemplate. 单层模式可空.</param>
        /// <param name="floorTemplatePaths">v7: 楼层别名→xlsx 路径 映射. 空 dict / 1 项 = v6 单层行为. >1 项 = 复式多模板混合.</param>
        public void Export(List<Room> rooms, string projectName, string primaryTemplatePath, string primaryTemplateName, Dictionary<string, string> floorTemplatePaths, string outputPath, QuoteConfig config)
        {
            // 🔧 v7: 参数兼营. v6 调用旧 5 参数 版本时 back-compat.
            if (floorTemplatePaths == null) floorTemplatePaths = new Dictionary<string, string>();
            string templatePath = primaryTemplatePath;
            bool isMultiTemplate = floorTemplatePaths.Count > 1;

            Debug($"模板路径: {templatePath}" + (isMultiTemplate ? $" (v7 多模板: {floorTemplatePaths.Count} 项)" : ""));
            Debug($"输出路径: {outputPath}");
            Debug($"识别的房间数: {rooms.Count}");

            // 库无关: 模板拷到 fresh ASCII 副本绕开 应用独占锁 (CAD/Excel)
            string rawCopyPath = SafeCopyTemplateToTemp(templatePath);

            // 库无关: 预处理 invalid defined names (例 \P). ZipArchive 处理 xlsx.
            string preprocessedPath = PreprocessInvalidDefinedNames(rawCopyPath);

            // ASCII 安全沙盒
            string tempWorkingPath = Path.Combine(
                Path.GetTempPath(),
                "baojia_work_" + Guid.NewGuid().ToString("N") + ".xlsx");

            try
            {
                SafeCopyFile(preprocessedPath, tempWorkingPath);
                Debug("已建立临时沙盒: " + tempWorkingPath);

                using (var wb = new XLWorkbook(tempWorkingPath))
                {
                    var ws = wb.Worksheets.First();
                    Debug($"工作表: {ws.Name}");

                    SetProjectName(ws, projectName);

                    // 🔧 v7: 1F 模板的 groups 是输出 ws 的原住客 — ParseTemplate(ws) 拿到, 同时 protoStart/chromeStart 也在里面.
                    //   复式多模板 (floorTemplatePaths.Count > 1) 时, 非主模板 (非 1F) 的 groups 拷到 scratchpad.
                    //   复式但 实际都选同一模板 (Count==1) → 走 v6 原路径, scratchpad 不用.
                    int scratchpadStart = -1;
                    // 🔧 v7 regression-guard: 只在 多模板模式 (isMultiTemplate) 下 给 1F groups 标 SourceTemplate.
                    //   v6 单模板 (isMultiTemplate=false) 时 必须 标 "" — 否则 1F groups 带 "dizhuan" 标, 但 LookupSourceTemplateForFloor
                    //   在 SelectedFloorTemplates 空时返 "", 造成 ResolveTemplates("", ...) 找不到 group (v6 隐性 regression).
                    //   令 tplDict 在 v6 模式下 退化为 2D (SourceTemplate 键都为 ""), 与 v6 行为一致.
                    string mainTplName = (isMultiTemplate && !string.IsNullOrEmpty(primaryTemplateName)) ? primaryTemplateName : "";
                    var templateGroups = ParseTemplate(ws, config, mainTplName);
                    Debug($"模板分组数: {templateGroups.Count} (mainTplName='{mainTplName}')");

                    List<TemplateGroup> scratchpadGroups = null;
                    if (isMultiTemplate)
                    {
                        scratchpadStart = ComputeScratchpadStart(ws, templateGroups);
                        scratchpadGroups = BuildMultiTemplateScratchpad(ws, floorTemplatePaths, config, scratchpadStart);
                        templateGroups.AddRange(scratchpadGroups);
                        Debug($"v7 多模板混合: 主模板 {mainTplName} + 其它 {scratchpadGroups.Count} 组 (scratchpad start=R{scratchpadStart})");
                    }

                    ProcessRooms(ws, rooms, templateGroups, config, scratchpadStart, scratchpadGroups);
                    FixTotalFormula(ws);

                    Debug("数据已填入，公式自动计算");

                    try
                    {
                        wb.Save();
                        Debug("沙盒内 Excel 已保存");
                    }
                    catch (Exception saveEx)
                    {
                        string dumpPath = Path.Combine(
                            Path.GetTempPath(),
                            "baojia_dump_" + Guid.NewGuid().ToString("N") + ".xlsx");
                        try { File.Copy(tempWorkingPath, dumpPath, true); } catch { }
                        throw new InvalidOperationException(
                            $"Excel 内部保存失败。已转储到:{dumpPath}。底层错误: {saveEx.Message}",
                            saveEx);
                    }
                }

                try
                {
                    SafeCopyFile(tempWorkingPath, outputPath);
                    Debug($"★ 报价单已生成: {outputPath}");
                }
                catch (Exception lockEx)
                {
                    string dir = Path.GetDirectoryName(outputPath) ?? Path.GetTempPath();
                    string fallbackOutput = Path.Combine(dir, "【恢复】报价单_" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".xlsx");
                    try
                    {
                        SafeCopyFile(tempWorkingPath, fallbackOutput);
                        Debug($"已备出备用拷贝: {fallbackOutput}");
                    }
                    catch (Exception fbEx)
                    {
                        throw new IOException(
                            $"原路径与备用路径都保存失败。备用异常: {fbEx.Message}", fbEx);
                    }

                    throw new IOException(
                        $"❌ 请先关闭 Excel 中正在打开的旧报价单文件！本次操作已备出到同目录备用文件,请查看:{fallbackOutput}。原始锁异常: {lockEx.Message}",
                        lockEx);
                }
            }
            finally
            {
                if (!string.Equals(preprocessedPath, rawCopyPath, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(preprocessedPath, templatePath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(preprocessedPath); } catch { /* 最佳努力 */ }
                }
                if (!string.Equals(rawCopyPath, templatePath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(rawCopyPath); } catch { /* 最佳努力 */ }
                }
                try { File.Delete(tempWorkingPath); } catch { /* 最佳努力 */ }
            }
        }

        // ====================================================================
        // 库无关文件 IO (ZipArchive 处理 invalid defined names)
        // ====================================================================

        private string SafeCopyTemplateToTemp(string templatePath)
        {
            string rawCopyPath = Path.Combine(
                Path.GetTempPath(),
                "baojia_raw_" + Guid.NewGuid().ToString("N") + ".xlsx");

            Exception lastEx = null;
            for (int retry = 0; retry < 3; retry++)
            {
                try
                {
                    File.Copy(templatePath, rawCopyPath, overwrite: true);
                    Debug($"模板已拷贝到 raw 副本 (绕开源锁, retry={retry}): {rawCopyPath}");
                    return rawCopyPath;
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                    Debug($"模板 raw 副本 retry={retry} 失败: {ex.Message}");
                    if (retry < 2) System.Threading.Thread.Sleep(500);
                }
            }
            Debug($"⚠ 模板 raw 副本 3 次重试均失败, 回退原文件 (可能独占锁): {lastEx?.Message}");
            return templatePath;
        }

        private static readonly Regex InvalidDefinedNameRegex = new Regex(
            @"<definedName[^>]*?name=""[^""]*[\\/?*\[\]:][^""]*""[^>]*?(?:/>|>.*?</definedName>)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static readonly Regex EmptyDefinedNamesRegex = new Regex(
            @"<definedNames>\s*</definedNames>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 🔧 v5: 「可能被当是贴装在地面上的行」识别关键词 — 原名需含 这些 「之一」 才走 floorItemCount==0 fallback.
        //   故意 「地板」「强化地板」「木地板」均 不 列入 — 避免mudiban 床屋被v4 误改为地砖+套价.
        private static readonly string[] TileishKeywords = new[] {
            "\u5730\u7816",  // 地砖
            "\u6b63\u94fa",  // 正铺
            "\u6b63\u8d34",  // 正贴
            "\u83f1\u94fa",  // 菱铺
            "\u83f1\u8d34",  // 菱贴
        };

        // 🔧 v14.2 fix: 通配 「MM 规格 sub-string」 检测 — catch 「600MM 圆形实木石砖线（300-800MM）」 / 「瓷质玻化砖（800*800MM）」 等 nomes 不含 「正铺」/「铺地砖」/「客厅」/「抹地」 的 多个 名字 形式.
        //   static readonly (不是 per-call 实例) — 避免每次 FillRoomData 都重市场编译，去 与 同文件 InvalidDefinedNameRegex / GroupHeaderRegex 一致。
        private static readonly Regex DirtSpecRegex = new Regex(
            @"\d+(?:[-*]\d+)?\s*MM[\)\uff09\s]*",
            RegexOptions.Compiled);

        // 🔧 v15 扩展 v14 trigger: 除 客餐厅 外, 阳台 / 外花园 也 同 走 special layout.
        //   理由: config.RoomTypeFallbackMap 已 让 阳台 / 外花园 fallback → 客餐厅 group (mudiban 模板中) —
        //     helper 内容 (rename 实木石砖线 → 地面找平 + 地面保护=0 + 墙面行 wallArea 与 RoomType 无关) 通用适用.
        //   其他 RoomType (主卧 / 卧室 / 厨房 / 卫生间 / 主卫) 不 触发 — 它们 没 「正铺地砖」+「地面保护」 同 group 形式,
        //     即便 user 选 NONE 也不需 该 layout (例 主卧 NONE 走 老 PHASE A 0-out; 厨房 NONE 不 动 防 套装错位).
        private static readonly HashSet<string> _v14TriggerRoomTypes = new HashSet<string>(StringComparer.Ordinal)
        {
            "客餐厅", "阳台", "外花园"
        };

        private string PreprocessInvalidDefinedNames(string srcPath)
        {
            if (!File.Exists(srcPath)) return srcPath;
            try
            {
                using (var zip = ZipFile.OpenRead(srcPath))
                {
                    var wbEntry = zip.GetEntry("xl/workbook.xml");
                    if (wbEntry == null) return srcPath;

                    string wbXml;
                    using (var stream = wbEntry.Open())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                        wbXml = reader.ReadToEnd();

                    if (!InvalidDefinedNameRegex.IsMatch(wbXml)) return srcPath;

                    string fixedXml = InvalidDefinedNameRegex.Replace(wbXml, string.Empty);
                    fixedXml = EmptyDefinedNamesRegex.Replace(fixedXml, string.Empty);

                    string tempPath = Path.Combine(Path.GetTempPath(), "baojia_" + Guid.NewGuid().ToString("N") + ".xlsx");
                    CopyZipWithReplacedWorkbook(srcPath, tempPath, fixedXml);
                    Debug("[模板预处理] 移除 invalid defined name 后写入临时文件: " + tempPath);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                Debug("[模板预处理] 失败，回退原文件: " + ex.Message);
                return srcPath;
            }
        }

        private static void CopyZipWithReplacedWorkbook(string srcPath, string dstPath, string newWbXml)
        {
            using (var src = ZipFile.OpenRead(srcPath))
            using (var dst = ZipFile.Open(dstPath, ZipArchiveMode.Create))
            {
                foreach (var entry in src.Entries)
                {
                    var newEntry = dst.CreateEntry(entry.FullName, entry.Length == 0 ? System.IO.Compression.CompressionLevel.NoCompression : System.IO.Compression.CompressionLevel.Optimal);
                    if (string.Equals(entry.FullName, "xl/workbook.xml", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var ws = newEntry.Open())
                        using (var sw = new StreamWriter(ws, new UTF8Encoding(false)))
                            sw.Write(newWbXml);
                    }
                    else
                    {
                        using (var rs = entry.Open())
                        using (var ws = newEntry.Open())
                            rs.CopyTo(ws);
                    }
                }
            }
        }

        private void SafeCopyFile(string srcPath, string dstPath)
        {
            const int maxRetries = 3;
            Exception lastEx = null;
            for (int i = 0; i < maxRetries; i++)
            {
                try
                {
                    File.Copy(srcPath, dstPath, overwrite: true);
                    return;
                }
                catch (IOException ex) { lastEx = ex; }
                catch (UnauthorizedAccessException ex) { lastEx = ex; }
                if (i < maxRetries - 1)
                {
                    System.Threading.Thread.Sleep(500);
                }
            }
            throw lastEx ?? new IOException("SafeCopyFile 未知异常");
        }

        // ====================================================================
        // 🔧 v7 多模板支持: per-floor template selection
        //   - ParseTemplateFromPath: 纯读 (open → ParseTemplate → close) 解析外部 xlsx 拿 groups.
        //     每个 group 带 SourceTemplate=tplName 标记.
        //   - BuildMultiTemplateScratchpad: 把非主模板 (非 1F) 的 prototype groups
        //     拷到输出 ws 的 scratchpad 区 (R{scratchpadStart}+) — 解决 ClosedXML
        //     跨 workbook CopyTo 丢格式 的问题 (proto group 必须在同一 wb 才能 CopyTo
        //     保留 formatting/row-height/formulas). 主模板 (1F) 的 groups 本来就在输出 ws,
        //     由 ParseTemplate(ws) 拿到, 不需 scratchpad.
        //   - scratchpadStart 由 ComputeScratchpadStart 算: chrome 最后一行的下一行 + 余量.
        //   - 单层 / 单模板模式 (floorTemplatePaths 空 或 只有 1 项) → 跳过 scratchpad, 走原 v6 路径.
        // ====================================================================

        private int ComputeScratchpadStart(IXLWorksheet ws, List<TemplateGroup> outputGroups)
        {
            // 取输出 ws 的 「最后行」 — 包括 chrome + 表尾隔行. 然后 +100 余量. 99.9% 模板 < 1000 行.
            int maxRow = 0;
            try { maxRow = ws.LastRowUsed()?.RowNumber() ?? 0; } catch { maxRow = 0; }
            int start = Math.Max(5000, maxRow + 100);  // 最低 5000 — 避免 0..200 撞上 chrome 区
            return start;
        }

        private List<TemplateGroup> ParseTemplateFromPath(string xlsxPath, string tplName, QuoteConfig config)
        {
            var groups = new List<TemplateGroup>();
            string raw = SafeCopyTemplateToTemp(xlsxPath);
            string pre = PreprocessInvalidDefinedNames(raw);
            try
            {
                using (var wb = new XLWorkbook(pre))
                {
                    var ws = wb.Worksheets.First();
                    groups = ParseTemplate(ws, config, tplName);
                }
            }
            finally
            {
                if (!string.Equals(pre, raw, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pre, xlsxPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(pre); } catch { }
                }
                if (!string.Equals(raw, xlsxPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(raw); } catch { }
                }
            }
            return groups;
        }

        // 🔧 v7 scratchpad 使用 cell-by-cell copy (而非 跨 wb Range.CopyTo):
        //   - ClosedXML 0.97 跨 wb CopyTo 在 stylesheet/theme 同步时抛 NullReferenceException
        //   - scratchpad 行 仅作为 后续 CloneGroupInPlace 的模板源 — 最终被删 — 不需保留原始 style.
        //   - 单纯复制 Value + FormulaA1 已 足供 prototype 克隆源 + scratchpad 后期删除.
        //   - 避免任何 跨 wb style sync 错误 + 无需 try-catch.
        private List<TemplateGroup> BuildMultiTemplateScratchpad(IXLWorksheet outputWs,
                                                                  Dictionary<string, string> floorTemplatePaths,
                                                                  QuoteConfig config,
                                                                  int scratchpadStart)
        {
            var added = new List<TemplateGroup>();
            if (floorTemplatePaths == null || floorTemplatePaths.Count == 0) return added;
            int cursor = scratchpadStart;
            var processedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in floorTemplatePaths)
            {
                string floor = kv.Key;
                string path = kv.Value;
                if (string.IsNullOrEmpty(path)) continue;
                if (processedPaths.Contains(path)) continue;
                processedPaths.Add(path);

                // 取模板名: 优先从 config.SelectedFloorTemplates 拿. 拿不到 fallback "unknown"
                string tplName = (config?.SelectedFloorTemplates != null
                                  && config.SelectedFloorTemplates.TryGetValue(floor ?? "", out var tn))
                                 ? tn : "unknown";

                var sourceGroups = ParseTemplateFromPath(path, tplName, config);
                if (sourceGroups == null || sourceGroups.Count == 0) continue;

                // 拷 prototype ranges 到 output ws 的 scratchpad (同 wb 内 CopyTo 保留格式)
                string sourceRaw = SafeCopyTemplateToTemp(path);
                string sourcePre = PreprocessInvalidDefinedNames(sourceRaw);
                try
                {
                using (var srcWb = new XLWorkbook(sourcePre))
                {
                    // 🔧 v7 防御: 用 FirstOrDefault 避免源 xlsx 无 worksheet 时抛 InvalidOperationException (损坏/空文件)
                    var srcWs = srcWb.Worksheets.FirstOrDefault();
                    if (srcWs == null)
                    {
                        Debug($"  [v7 scratchpad] ⚠ {Path.GetFileName(path)} 无 worksheet, 跳过该模板");
                        continue;
                    }                    foreach (var g in sourceGroups)
                    {
                        int span = g.SubtotalRow - g.HeaderRow + 1;
                        if (span < 1) continue;
                        int srcStart = g.HeaderRow;
                        int dstStart = cursor;
                        int shift = dstStart - srcStart;
                        // 🔧 v7 cell-by-cell copy (替代跨 wb Range.CopyTo):
                        //   - ClosedXML 0.97 跨 wb CopyTo NRE (stylesheet/theme sync 深层错误)
                        //   - scratchpad 行 仅 是 prototype 源 + 后期删 — 不需保留 原始 style
                        //   - 复制 Value + FormulaA1 足够 — FormulaA1 setter 保留 A1 字面量 (不重锚) ✓
                        for (int r = 0; r < span; r++)
                        {
                            for (int c = 1; c <= 9; c++)
                            {
                                var sc = srcWs.Cell(srcStart + r, c);
                                var dc = outputWs.Cell(dstStart + r, c);
                                if (sc.HasFormula) dc.FormulaA1 = sc.FormulaA1;
                                else if (sc.HasRichText)
                                {
                                    // 🔧 v7 防御: IXLRichText 跨 wb 写引用会绑定源 wb 的 internal rich-text 表,
                                    //   srcWb using 退出后该表失效 → wb.Save() NRE. 提取成纯 string.
                                    dc.Value = sc.GetRichText().Text;
                                }
                                else dc.Value = sc.Value;   // 数字 / 日期 / 文本 / bool 都是 XLCellValue struct 值类型, 跨 wb 安全.
                                // 🔧 v9.3: 复制关键样式 (Border + Solid Fill + Font.Bold) — 2F+ 走 secondary 模板(mudiban)
                                //   原本 只挪 Value, CloneGroupInPlace 后丢 边框与 小计灰底. 3 项合一 try-catch:
                                //   跨 wb Style 共享 一损 俱损 (v7 跨 wb Range.CopyTo NRE 教训), 但 合并后 调试 log 噪音 减 ⅔, 抓 1 个 NRE 看 cell 到位.
                                if (!sc.IsEmpty())
                                {
                                    try
                                    {
                                        // 🔧 v9.3 slim: Border 由 v9.4 达截 全列 强制 default, 这里 只 掘 Fill + Font.Bold.
                                        //   Fill: 仅 Solid 拷贝 — chrome 块 鲁铜鲁蓝 background 需 保留 (v9.4 不 覆).
                                        if (sc.Style.Fill.PatternType == XLFillPatternValues.Solid)
                                        {
                                            dc.Style.Fill.PatternType = XLFillPatternValues.Solid;
                                            dc.Style.Fill.BackgroundColor = sc.Style.Fill.BackgroundColor;
                                        }
                                        // Font.Bold: 只搬 Bold (其他 Font 字段 Color/Size/Family 跨 wb 风险高且 视觉冲击小)
                                        if (sc.Style.Font.Bold) dc.Style.Font.Bold = true;
                                    }
                                    catch (NullReferenceException ex) { Debug($"  [v9.3 style] sc={sc.Address} → dc={dc.Address} NRE-safe skip ({ex.Message}): {ex.GetType().Name}"); }
                                }
                            }
                            // 🔧 v9.5: row-level enterprise defaults — 全列 Border.Thin + 大类 (group header, r==0) 12pt bold + 小类 (item+subtotal) 11pt + 小计行 A-H 灰底
                            //   用户明确表达 “大类 12pt bold, 小类 11pt” — 不读 src 字体 (跨 wb Font 风险高 + 源 残), 强制按行位置 enterprise 换装.
                            //   rowOffset 用 g.SubtotalRow - g.HeaderRow 推:0=group header,中间=item,末=subtotal — 不需 c2 string 检测.
                            int targetRow = dstStart + r;
                            int subtotalOffset = g.SubtotalRow - g.HeaderRow;
                            bool isGroupHeader = (r == 0);
                            bool isSummary = (r == subtotalOffset);
                            try
                            {
                                // 1) Border: 全 9 列强制 Thin — 覆盖 mudiban/dizhuan/fushi 等 源 短缺
                                for (int c = 1; c <= 9; c++)
                                {
                                    var cell = outputWs.Cell(targetRow, c);
                                    cell.Style.Border.TopBorder = XLBorderStyleValues.Thin;
                                    cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
                                    cell.Style.Border.LeftBorder = XLBorderStyleValues.Thin;
                                    cell.Style.Border.RightBorder = XLBorderStyleValues.Thin;
                                }
                                // 2) Font: 大类 12pt bold, 小类 (item+subtotal) 11pt normal
                                double fontSize = isGroupHeader ? 12.0 : 11.0;
                                for (int c = 1; c <= 9; c++)
                                {
                                    var cell = outputWs.Cell(targetRow, c);
                                    cell.Style.Font.FontSize = fontSize;
                                    if (isGroupHeader) cell.Style.Font.Bold = true;
                                }
                                // 3) Subtotal/total 行 col 1..9 灰底 — A->I 全列 灰 (用户明确表达, 包括 备注栏 col I)
                                if (isSummary)
                                {
                                    for (int c = 1; c <= 9; c++)
                                    {
                                        var cell = outputWs.Cell(targetRow, c);
                                        cell.Style.Fill.PatternType = XLFillPatternValues.Solid;
                                        // ClosedXML 0.97: FromHtml 不可用, FromArgb 0xD9,D9,D9 = Excel 默认 LightGray
                                        cell.Style.Fill.BackgroundColor = XLColor.FromArgb(0xD9, 0xD9, 0xD9);
                                    }
                                }
                            }
                            catch (Exception ex) { Debug($"  [v9.5 force-default] row={targetRow} NRE-safe skip ({ex.Message}): {ex.GetType().Name}"); }
                        }
                        g.HeaderRow += shift;
                            g.SubtotalRow += shift;
                            foreach (var it in g.Items) it.Row += shift;
                            for (int i = 0; i < g.ExtraSubtotalRows.Count; i++)
                                g.ExtraSubtotalRows[i] += shift;

                            cursor += span;
                            added.Add(g);
                        }
                    }
                }
                finally
                {
                    if (!string.Equals(sourcePre, sourceRaw, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(sourcePre, path, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(sourcePre); } catch { }
                    }
                    if (!string.Equals(sourceRaw, path, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(sourceRaw); } catch { }
                    }
                }
            }
            Debug($"  [v7 scratchpad] 从 {processedPaths.Count} 个非主模板拷 {added.Count} 组到 R{scratchpadStart}..R{cursor - 1}");
            return added;
        }

        // ====================================================================
        // 表格写入辅助 (ClosedXML API)
        // ====================================================================

        private static string CellString(IXLCell cell)
        {
            if (cell == null) return "";
            // 使用 GetString() 代替 cell.Value.IsBlank 路径:
            //   - 在 ClosedXML 0.97.x (0.100 之前) cell.Value 返回 object, IsBlank 不存在
            //   - GetString() 跨 0.95 / 0.97 / 0.100 / 0.102 都是稳定接口, 返回 "" 表示空白
            return cell.GetString() ?? "";
        }

        private void SetProjectName(IXLWorksheet ws, string projectName)
        {
            int maxRow = GetLastRow(ws);
            for (int row = 1; row <= maxRow; row++)
            {
                if (ws.Cell(row, 1).Value.ToString().Contains("工程名称"))
                {
                    ws.Cell(row, 1).Value = $"工程名称：{projectName}";
                    Debug($"  工程名称已设置: R{row}");
                    return;
                }
            }
        }

        private int GetLastRow(IXLWorksheet ws)
        {
            try
            {
                return ws.LastRowUsed()?.RowNumber() ?? 200;
            }
            catch
            {
                return 200;
            }
        }

        // ====================================================================
        // 模板解析: 把 Excel 结构 (header / items / subtotal / chrome / 合计) 抽到内存 TemplateGroup
        // ====================================================================

        private List<TemplateGroup> ParseTemplate(IXLWorksheet ws, QuoteConfig config, string tplName = "")
        {
            var groups = new List<TemplateGroup>();
            TemplateGroup currentGroup = null;
            int maxRow = GetLastRow(ws);
            // chrome (八 综合 / 七 直接费 / 九 其它 起, 直至表末尾) 视为静态块 — ParseTemplate
            // 扫到第一个 chrome 触发行立即 break, 不再当 TemplateGroup 入 groups.
            // 这条规则取代旧的"inChromeMode 持续累积 chrome 子段"模式, 是消除 248/77 行
            // 视觉空白带的根因: 不再解析 chrome → 不再 Clear+Hide 原型区失败留下空白.
            int chromeStartRow = 0;

            for (int row = 8; row <= maxRow; row++)
            {
                var c1 = CellString(ws.Cell(row, 1)).Trim();
                var c2 = CellString(ws.Cell(row, 2)).Trim();

                if (chromeStartRow == 0 && IsHouseLevelMarker(c1, c2))
                {
                    chromeStartRow = row;
                    Debug($"  [ParseTemplate 终止] chrome 静态块起点 R{row} col1=[{c1}] col2=[{c2}] — 后续直至末尾不再入 groups");
                    break;
                }

                if (IsGroupHeader(c1))
                {
                    currentGroup = new TemplateGroup
                    {
                        HeaderRow = row,
                        Name = c2,
                        RoomType = ClassifyRoom(c2, c2, config),
                        FloorLevel = ExtractFloorLevel(c2, config),
                        // chrome 段已 break — 这里所有 group 都是房间原型, ChromeOnly 恒为 false.
                        ChromeOnly = false,
                        // 🔧 v7: 标记该组来自哪个 xlsx. ParseTemplateFromPath 调用时传入 tplName;
                        //   v6 走原 Export 路径时 tplName="" 兼容.
                        SourceTemplate = tplName,
                    };
                    groups.Add(currentGroup);
                    continue;
                }

                if (c1 == "" && c2.Contains("小计"))
                {
                    if (currentGroup != null)
                    {
                        if (currentGroup.SubtotalRow == 0)
                            currentGroup.SubtotalRow = row;
                        else
                            currentGroup.ExtraSubtotalRows.Add(row);
                    }
                    continue;
                }

                if (c1 == "" && c2.Contains("合计"))
                    break;

                if (currentGroup != null && !string.IsNullOrEmpty(c1) && int.TryParse(c1, out _))
                {
                    currentGroup.Items.Add(new TemplateItem
                    {
                        Row = row,
                        Name = c2,
                        Unit = CellString(ws.Cell(row, 4)).Trim(),
                    });
                }
            }

            return groups;
        }

        private static readonly (string Col1, string Prefix)[] HouseLevelMarkers = new[]
        {
            ("七", "直接费"),
            ("八", "综合"),
            ("九", "其它"),
        };

        private bool IsHouseLevelMarker(string col1, string col2)
        {
            if (string.IsNullOrEmpty(col1) || string.IsNullOrEmpty(col2)) return false;
            foreach (var m in HouseLevelMarkers)
                if (col1 == m.Col1 && col2.StartsWith(m.Prefix, StringComparison.Ordinal))
                    return true;
            return false;
        }

        private string ExtractFloorLevel(string text, QuoteConfig config)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var aliasMap = config?.TemplateSettings?.FloorAliasMap;
            if (aliasMap == null || aliasMap.Count == 0) return "";
            var match = Regex.Match(text, @"(?:^\s*|[^一-龥\d])(1F|2F|3F|4F|5F|一楼|二楼|三楼|四楼|五楼|一层|二层|三层|首层)(?=\s|$|[^一-龥\d])");
            if (!match.Success) return "";
            string raw = match.Groups[1].Value;
            return aliasMap.TryGetValue(raw, out var norm) ? norm : raw;
        }

        private string ClassifyRoom(string text, string fallback, QuoteConfig config)
        {
            if (string.IsNullOrWhiteSpace(text)) return fallback;
            foreach (var map in config.RoomTypeMaps)
                foreach (var kw in map.Keywords)
                    if (text.Contains(kw)) return map.RoomType;
            return fallback;
        }

        // ====================================================================
        // 核心: ProcessRooms — 在原工作表上原地写，靠 InsertRowsAbove 拉空位
        // ====================================================================

        private void ProcessRooms(IXLWorksheet ws, List<Room> rooms,
            List<TemplateGroup> templateGroups, QuoteConfig config, int scratchpadStart = -1,
            List<TemplateGroup> scratchpadGroups = null)
        {
            if (templateGroups.Count == 0) return;
            if (rooms == null || rooms.Count == 0) return;

            var prototypes = templateGroups.ToList();
            // 🔧 v7 关键修复: protoSpan 必须仅基于 1F 原型 (HeaderRow < scratchpadStart).
            //   原逻辑 用 roomPrototypes = prototypes.ToList() 包含 scratchpad 组, 会把 protoSpan 拉到 scratchpad 后 (5000+),
            //   Phase 5 Rows.Delete(origProtoStart, origProtoEnd) 跨 scratchpad 把整张表 + scratchpad + chrome 全部清空,
            //   wb.Save() 内部 xml 链断裂 → NullReferenceException.
            var roomPrototypes = scratchpadStart > 0
                ? prototypes.Where(g => g.HeaderRow < scratchpadStart).ToList()
                : prototypes;
            if (roomPrototypes.Count == 0)
            {
                Debug("  ⚠ [ProcessRooms] 没 1F 原型, 早退");
                return;
            }
            int protoStart = roomPrototypes.First().HeaderRow;

            // protoSpan 只覆盖 1F 原型 (chrome 由 Delete 顶上来, 不需它的长度)
            int roomBlockEnd = protoStart;
            int validSubtotalCount = 0;
            foreach (var g in roomPrototypes)
            {
                int candidateEnd = Math.Max(g.SubtotalRow, g.HeaderRow);
                if (candidateEnd <= 0) continue;
                if (candidateEnd > roomBlockEnd) roomBlockEnd = candidateEnd;
                if (g.SubtotalRow > g.HeaderRow) validSubtotalCount++;
            }
            int protoSpan = roomBlockEnd - protoStart + 1;
            if (validSubtotalCount < roomPrototypes.Count)
                Debug($"  [protoSpan 诊断] 异常: roomPrototypes={roomPrototypes.Count}, 有效小计={validSubtotalCount}, protoSpan={protoSpan}");

            if (protoStart <= 0)
            {
                Debug($"  ⚠ protoStart={protoStart} 不合法, ProcessRooms 早退 — 不输出任何房间块, chrome 静态块保留原位");
                templateGroups.Clear();
                return;
            }

            // 模板破损早退: 真删原型区行 — chrome 顺势顶到 protoStart,不留可见空白带
            //   (与主路径一致用 Rows().Delete(), 不与主路径重复 Hide-失效 风险)
            if (protoSpan < 1)
            {
                int relativeSpan = roomBlockEnd - protoStart + 1;
                int safeSpan = roomPrototypes.Count > 0 ? Math.Max(1, relativeSpan) : 0;
                if (safeSpan > 0)
                    ws.Rows(protoStart, protoStart + safeSpan - 1).Delete();
                templateGroups.Clear();
                Debug("⚠ [ProcessRooms 早退] 模板 room prototypes 缺『小计』行 — 不输出任何房间块, chrome 静态块顶到 protoStart");
                return;
            }

            // 楼层×类型 模板查找字典 (含 1F 原型 + scratchpad 组)
            // 🔧 v7: 三维 key (SourceTemplate, FloorLevel, RoomType) — 复式多模板时 不同层可同 roomType 但
            //   走不同 xlsx 模板 (e.g. 1F|客 走 dizhuan, 2F|客 走 mudiban). tplDict 严格分流.
            //   v6 单层 / 单模板 时 所有 group 都有 SourceTemplate="" 或 同名 → 退化为二维 (隐式兼容).
            //   选 prototypes (包括 scratchpad), 跨模板 lookup 才会命中 scratchpad 组.
            var tplDict = prototypes
                .Where(g => g.SubtotalRow > g.HeaderRow)
                .GroupBy(g => (g.SourceTemplate ?? "", g.FloorLevel, g.RoomType))
                .ToDictionary(g => g.Key, g => g.ToList());

            string NormFloor(string f) => string.IsNullOrWhiteSpace(f) ? "未指定" : f;
            var floorSet = rooms.Select(r => NormFloor(r.FloorLevel)).Distinct()
                .OrderBy(f => FloorOrderKey(f)).ToList();
            floorSet = floorSet.Where(f => f != "未指定").Concat(floorSet.Where(f => f == "未指定")).ToList();
            bool multiFloor = floorSet.Count(f => f != "未指定") > 1;

            var catOrder = CategoryPanel.SixCats.Concat(new[] { "其他" }).ToList();

            // 阶段 1: 仿真计算 totalRequiredRows
            int totalRequiredRows = 0;
            int matchedRoomCount = 0;
            foreach (var floor in floorSet)
            {
                if (multiFloor && floor != "未指定") totalRequiredRows += 1;
                var floorRooms = rooms.Where(r => NormFloor(r.FloorLevel) == floor).ToList();
                foreach (var cat in catOrder)
                {
                    var catRooms = floorRooms
                        .Where(r => CategoryPanel.MapToSixCategory(r.Name, config) == cat)
                        .OrderBy(r => r.Name, StringComparer.Ordinal)
                        .ToList();
                    foreach (var room in catRooms)
                    {
                        // 🔧 v7: 根据该层的面板选择 决定 sourceTpl — 复式多模板关键路径.
                        string sourceTpl = LookupSourceTemplateForFloor(room.FloorLevel, config);
                        var tplList = ResolveTemplates(sourceTpl, room.FloorLevel, room.RoomType, tplDict, roomPrototypes, config);
                        if (tplList != null && tplList.Count > 0)
                        {
                            int span = tplList[0].SubtotalRow - tplList[0].HeaderRow + 1;
                            if (span > 0)
                            {
                                totalRequiredRows += span;
                                matchedRoomCount++;
                            }
                        }
                        else
                        {
                            Debug($"  类型 [{room.RoomType}] 楼层 [{room.FloorLevel}] (源模板={sourceTpl}): 模板中无匹配分组，跳过房间 [{room.Name}]");
                        }
                    }
                }
            }
            Debug($"  仿真计算: 共需 {totalRequiredRows} 行 (匹配 {matchedRoomCount} 个房间)");

            if (matchedRoomCount == 0)
            {
                // 无匹配房间: 真删原型区行 — chrome 静态块顺势顶到 protoStart
                //   (与主路径一致用 Rows().Delete(), 避免 Hide-失效 风险)
                ws.Rows(protoStart, protoStart + protoSpan - 1).Delete();
                templateGroups.Clear();
                Debug("  ⚠ 没有匹配任何模板的房间，仅输出 chrome 静态块(从 protoStart 开始,原位无原型区)");
                return;
            }

            // 阶段 2: InsertRowsAbove 在原工作表 R{protoStart} 拉空位
            //   - R8..R(7+totalRequiredRows) 变成空白 (供房间克隆用)
            //   - 老 prototypes/chrome 自动下移 +totalRequiredRows
            //   - Logo 与 drawings 在 InsertRows 内不会被破坏
            ws.Row(protoStart).InsertRowsAbove(totalRequiredRows);
            Debug($"  已插入 {totalRequiredRows} 空白行 at R{protoStart} (ClosedXML 保留 drawings)");

            // 阶段 3: 更新所有 templateGroup 的 row 偏移 (InsertRowsAbove 把旧内容整体下移)
            //   🔧 v7 修复: 选 prototypes 不是 roomPrototypes — scratchpad 组 也会被插入行 下推 totalRequiredRows,
            //   不更新他们的 refs 会 让他们 refs 错位 后续 CloneGroupInPlace 找不到源。
            int shift = totalRequiredRows;
            foreach (var g in prototypes)
            {
                g.HeaderRow += shift;
                g.SubtotalRow += shift;
                foreach (var it in g.Items) it.Row += shift;
                if (g.ExtraSubtotalRows != null)
                    for (int i = 0; i < g.ExtraSubtotalRows.Count; i++)
                        g.ExtraSubtotalRows[i] += shift;
            }
            // 注: chromeGroups 已不存在, 不再需要 chrome 区行偏移维护
            //     (chrome 静态块整段移动, 公式相对引用保持不变)

            // 阶段 4: 逐房间在空白区写入克隆
            int writeCursor = protoStart;
            var generatedGroups = new List<TemplateGroup>();

            foreach (var floor in floorSet)
            {
                bool emitFloorHeader = multiFloor && floor != "未指定";
                if (emitFloorHeader)
                {
                    ws.Range(writeCursor, 1, writeCursor, 9).Merge();
                    ws.Cell(writeCursor, 1).Value = floor;
                    ws.Cell(writeCursor, 1).Style.Font.Bold = true;
                    ws.Cell(writeCursor, 1).Style.Font.FontSize = 14;
                    ws.Cell(writeCursor, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Row(writeCursor).Height = 25;
                    writeCursor += 1;
                }

                var floorRooms = rooms.Where(r => NormFloor(r.FloorLevel) == floor).ToList();

                foreach (var cat in catOrder)
                {
                    var catRooms = floorRooms
                        .Where(r => CategoryPanel.MapToSixCategory(r.Name, config) == cat)
                        .OrderBy(r => r.Name, StringComparer.Ordinal)
                        .ToList();
                    foreach (var room in catRooms)
                    {
                        // 🔧 v7: per-floor template 分流 — 1F 走 dizhuan groups (输出 ws 内), 2F 走 mudiban groups (scratchpad 内).
                        string sourceTpl = LookupSourceTemplateForFloor(room.FloorLevel, config);
                        var tplList = ResolveTemplates(sourceTpl, room.FloorLevel, room.RoomType, tplDict, roomPrototypes, config);
                        if (tplList == null || tplList.Count == 0) continue;

                        var sourceGroup = tplList[0];
                        int sourceSpan = sourceGroup.SubtotalRow - sourceGroup.HeaderRow + 1;
                        var newGroup = CloneGroupInPlace(ws, sourceGroup, writeCursor);
                        if (newGroup == null) continue;
                        newGroup.RoomType = room.RoomType;
                        newGroup.FloorLevel = room.FloorLevel;

                        ws.Cell(newGroup.HeaderRow, 2).Value = room.Name;
                        Debug($"  房间 [{room.Name}] 楼层 [{room.FloorLevel}] 类 [{cat}] -> 模板 [{newGroup.Name}]");

                        FillRoomData(ws, newGroup, room, config);
                        generatedGroups.Add(newGroup);
                        writeCursor += sourceSpan;
                    }
                }
            }

            // 阶段 5: 真删旧原型区 — chrome 静态块从 R(protoStart+N+protoSpan) 顺势顶到 R(protoStart+N)
            //         与房间无缝拼接(消除 248/77 行视觉空白带).
            //   - 旧路径 Clear+Hide 在 ClosedXML 0.97 上 Hide 不住 Clear 过的行, 残留可见
            //   - 改成 Rows().Delete() 真从 XML 删行, chrome 区绝对连续, 无空白残留
            int origProtoStartAfterInsert = protoStart + totalRequiredRows;
            int origProtoEndAfterInsert = origProtoStartAfterInsert + protoSpan - 1;
            ws.Rows(origProtoStartAfterInsert, origProtoEndAfterInsert).Delete();
            Debug($"  原型区 R{origProtoStartAfterInsert}..R{origProtoEndAfterInsert} ({protoSpan} 行) 已 Delete — chrome 静态块自动顶到 R{protoStart + totalRequiredRows}");

            // 🔧 v7 阶段 5.5: scratchpad group refs 需同步下推 protoSpan 行 (因 Phase 5 Delete 把这些行上推了).
            //   scratchpad 行在 ws 中实际位置: scratchpadStart + totalRequiredRows (Phase 3 InsertRowsAbove 同步下推) - protoSpan (Phase 5 Delete 同步上推).
            //   scratchpadGroups 字段是 Phase 3 后值 (未含 Phase 5 shift). 这里补 -protoSpan 让它跟上 ws 实际位置.
            if (scratchpadGroups != null && scratchpadGroups.Count > 0)
            {
                foreach (var sg in scratchpadGroups)
                {
                    sg.HeaderRow -= protoSpan;
                    sg.SubtotalRow -= protoSpan;
                    foreach (var it in sg.Items) it.Row -= protoSpan;
                    if (sg.ExtraSubtotalRows != null)
                        for (int i = 0; i < sg.ExtraSubtotalRows.Count; i++)
                            sg.ExtraSubtotalRows[i] -= protoSpan;
                }
                Debug($"  v7 scratchpad refs -protoSpan (Phase 5 Delete shift) 同步完成 ({scratchpadGroups.Count} 组)");
            }

            // 阶段 6+7 已删除:
            //   - FixChromeFormulas 不再调用 — chrome 区视为单一静态块, 模板自带的子段公式不动
            //   - chrome "其它"段 col3/5/6/7/8 Clear 不再调用 — 模板里这些列本就是空(预留用户填)

            templateGroups.Clear();
            templateGroups.AddRange(generatedGroups);

            // 🔧 v10: Phase 8 (PrintArea) 后置 到 Phase 9 (scrap cleanup) 之后 — 旧顺序 LastRowUsed() 含 scratchpad (R5000+) → PrintArea 包含 R1:R5000+ 空白 → 用户 每次 要 手点取消 打印区
            //   顺序 修正 后, Phase 9 先 删 scratchpad 行, Phase 8 再 跑 LastRowUsed() = 真实内容末行 (= chrome末行 与 房末写 取 max), print 区域 与 报告 内容 一致.
            //   该代码块 本轮 删除, 见 下方 在 Phase 9 后 重新插入
            // 🔧 v7 阶段 9: 清理 scratchpad — 复式多模板时 R{scratchpadStart}+ 有拷进来的外部模板原型区,
            //   现在全部房间已 clone 完, 这些暂存区可删 (避免用户滚到下面看到他们).  计算需删的区段:
            //   scratchpadStart 已被 InsertRowsAbove 上推 +totalRequiredRows → 仍远在原型区下方, Delete 不影响 chrome.
            // 🔧 v7 阶段 9: 清理 scratchpad — 复式多模板时 R{scratchpadStart}+ 有拷进来的外部模板原型区,
            //   现在全部房间已 clone 完, 这些暂存区可删 (避免用户滚到下面看到他们).
            //   scratchpad 在 ws 中实际位置: scratchpadStart + totalRequiredRows - protoSpan
            //     (Phase 3 InsertRowsAbove 下推 +totalRequiredRows, Phase 5 Delete 上推 protoSpan).
            //   scratchpadGroups 字段已在 Phase 5.5 同步 (减过 protoSpan). 直接读即可.
            if (scratchpadGroups != null && scratchpadGroups.Count > 0 && scratchpadStart > 0)
            {
                int padStart = scratchpadStart + totalRequiredRows - protoSpan;
                int padEnd = padStart;
                foreach (var g in scratchpadGroups)
                {
                    if (g.SubtotalRow > padEnd) padEnd = g.SubtotalRow;
                }
                if (padEnd >= padStart)
                {
                    try
                    {
                        ws.Rows(padStart, padEnd).Delete();
                        Debug($"  v7 scratchpad R{padStart}..R{padEnd} 已 Delete (清理 {padEnd - padStart + 1} 行)");
                    }
                    catch (Exception spEx)
                    {
                        Debug($"  v7 scratchpad Delete 失败 (非致命): {spEx.Message}");
                    }
                }
            }

            // 🔧 v10: Phase 8 (PrintArea) 后置 — 在 Phase 9 scratchpad cleanup 后 跑, LastRowUsed() = 真内容末行 (不含 R5000+ scratchpad).
            //   产品 老路径 Phase 8 在 Phase 9 前 → LastRowUsed 看到 scratchpad 内容 → PrintArea = A1:I{5200+} → Excel 打印时 多 50+ 页空白.
            //   实现 是 “默认 安全” + “保守” 双面: writeCursor-1 (本地 tracker) 与 LastRowUsed() (post-cleanup ws 真实) 取 max — 防 chrome 多行 vs content 多行 两个一边偏低.
            try
            {
                int contentMax = Math.Max(writeCursor - 1, 0);
                int newMaxRow = ws.LastRowUsed()?.RowNumber() ?? contentMax;
                if (newMaxRow < contentMax) newMaxRow = contentMax;
                ws.PageSetup.PrintAreas.Clear();
                ws.PageSetup.PrintAreas.Add($"A1:I{newMaxRow}");
                Debug($"  PrintArea 已重设为 A1:I{newMaxRow} (v10 后置: Phase 9 scratchpad 已清, LastRowUsed() = 真末行)");
            }
            catch (Exception paEx)
            {
                Debug($"  PrintArea 重设失败 (非致命): {paEx.Message}");
            }
        }

        // ====================================================================
        // 单个房间克隆: 在临时 InsertRows 拉出的空白范围内写入
        //   - Range.CopyTo 从 source (现在位于 旧位置+totalRequiredRows) 到 writeCursor (protoStart)
        //   - FixClonedFormulas 重写 col6/8 与 subtotal 公式 (克隆内相对行号)
        // ====================================================================

        private TemplateGroup CloneGroupInPlace(IXLWorksheet ws, TemplateGroup source, int insertAt)
        {
            if (source.SubtotalRow <= source.HeaderRow)
            {
                Debug($"  [!] 跳过复制 [{source.Name}]: 分组无小计行");
                return null;
            }
            int totalRows = source.SubtotalRow - source.HeaderRow + 1;

            ws.Range(source.HeaderRow, 1, source.SubtotalRow, 9)
                .CopyTo(ws.Cell(insertAt, 1));

            // 同步行高
            for (int r = 0; r < totalRows; r++)
            {
                try
                {
                    ws.Row(insertAt + r).Height = ws.Row(source.HeaderRow + r).Height;
                }
                catch { }
            }

            // 重写克隆区域内所有公式, 保证每行引用自己的行号
            FixClonedFormulas(ws, insertAt, totalRows);

            var clone = new TemplateGroup
            {
                HeaderRow = insertAt,
                SubtotalRow = insertAt + totalRows - 1,
                Name = source.Name,
                RoomType = source.RoomType,
                FloorLevel = source.FloorLevel,
            };

            int rowOffset = insertAt - source.HeaderRow;
            foreach (var item in source.Items)
            {
                clone.Items.Add(new TemplateItem
                {
                    Row = item.Row + rowOffset,
                    Name = item.Name,
                    Unit = item.Unit,
                });
            }

            return clone;
        }

        private void FixClonedFormulas(IXLWorksheet ws, int headerRow, int totalRows)
        {
            int firstItemRow = headerRow + 1;
            int subtotalRow = headerRow + totalRows - 1;
            int lastItemRow = subtotalRow - 1;

            for (int r = firstItemRow; r <= lastItemRow; r++)
            {
                ws.Cell(r, 6).FormulaA1 = $"=C{r}*E{r}";
                ws.Cell(r, 8).FormulaA1 = $"=C{r}*G{r}";
            }

            if (lastItemRow >= firstItemRow)
            {
                ws.Cell(subtotalRow, 6).FormulaA1 = $"=SUM(F{firstItemRow}:F{lastItemRow})";
                ws.Cell(subtotalRow, 8).FormulaA1 = $"=SUM(H{firstItemRow}:H{lastItemRow})";
            }
        }

        // (FixChromeFormulas 已删除 — chrome 区视为静态块, 模板自带公式原封不动)

        // ====================================================================
        // 模板查找 + 兜底 (逻辑不变, 仅闭包参数列表改 List 引用)
        // ====================================================================

        private List<TemplateGroup> ResolveTemplates(
            string sourceTpl, string cadFloor, string cadType,
            Dictionary<(string Tpl, string Floor, string Type), List<TemplateGroup>> tplGroups,
            List<TemplateGroup> allGroups, QuoteConfig config)
        {
            // 🔧 v7: 三维 key (Tpl, Floor, Type) 严格分流 — 不同模板不混。优先级:
            //   1. (sourceTpl, cadFloor, cadType)           — 严格命中
            //   2. (sourceTpl, "", cadType)                 — 源模板内 通用 (不需层前缀)
            //   3. (sourceTpl, cadFloor, fallbackType)      — 源模板 + 房间类型回退 (eg 阳台→客餐厅)
            //   4. (sourceTpl, "", fallbackType)           — 源模板 + 通用回退
            //   5. ("", cadFloor, cadType)                  — v6 兑底: 不分模板 按 (floor, type) 查
            //   6. ("", "", cadType)                        — v6 兑底: 完全通用
            //   7. allGroups 中 SourceTemplate==sourceTpl + RoomType==cadType 的 fallback (兑底)
            string st = sourceTpl ?? "";

            // 1.
            if (!string.IsNullOrEmpty(cadFloor)
                && tplGroups.TryGetValue((st, cadFloor, cadType), out var exact) && exact.Count > 0)
                return exact;
            // 2.
            if (tplGroups.TryGetValue((st, "", cadType), out var flat) && flat.Count > 0)
            {
                Debug($"  源模板 [{st}] 楼层 [{cadFloor}] 命中通用模板: {cadType}");
                return flat;
            }

            var fbMap = config?.TemplateSettings?.RoomTypeFallbackMap;
            string fallbackType = null;
            // 🔧 v7 防御: cadType 为 null/空时 Dictionary.TryGetValue 抛 ArgumentNullException (会被 catch 接 化成 自定义错误对话,
            //   但原 v6 这里没有 guard — JSON deserializer 可能 进 null fbMap value 进 dict).
            if (fbMap != null && !string.IsNullOrEmpty(cadType) && fbMap.TryGetValue(cadType, out var fb))
                fallbackType = fb;
            if (fallbackType == null && (cadType == "阳台" || cadType == "外花园"))
                fallbackType = "客餐厅";
            if (fallbackType != null)
            {
                // 3.
                if (!string.IsNullOrEmpty(cadFloor)
                    && tplGroups.TryGetValue((st, cadFloor, fallbackType), out var fbExact) && fbExact.Count > 0)
                {
                    Debug($"  源模板 [{st}] 房间类型 [{cadType}] 回退到 [{fallbackType}] 模板组");
                    return fbExact;
                }
                // 4.
                if (tplGroups.TryGetValue((st, "", fallbackType), out var fbFlat) && fbFlat.Count > 0)
                {
                    Debug($"  源模板 [{st}] 房间类型 [{cadType}] 回退到 [{fallbackType}] 通用模板");
                    return fbFlat;
                }
            }

            // 5. v6 兑底: 不带 SourceTemplate
            if (!string.IsNullOrEmpty(cadFloor)
                && tplGroups.TryGetValue(("", cadFloor, cadType), out var v6Exact) && v6Exact.Count > 0)
                return v6Exact;
            // 6.
            if (tplGroups.TryGetValue(("", "", cadType), out var v6Flat) && v6Flat.Count > 0)
            {
                Debug($"  v6 兑底: 源模板 [{st}] 无 {cadType} 模板, 走 v6 通用 (key=[\"\",\"\",\"{cadType}\"])");
                return v6Flat;
            }

            // 7. allGroups 兑底 (源模板 + 楼层 不命中, 源模板 + 类型 不命中 → 从 allGroups 扫同源模板 + 同类型的任何楼层)
            var sameTplSameType = allGroups
                .Where(g => (g.SourceTemplate ?? "") == st && g.RoomType == cadType && g.SubtotalRow > g.HeaderRow)
                .ToList();
            if (sameTplSameType.Count > 0)
            {
                Debug($"  源模板 [{st}] 类型 [{cadType}] 兑底: 走同模板同类型的任何楼层");
                return sameTplSameType;
            }

            // 8. 最后兑底: 忽略 SourceTemplate 随便拿一个同 cadType 的 group (仅 CAD 无楼层 时)
            if (string.IsNullOrEmpty(cadFloor))
            {
                var flatNoCad = allGroups
                    .Where(g => g.RoomType == cadType && g.SubtotalRow > g.HeaderRow && string.IsNullOrEmpty(g.FloorLevel))
                    .ToList();
                if (flatNoCad.Count > 0) return flatNoCad;

                var oneF = allGroups
                    .Where(g => g.RoomType == cadType && g.SubtotalRow > g.HeaderRow && g.FloorLevel == "一楼")
                    .ToList();
                if (oneF.Count > 0)
                {
                    Debug($"  CAD无楼层, 回退到模板一楼: {cadType}");
                    return oneF;
                }
                var first = allGroups
                    .Where(g => g.RoomType == cadType && g.SubtotalRow > g.HeaderRow && !string.IsNullOrEmpty(g.FloorLevel))
                    .OrderBy(g => g.FloorLevel)
                    .ToList();
                if (first.Count > 0)
                {
                    Debug($"  CAD无楼层, 回退到模板首个楼层 [{first[0].FloorLevel}]: {cadType}");
                    return first;
                }
            }
            return null;
        }

        /// <summary>🔧 v7 根据房间的楼层查该层面板选的模板. 单层模式 (SelectedFloorTemplates 空) 返回 "" 走 v6 路径.</summary>
        private string LookupSourceTemplateForFloor(string floorLevel, QuoteConfig config)
        {
            if (config?.SelectedFloorTemplates == null) return "";
            if (string.IsNullOrEmpty(floorLevel)) return "";
            if (config.SelectedFloorTemplates.TryGetValue(floorLevel, out var t) && !string.IsNullOrEmpty(t))
                return t;
            return "";
        }

        // ====================================================================
        // 🔧 v14 (v15 扩展): 客餐厅 / 阳台 / 外花园 + mudiban 模板 + "用户选 <无>" → special layout.
        //   逻辑: 房型 ∊ {客餐厅,阳台,外花园} + 模板 mudiban + user 选 "<无>" → 不走 PHASE A/B/C 默认路径,
        //     而是: “客厅及餐厅正铺地砖” (或 “原地” 或 “铺地砖”) 行 改名 “地面找平” + 填 C3 = room.FloorArea;
        //     “地面保护” 行 填 C3 = 0; 其他 (墙面类) 走 IsWallItem 原逻辑.
        //   def 动机: mudiban 模板中 这 3 类房 groups 通常只有 “正铺地砖” + “地面保护” 2 row, 没 “地面找平”,
        //     这 状态下业主说 “没贴砖” = 取消地砖, 但需保留 找平 + 不需保护 = 原 spec 这 2 row 错位.
        //   v15 扩展: 阳台 / 外花园 走 Special 是因 RoomTypeFallbackMap 它们 fallback 至 客餐厅 group,
        //     helper 内容 与 RoomType 无关, 仅 改写 「正铺地砖」行 + 「地面保护」行 — 通用 适用.
        //   採 user 概率低但 要能选 — v14 加; v15 同步 阳/外 — user 报 “阳台 / 外花园 选 NONE 不走” 后 加 ).
        // ====================================================================
        private int ApplyLivingRoomNoneMudiban(IXLWorksheet ws, TemplateGroup group, Room room, QuoteConfig config)
        {
            int filled = 0;
            double floorArea = Math.Round(room.FloorArea, 1, MidpointRounding.AwayFromZero);

            // 🔧 v14 fix(reviewer 1): C9 marker 用 PHASE A 同模式 — 保留 原 备注 + 仅 strip 旧 v14 marker 再 加 新.
            Action<int, string> writeMarker = (row, marker) =>
            {
                string rawDesc = CellString(ws.Cell(row, 9)) ?? string.Empty;
                string cleaned = Regex.Replace(rawDesc, @"\n?【v14 mudiban 客None:.*?】", string.Empty, RegexOptions.Singleline).TrimEnd('\n', '\r');
                ws.Cell(row, 9).Value = (cleaned.Length == 0 ? marker : cleaned + "\n" + marker);
            };
            // 🔧 v14 fix(reviewer 2): existing 「地面找平」 detection FIRST — 修 CS0841 (之前 是 另一个 后序 块调用了 existingFloorLevelingRow 才 声明)。
            TemplateItem existingFloorLevelingRow = group.Items.FirstOrDefault(i =>
                !string.IsNullOrEmpty(i.Name) && i.Name.Contains("地面找平"));
            // 🔧 v14.2/14.5 fix: dirtRow via static DirtSpecRegex (避免 「600MM 圆形实木石砖线（300-800MM）」 / 「瓷质玻化砖（800*800MM）」 等 nomes 不含 「正铺」/「铺地砖」/「客厅」 的 多个 名字 形式).
            //   「处理」 dropped — 不 误中 「倒角处理」/「磨光处理」 等 tile 相关 行 (这些 应 改名/填 ㎡ 而 不 是 错中)。
            //   🔧 v14.5 fix (user 反馈 「错行被改了」): DirtSpecRegex 路径 加 材质 keyword gate (砖/瓷/玻/石/木) —
            //     不 误中 「5MM 圆角倒角」/「5MM 打磨处理」 等 含 MM 但 不 是 真 地砖 的 sub-spec 字符串。 之前 「普通艺术石渍玻线（300-800MM）」 会被 误抓 (在地砖行 以上)
            //     然后 被 FirstOrDefault 选 中 — 造成 「错行改了」 现象。 现在 需 名 字 含 至少 一个 材质 keyword 才 可 候选。
            // 🔧 v14.6: FirstOrDefault → LastOrDefault — 在 mudiban 客 group 中 双多形 (e.g. 「普通艺术石渍玻线（300-800MM）」 + 「800MM圆形实木石砖线（300-800MM）」 都 match 材料+MM),
            //   主 规格 row 通常是 group 中末位。原 FirstOrDefault 错 中 前行, 用户 已 报 「错行被改」. 现 取 末位 = 真 dirt row.
            TemplateItem dirtRow = group.Items.LastOrDefault(i =>
                !string.IsNullOrEmpty(i.Name)
                && !i.Name.Contains("找平") && !i.Name.Contains("保护")
                && !i.Name.Contains("防水") && !i.Name.Contains("墙面") && !i.Name.Contains("顶面")
                && !i.Name.Contains("乳胶漆") && !i.Name.Contains("腻子") && !i.Name.Contains("基层")
                && !i.Name.Contains("勾缝")
                && (
                    // 1) canonical 命名 - 已 隐 含 含 「砖」/「抹地」 等材类
                    i.Name.Contains("正铺地砖") || i.Name.Contains("铺地砖") || i.Name.Contains("抹地")
                    || i.Name.Contains("客厅及餐厅地砖") || i.Name.Contains("客厅及餐厅正铺")
                    // 2) 通配 MM: 同时 必 含 砖/瓷/玻/石/木 任 一 材质 keyword - 这 才 是 真 地砖行
                    || (DirtSpecRegex.IsMatch(i.Name)
                        && (i.Name.Contains("砖") || i.Name.Contains("瓷") || i.Name.Contains("玻")
                            || i.Name.Contains("石") || i.Name.Contains("木")))
                ));
            if (dirtRow != null)
                Debug($"    [v14 客None mudiban] dirtRow 命中 行{dirtRow.Row} [{dirtRow.Name}] (材料 + MM 路径, LastOrDefault 主规格优先)");
            var handledRows = new HashSet<TemplateItem>();

            if (existingFloorLevelingRow != null)
            {
                // Case A/C: 现有 找平 已 在 group → 直接 fill floorArea, 不 改名
                ws.Cell(existingFloorLevelingRow.Row, 3).Value = floorArea;
                writeMarker(existingFloorLevelingRow.Row, "【v14 mudiban 客None: 现有地面找平已应用, 数量=㎡】");
                handledRows.Add(existingFloorLevelingRow);
                filled++;
                Debug($"    [v14 mudiban 客None] 现有找平行{existingFloorLevelingRow.Row} [{existingFloorLevelingRow.Name}] C3={floorArea:F2}");

                // 同时 group 中 「正铺地砖」 行 0-out — 同 group 双计 防护。
                if (dirtRow != null)
                {
                    ws.Cell(dirtRow.Row, 3).Value = 0m;
                    writeMarker(dirtRow.Row, "【v14 mudiban 客None: 同 group 已有找平, 此正铺地砖禁用】");
                    handledRows.Add(dirtRow);
                    filled++;
                    Debug($"    [v14 mudiban 客None] 同 group dirtRow{dirtRow.Row} [{dirtRow.Name}] 0-out 防双计");
                }
            }
            else if (dirtRow != null)
            {
                // Case B: group 无 找平行 → rename 「正铺地砖」 → 「地面找平」 + fill
                ws.Cell(dirtRow.Row, 2).Value = "地面找平";
                ws.Cell(dirtRow.Row, 3).Value = floorArea;
                writeMarker(dirtRow.Row, "【v14 mudiban 客None: 原铺地砖 → 地面找平, 数量=㎡】");
                handledRows.Add(dirtRow);
                filled++;
                Debug($"    [v14 mudiban 客None] 改名行{dirtRow.Row} [原 {dirtRow.Name} → 地面找平] C3={floorArea:F2}");
            }
            else
            {
                // 🔧 v14.2 fix(reviewer 3): no-cost zero-fill fallback — both 找平 + dirtRow 都 null 时,
                //   zero-fill ANY IsFloorItem OR DirtSpec-matched row 名 含 「MM/砖/瓷/玻/石/木」 keyword.
                //   「客 None = 不贴砖」 意图 保留 — 不会 留下 原 实木石砖线 full cost。
                int zeroFilled = 0;
                foreach (var candidate in group.Items)
                {
                    if (string.IsNullOrEmpty(candidate.Name)) continue;
                    if (candidate.Name.Contains("找平") || candidate.Name.Contains("保护")) continue;
                    // 跳过 墙面类 — 保护 IsWallItem 处理 路径 不 被 误清零
                    if (candidate.Name.Contains("墙面") || candidate.Name.Contains("顶面") || candidate.Name.Contains("乳胶漆")
                        || candidate.Name.Contains("腻子") || candidate.Name.Contains("基层") || candidate.Name.Contains("勾缝")) continue;
                    if (!IsFloorItem(candidate.Name, config) && !DirtSpecRegex.IsMatch(candidate.Name)) continue;
                    if (!candidate.Name.Contains("MM") && !candidate.Name.Contains("砖") && !candidate.Name.Contains("瓷")
                        && !candidate.Name.Contains("玻") && !candidate.Name.Contains("石") && !candidate.Name.Contains("木")) continue;
                    ws.Cell(candidate.Row, 3).Value = 0m;
                    writeMarker(candidate.Row, "【v14 mudiban 客None zero-fill fallback: 未侦地砖行, 0-out 保 「不贴砖」语义】");
                    handledRows.Add(candidate);
                    zeroFilled++;
                    Debug($"    [v14 mudiban 客None zero-fill] 行{candidate.Row} [{candidate.Name}] C3=0");
                }
                if (zeroFilled == 0)
                    Debug($"    [v14 mudiban 客None WARN] group [{group.Name}] 无 找平 + 无 dirtRow + 无 floor-candidate — floor 处理 0 filled");
                filled += zeroFilled;
            }

            // 「地面保护」→ C3=0 (mudiban 模板 几乎 都 有 这 row)
            TemplateItem protectRow = group.Items.FirstOrDefault(i =>
                !string.IsNullOrEmpty(i.Name) && i.Name.Contains("地面保护"));
            if (protectRow != null)
            {
                ws.Cell(protectRow.Row, 3).Value = 0m;
                writeMarker(protectRow.Row, "【v14 mudiban 客None: 原地面保护=㎡ → 0, 不贴砖后不需保护】");
                handledRows.Add(protectRow);
                filled++;
                Debug($"    [v14 mudiban 客None] 保护行{protectRow.Row} [地面保护] C3=0 (免保护)");
            }

            // 其他 墙面 row 正常走 IsWallItem 原 fill wallArea path
            foreach (var item in group.Items)
            {
                if (handledRows.Contains(item)) continue;
                if (IsWallItem(item.Name, config))
                {
                    ws.Cell(item.Row, 3).Value = Math.Round(room.WallArea, 1, MidpointRounding.AwayFromZero);
                    filled++;
                    Debug($"    [v14 mudiban 客None] 墙行{item.Row} [{item.Name}] C3={room.WallArea:F2}");
                }
            }
            return filled;
        }

        // ====================================================================
        // 合计公式: 全表 SUMIF 通配匹配 "小计"
        // ====================================================================

        private void FixTotalFormula(IXLWorksheet ws)
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
                else if (c2.Contains("合计")) totalRows.Add(row);
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
        }

        // ====================================================================
        // 填房间数据 (数量 + 卫生间/外花园特殊)
        // ====================================================================

        private int FillRoomData(IXLWorksheet ws, TemplateGroup group, Room room, QuoteConfig config)
        {
            int filled = 0;
            int outdoorHit = 0;
            int indoorHit = 0;
            bool hasOutdoor = room.IsWaterproofedRoll.HasValue
                && (room.OutdoorGardenFormulas != null && room.OutdoorGardenFormulas.Count > 0);
            if (room.IsWaterproofedRoll.HasValue
                && (room.OutdoorGardenFormulas == null || room.OutdoorGardenFormulas.Count == 0))
            {
                Debug($"    [! 警告] 房间 [{room.Name}] IsWaterproofedRoll={(room.IsWaterproofedRoll.Value ? "卷材" : "非卷材")}，OutdoorGardenFormulas 为空，按地面/墙面默认填充");
            }
            bool hasIndoor = room.ItemFormulas != null && room.ItemFormulas.Count > 0;
            // 🔧 双模规格化: 同一套引擎既处理 zhubaojiao 多 variant 行 (blank-on-mismatch),
            //    也处理 dizhuan 单行通用模板 (override col5/col7 价格).
            //   floorItemCount 决定是「单规格模板」还是「多规格模板」:
            //     ≤1 → 单规格 (override C5/C7/C9; 不 blank C3)
            //     ≥2 → 多规格 (保留原 blank-on-mismatch 行为)
            // 🔧 v4 修正: 仅统计真正能被 某个 TileSpec 评中 的 row (itemSpec != null),
            //   避免 强化地板 / 成品保护 这类 走 FloorKeywords (「地板」「成品保护」)但
            //   本身不属 tile谱系 的行被 误计数  → 出现 dizhuan 模板 只有 1 实际 tile
            //   行却 被算成 多规格 的事故
            int floorItemCount = group.Items.Count(i =>
                IsFloorItem(i.Name, config) && IdentifyTileSpecMatch(i.Name, config, room.RoomType) != null);
            // 🔧 v6 多楼层选择: 3-key 回退 — k1 主路径(per-floor), k2 全局兑底, k3 老单楼层面板.
            //   "<NONE>" marker → selectedSpecCached=null (mudiban 风格跳过 PHASE A).
            string selectedSpecCached = null;
            // 🔧 v14 fix: hoist `resolved` to outer scope — v14 mudiban-LR none detection below references it.
            string resolved = null;
            if (config?.SelectedTileSpecs != null)
            {
                string floorKey = (room.FloorLevel ?? "").Trim();
                string roomKey = (room.RoomType ?? "").Trim();
                string k1 = floorKey + "|" + roomKey;
                string k2 = "|" + roomKey;
                string k3 = roomKey;
                if (config.SelectedTileSpecs.TryGetValue(k1, out var v1) && !string.IsNullOrEmpty(v1))
                    resolved = v1;
                else if (config.SelectedTileSpecs.TryGetValue(k2, out var v2) && !string.IsNullOrEmpty(v2))
                    resolved = v2;
                else if (config.SelectedTileSpecs.TryGetValue(k3, out var v3) && !string.IsNullOrEmpty(v3))
                    resolved = v3;
                // 🔧 "<NONE>" → selectedSpecCached=null (mudiban 风格 — 泥板/木地板本奇价格不动).
                if (resolved != null && resolved != "<NONE>")
                    selectedSpecCached = resolved;
            }
            // 🔧 v14 fix: mudiban 模板 + 客餐厅 + "<NONE>" 选 → 「正铺地砖」 转 「地面找平」 + 「地面保护」 C3=0.
            //   sourceTpl 探测 复式路径 (SelectedFloorTemplates[room.FloorLevel]) + 单层 fallback (config.SelectedTemplate).
            //   单层 fallback 是 能让 user 提到的「单楼层 UI 选了 mudiban + 客 None」 走上 special 路径 的关键。
            string sourceTplV14 = "";
            if (config?.SelectedFloorTemplates != null
                && !string.IsNullOrWhiteSpace(room?.FloorLevel)
                && config.SelectedFloorTemplates.TryGetValue(room.FloorLevel ?? "", out var _t14))
                sourceTplV14 = _t14;
            else if (!string.IsNullOrEmpty(config?.TemplateSettings?.ActiveTemplate))
                sourceTplV14 = config.TemplateSettings.ActiveTemplate;
            bool isLivingRoomNoneMudiban = (resolved == "<NONE>")
                && _v14TriggerRoomTypes.Contains((room?.RoomType ?? "").Trim())
                && string.Equals(sourceTplV14, "mudiban", StringComparison.OrdinalIgnoreCase);
            if (isLivingRoomNoneMudiban)
            {
                int n14 = ApplyLivingRoomNoneMudiban(ws, group, room, config);
                Debug($"    [v14 客None mudiban] 房 [{room.Name}] (RoomType={room.RoomType}) 独有 layout 写完 filled={n14}");
                return n14;
            }
            foreach (var item in group.Items)
            {
                // 🔧 PHASE A: 单规格模板 price override (C5/C7 + C9 备注 marker)
                //   走 在最前 以保证即使 row 同时命中 ItemFormulas / OutdoorGardenFormulas (Phase B/C)
                //   也把材质/人工单价先覆写为 selectedSpec — ItemFormulas 只动 C3 与之处突。
                //   double? + HasValue → 未配价格不动模板, 0 表达 「材质免费」 语义保留.
                //   C9 marker 重复跑多规格时会被正则清除历史后从唯一个 marker 重写.
                //   🔧 v4 另加防御: isExactSpecRow 免 强化地板 / 成品保护 被颛 误跳 进 override
                //     (他们是 IsFloorItem=true 但 itemSpec=null, 不是 谱）；fall back 则是
                //     floorItemCount==0 即全组 谱 都没 能评中—保持 原始 「通用单行模板」 行为.
                bool isExactSpecRow = IdentifyTileSpecMatch(item.Name, config, room.RoomType) != null;
                if (floorItemCount <= 1 && IsFloorItem(item.Name, config) && selectedSpecCached != null
                    && config?.TemplateSettings?.TileSpecOptions != null
                    && config.TemplateSettings.TileSpecOptions.TryGetValue(room.RoomType ?? "", out var specList)
                    && specList != null
                    && (isExactSpecRow || (floorItemCount == 0 && IsTileishName(item.Name))))
                {
                    var opt = specList.FirstOrDefault(s => string.Equals(s.Value, selectedSpecCached, StringComparison.Ordinal));
                    if (opt != null)
                    {
                        if (opt.MaterialPrice.HasValue)
                        {
                            ws.Cell(item.Row, 5).Value = opt.MaterialPrice.Value;
                            Debug($"    [单规格 override C5] 行{item.Row}: [{item.Name}] material={opt.MaterialPrice.Value:F2} (规格={selectedSpecCached})");
                        }
                        if (opt.LaborPrice.HasValue)
                        {
                            ws.Cell(item.Row, 7).Value = opt.LaborPrice.Value;
                            Debug($"    [单规格 override C7] 行{item.Row}: [{item.Name}] labor={opt.LaborPrice.Value:F2} (规格={selectedSpecCached})");
                        }
                        if (opt.MaterialPrice.HasValue || opt.LaborPrice.HasValue)
                        {
                            // 🔧 C2 重写：选中规格后, 让 row 名 明确呈现所选规格 (dizhuan 类单行模板中 row 原本只写「铺地砖」无规格信息)
                            //   若模板原有的 row 名 已与 BuildSpecItemName 不同, 覆写. zhubaojiao 多规格模板行名 本身已对齐
                            //   spec.Lab 里的 size 部分 → 不会触发覆写 (避免命名被改).
                            string c2New = BuildSpecItemName(opt);
                            if (!string.IsNullOrEmpty(c2New)
                                && !string.Equals(item.Name ?? string.Empty, c2New, StringComparison.Ordinal))
                            {
                                ws.Cell(item.Row, 2).Value = c2New;
                                Debug($"    [单规格 rewrite C2] 行{item.Row}: [{item.Name}] -> [{c2New}] (规格={selectedSpecCached})");
                            }
                            // 清掉历史 marker（防同一行跨多次跑多规格造成 stack），只留最新一个
                            string rawDesc = CellString(ws.Cell(item.Row, 9)) ?? string.Empty;
                            string desc = Regex.Replace(rawDesc, @"\n?\u3010\u5df2\u5e94\u7528\u89c4\u683c:.*?\u3011", string.Empty, RegexOptions.Singleline).TrimEnd('\n', '\r');
                            string marker = "\u3010\u5df2\u5e94\u7528\u89c4\u683c: " + (opt.Label ?? selectedSpecCached) + "\u3011";
                            ws.Cell(item.Row, 9).Value = (desc.Length == 0 ? marker : desc + "\n" + marker);
                        }
                    }
                    else
                    {
                        Debug($"    [单规格 跳过 override] 行{item.Row}: selectedSpec={selectedSpecCached} 但 config 里没找到, 模板价格保留");
                    }
                }

                if (hasOutdoor)
                {
                    string key = FindFormulaKey(room.OutdoorGardenFormulas, item.Name);
                    if (key != null)
                    {
                        double qty = room.OutdoorGardenFormulas[key];
                        ws.Cell(item.Row, 3).Value = Math.Round(qty, 1, MidpointRounding.AwayFromZero);
                        filled++;
                        outdoorHit++;
                        Debug($"    [外花园公式] 行{item.Row} [{item.Name}]: 数量={qty:F2} (key={key})");
                        continue;
                    }
                }
                if (hasIndoor)
                {
                    string key2 = FindFormulaKey(room.ItemFormulas, item.Name);
                    if (key2 != null)
                    {
                        double qty2 = room.ItemFormulas[key2];
                        ws.Cell(item.Row, 3).Value = Math.Round(qty2, 1, MidpointRounding.AwayFromZero);
                        filled++;
                        indoorHit++;
                        Debug($"    [室内公式] 行{item.Row} [{item.Name}]: 数量={qty2:F2} (key={key2})");
                        continue;
                    }
                }
                if (IsFloorItem(item.Name, config))
                {
                    // 🔧 双模规格化: 同一套引擎既能处理 zhubaojiao 多 variant 行 (blank-on-mismatch),
                    //    也能处理 dizhuan 单行通用模板 (override col5/col7 价格, 在 PHASE A 已跑).
                    //    判定仅依赖 floorItemCount (上面已算好):
                    //      ≤1 → 单规格 (PHASE A override 已跑, 这里只填 C3)
                    //      ≥2 → 多规格 (保留原 blank-on-mismatch 行为)
                    //    selectedSpec / itemSpec 字段定义沿用 TileSpecOption.Value.
                    string itemSpec = IdentifyTileSpecMatch(item.Name, config, room.RoomType);

                    if (floorItemCount <= 1)
                    {
                        // ── 单规格模板 (dizhuan 等只有 1 行「铺地砖」每分组的模板) ──
                        // C5/C7/C9 已在 PHASE A 处理 (顶位跑保证不被 ItemFormulas 短路).
                        // 这里只为 C3 填 FloorArea. 若 ItemFormulas 已填 (Phase C 先 续)
                        // 也不会被 覆盖,因为 Phase C 是在该块之前跑且全程已 Phase A 生效.
                        ws.Cell(item.Row, 3).Value = Math.Round(room.FloorArea, 1, MidpointRounding.AwayFromZero);
                        filled++;
                        Debug($"    [填地面 单规格] 行{item.Row}: [{item.Name}] 数量={room.FloorArea:F2} (规格={selectedSpecCached ?? "<未选>"})");
                        continue;
                    }

                    // ── 多规格模板 (zhubaojiao 等多个 variant 行) ── 维持原 blank-on-mismatch 行为
                    if (selectedSpecCached != null && itemSpec != null && !string.Equals(itemSpec, selectedSpecCached, StringComparison.Ordinal))
                    {
                        ws.Cell(item.Row, 3).Value = 0m;
                        filled++;
                        Debug($"    [规格失配 blank] 行{item.Row}: [{item.Name}] itemSpec={itemSpec} != selectedSpec={selectedSpecCached}, 已清零");
                        continue;
                    }

                    ws.Cell(item.Row, 3).Value = Math.Round(room.FloorArea, 1, MidpointRounding.AwayFromZero);
                    filled++;
                    Debug($"    填地面 行{item.Row}: [{item.Name}] 数量={room.FloorArea:F2}");
                }
                else if (IsWallItem(item.Name, config))
                {
                    ws.Cell(item.Row, 3).Value = Math.Round(room.WallArea, 1, MidpointRounding.AwayFromZero);
                    filled++;
                    Debug($"    填墙面 行{item.Row}: [{item.Name}] 数量={room.WallArea:F2}");
                }
                else if (IsCurtainBoxItem(item.Name))
                {
                    var qty = Math.Round(room.CurtainBoxLength, 1, MidpointRounding.AwayFromZero);
                    ws.Cell(item.Row, 3).Value = qty;
                    filled++;
                    if (qty > 0)
                        Debug($"    [窗帘盒] 行{item.Row}: [{item.Name}] 数量={qty:F2} m (房 [{room.Name}])");
                    else
                        Debug($"    [窗帘盒] 行{item.Row}: [{item.Name}] 数量=0 m (房 [{room.Name}] 未侦到窗户 — DWG 里无 Layer=窗户 且 Color≠251 线段, 或 浮空)");
                }
            }
            if (hasOutdoor && outdoorHit == 0)
                Debug($"    [! 警告] 房间 [{room.Name}] IsWaterproofedRoll 参数已设但模板未匹配外花园项, 请检查模板是否定全");
            if (hasIndoor && indoorHit == 0)
                Debug($"    [! 警告] 房间 [{room.Name}] 室内公式已设但模板未匹配任何 卫生间/厨房 子项, 请检查模板");
            return filled;
        }

        private static string FindFormulaKey(Dictionary<string, double> dict, string itemName)
        {
            if (dict == null || string.IsNullOrEmpty(itemName)) return null;
            if (dict.TryGetValue(itemName, out _)) return itemName;
            var match = dict.Keys
                .Where(k => !string.IsNullOrEmpty(k) && (itemName.Contains(k) || k.Contains(itemName)))
                .OrderByDescending(k => k.Length)
                .FirstOrDefault();
            return match;
        }

        private List<string> GetFloorKeywords(QuoteConfig config)
        {
            var kws = config?.TemplateSettings?.FloorItemKeywords;
            if (kws != null && kws.Count > 0) return kws;
            return new List<string> { "铺地砖", "地砖", "地板", "正铺", "地面保护" };
        }

        private List<string> GetWallKeywords(QuoteConfig config)
        {
            var kws = config?.TemplateSettings?.WallItemKeywords;
            if (kws != null && kws.Count > 0) return kws;
            return new List<string> { "墙顶面基层加固", "墙面基层处理", "鸟巢腻子", "芬琳芬华", "五合一", "内墙乳胶漆" };
        }

        private bool IsFloorItem(string name, QuoteConfig config)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return GetFloorKeywords(config).Any(k => name.Contains(k));
        }

        /// <summary>
        /// 「原 row 名是否看起来像是 铺地砖的候选」 — 只需包含 TileishKeywords 中任一即可.
        /// 是 v5 PHASE A gate 中的 「floorItemCount==0 fallback」 唯一准入依据。mudiban 的 「地面找平」「自流平」「成品保护」这类不含。
        /// </summary>
        private static bool IsTileishName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var k in TileishKeywords)
                if (name.Contains(k)) return true;
            return false;
        }


        /// <summary>
        /// 根据 spec.Match 构造规格对应的 C2 项目名称 — 在单规格模板 (dizhuan) PHASE A 用.
        ///   - Match 含「正铺」 → prefix "正铺地砖"; 含「正贴」 → "正贴地砖".
        ///   - 含「菱铺」/「菱贴」 → 仅返回 prefix (菱补 销大小子串 — diamond 实际可不报尺寸).
        ///   - 其他 → "铺地砖".
        ///   - size 从 Match 里第一个数字型子串提取; 已带 "MM" 不重复加. 菱补 return prefix only.
        ///   - 例: ["正铺","750*1500"]  → "正铺地砖（750*1500MM）"
        ///   - 例: ["菱铺"]           → "菱铺地砖"
        /// </summary>
        private static string BuildSpecItemName(TileSpecOption opt)
        {
            if (opt?.Match == null || opt.Match.Count == 0) return null;
            string prefix = "铺地砖";
            string size = null;
            foreach (var m in opt.Match)
            {
                if (string.IsNullOrEmpty(m)) continue;
                if (m.Contains("\u83f1\u94fa")) prefix = "\u83f1\u94fa\u5730\u7816";        // 菱铺
                else if (m.Contains("\u83f1\u8d34")) prefix = "\u83f1\u8d34\u5730\u7816";    // 菱贴
                else if (m.Contains("\u6b63\u94fa")) prefix = "\u6b63\u94fa\u5730\u7816";    // 正铺
                else if (m.Contains("\u6b63\u8d34")) prefix = "\u6b63\u8d34\u5730\u7816";    // 正贴
                else if (m.Contains("\u94fa\u5730\u7816") || m == "\u5730\u7816" || m == "\u5730\u677f")
                    prefix = "\u94fa\u5730\u7816";                                            // 铺地砖 / 地砖 / 地板
                else size = m;                                                                // 首个数字型子串视为 size
            }
            // 菱补 spec Match 只有 「菱铺/菱贴」 关键子, 不带 size. fallback 从 opt.Label 正则抓取 "数字尺寸MM".
            // 避免选中菱铺后 row 丢失 size 提示 (如 300-800MM).
            if (prefix.StartsWith("\u83f1") && string.IsNullOrEmpty(size) && !string.IsNullOrEmpty(opt.Label))
            {
                var lblMatch = Regex.Match(opt.Label, "([0-9]+(?:[\\-\\*][0-9]+)?MM)");
                if (lblMatch.Success) size = lblMatch.Groups[1].Value;
            }
            if (string.IsNullOrEmpty(size)) return prefix;
            string sizeWithUnit = size.EndsWith("MM") ? size : size + "MM";
            return prefix + "\uff08" + sizeWithUnit + "\uff09";
        }

        private bool IsWallItem(string name, QuoteConfig config)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return GetWallKeywords(config).Any(k => name.Contains(k));
        }

        /// <summary>
        /// 🔧 v16: 模板行 名 含「窗帘盒」匹配.
        ///   FloorItemKeywords / WallItemKeywords 都不会勾它 — 走这条独立路径 直接 读 room.CurtainBoxLength (=米).
        ///   不动材质/人工 价格 (用户另设) — 只填 C3 数量列.
        /// </summary>
        private static bool IsCurtainBoxItem(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.Contains("窗帘盒");
        }

        /// <summary>
        /// 找出 item.Name 命中的 spec.Value. 用 ALL-命中: spec.Match 列表的所有子串都必须出现.
        /// 不命中 任一 spec 返 null (Faller Safe — 让 IsFloorItem 老逻辑继续走).
        /// </summary>
        private string IdentifyTileSpecMatch(string itemName, QuoteConfig config, string roomType)
        {
            if (string.IsNullOrEmpty(itemName) || config == null) return null;
            var dict = config.TemplateSettings?.TileSpecOptions;
            if (dict == null) return null;
            if (!dict.TryGetValue(roomType ?? "", out var specs) || specs == null) return null;

            // 🔧 最先命中用 「总 key 长度 desc + keyword 数 desc」, 避免以后 config 顺序变化 ·
            // blanket spec (如 ["正铺"]) 在前但不该胜出. 同一 item 上多个 spec 否则 all-hit 并列时, 最具体的先赢.
            var ordered = specs
                .Where(s => s?.Match != null && s.Match.Count > 0)
                .OrderByDescending(s => s.Match.Sum(m => (m?.Length ?? 0)))
                .ThenByDescending(s => s.Match.Count);

            foreach (var spec in ordered)
            {
                bool allHit = true;
                foreach (var m in spec.Match)
                {
                    if (string.IsNullOrEmpty(m)) continue;
                    if (!itemName.Contains(m)) { allHit = false; break; }
                }
                if (allHit) return spec.Value ?? "";
            }
            return null;
        }

        // ====================================================================
        // 楼层排序 + 模板 header 识别正则 + 通用
        // ====================================================================

        private static readonly Regex GroupHeaderRegex = new Regex(
            @"^[一二三四五六七八九十]+、?$",
            RegexOptions.Compiled);

        private bool IsGroupHeader(string col1)
        {
            if (string.IsNullOrWhiteSpace(col1)) return false;
            return GroupHeaderRegex.IsMatch(col1.Trim());
        }

        private static int FloorOrderKey(string floor)
        {
            if (string.IsNullOrWhiteSpace(floor)) return int.MaxValue;
            if (floor == "未指定") return int.MaxValue - 1;
            if (floor.Contains("一")) return 1000;
            if (floor.Contains("二")) return 2000;
            if (floor.Contains("三")) return 3000;
            if (floor.Contains("四")) return 4000;
            if (floor.Contains("五")) return 5000;
            if (floor.Contains("六")) return 6000;
            if (floor.Contains("七")) return 7000;
            if (floor.Contains("八")) return 8000;
            if (floor.Contains("九")) return 9000;
            if (floor.Contains("十")) return 10000;
            return 20000;
        }
    }

    internal class TemplateGroup
    {
        public int HeaderRow { get; set; }
        public string Name { get; set; }
        public string RoomType { get; set; }
        public string FloorLevel { get; set; } = "";
        public int SubtotalRow { get; set; }
        public List<int> ExtraSubtotalRows { get; set; } = new List<int>();
        public bool ChromeOnly { get; set; } = false;
        public List<TemplateItem> Items { get; set; } = new List<TemplateItem>();
        /// <summary>🔧 v7 per-floor template: 来自哪个 xlsx (e.g. "dizhuan", "mudiban"). "" = v6 默认/未指定.
        ///   用于 tplDict 三维 key (SourceTemplate, FloorLevel, RoomType) + ResolveTemplates 优先级查找.</summary>
        public string SourceTemplate { get; set; } = "";
    }

    internal class TemplateItem
    {
        public int Row { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }
    }
}
