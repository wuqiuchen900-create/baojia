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

        public void Export(List<Room> rooms, string projectName, string templatePath, string outputPath, QuoteConfig config)
        {
            Debug($"模板路径: {templatePath}");
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

                    var templateGroups = ParseTemplate(ws, config);
                    Debug($"模板分组数: {templateGroups.Count}");

                    ProcessRooms(ws, rooms, templateGroups, config);
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

        private List<TemplateGroup> ParseTemplate(IXLWorksheet ws, QuoteConfig config)
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
            List<TemplateGroup> templateGroups, QuoteConfig config)
        {
            if (templateGroups.Count == 0) return;
            if (rooms == null || rooms.Count == 0) return;

            var prototypes = templateGroups.ToList();
            int protoStart = prototypes.First().HeaderRow;

            // Chrome 段已不在 groups — 不再维护 chromeGroups 变量
            var roomPrototypes = prototypes.ToList();

            // protoSpan 只覆盖 room prototypes (chrome 由 Delete 顶上来, 不需要它的长度)
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

            // 楼层×类型 模板查找字典 (仅 room prototypes)
            var tplDict = roomPrototypes
                .Where(g => g.SubtotalRow > g.HeaderRow)
                .GroupBy(g => (g.FloorLevel, g.RoomType))
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
                        var tplList = ResolveTemplates(room.FloorLevel, room.RoomType, tplDict, roomPrototypes, config);
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
                            Debug($"  类型 [{room.RoomType}] 楼层 [{room.FloorLevel}]: 模板中无匹配分组，跳过房间 [{room.Name}]");
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
            int shift = totalRequiredRows;
            foreach (var g in roomPrototypes)
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
                        var tplList = ResolveTemplates(room.FloorLevel, room.RoomType, tplDict, roomPrototypes, config);
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

            // 阶段 6+7 已删除:
            //   - FixChromeFormulas 不再调用 — chrome 区视为单一静态块, 模板自带的子段公式不动
            //   - chrome "其它"段 col3/5/6/7/8 Clear 不再调用 — 模板里这些列本就是空(预留用户填)

            templateGroups.Clear();
            templateGroups.AddRange(generatedGroups);

            // 阶段 8: 重设 PrintArea
            try
            {
                int newMaxRow = ws.LastRowUsed()?.RowNumber() ?? writeCursor - 1;
                ws.PageSetup.PrintAreas.Clear();
                ws.PageSetup.PrintAreas.Add($"A1:I{newMaxRow}");
                Debug($"  PrintArea 已重设为 A1:I{newMaxRow}");
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
            string cadFloor, string cadType,
            Dictionary<(string Floor, string Type), List<TemplateGroup>> tplGroups,
            List<TemplateGroup> allGroups, QuoteConfig config)
        {
            if (!string.IsNullOrEmpty(cadFloor)
                && tplGroups.TryGetValue((cadFloor, cadType), out var exact) && exact.Count > 0)
                return exact;
            if (!string.IsNullOrEmpty(cadFloor)
                && tplGroups.TryGetValue(("", cadType), out var flat) && flat.Count > 0)
            {
                Debug($"  CAD楼层 [{cadFloor}] 回退到通用模板: {cadType}");
                return flat;
            }
            var fbMap = config?.TemplateSettings?.RoomTypeFallbackMap;
            string fallbackType = null;
            if (fbMap != null && fbMap.TryGetValue(cadType, out var fb))
                fallbackType = fb;
            if (fallbackType == null && (cadType == "阳台" || cadType == "外花园"))
                fallbackType = "客餐厅";
            if (fallbackType != null)
            {
                if (!string.IsNullOrEmpty(cadFloor)
                    && tplGroups.TryGetValue((cadFloor, fallbackType), out var fbExact) && fbExact.Count > 0)
                {
                    Debug($"  房间类型 [{cadType}] 回退到 [{fallbackType}] 模板组");
                    return fbExact;
                }
                if (!string.IsNullOrEmpty(cadFloor)
                    && tplGroups.TryGetValue(("", fallbackType), out var fbFlat) && fbFlat.Count > 0)
                {
                    Debug($"  房间类型 [{cadType}] 回退到 [{fallbackType}] 通用模板");
                    return fbFlat;
                }
            }
            if (string.IsNullOrEmpty(cadFloor))
            {
                if (tplGroups.TryGetValue(("", cadType), out var flatNoCad) && flatNoCad.Count > 0)
                {
                    Debug($"  CAD无楼层，回退到通用模板 (key=\"\"): {cadType}");
                    return flatNoCad;
                }
                var oneF = allGroups
                    .Where(g => g.RoomType == cadType && g.SubtotalRow > g.HeaderRow && g.FloorLevel == "一楼")
                    .ToList();
                if (oneF.Count > 0)
                {
                    Debug($"  CAD无楼层，回退到模板一楼: {cadType}");
                    return oneF;
                }
                var first = allGroups
                    .Where(g => g.RoomType == cadType && g.SubtotalRow > g.HeaderRow && !string.IsNullOrEmpty(g.FloorLevel))
                    .OrderBy(g => g.FloorLevel)
                    .ToList();
                if (first.Count > 0)
                {
                    Debug($"  CAD无楼层，回退到模板首个楼层 [{first[0].FloorLevel}]: {cadType}");
                    return first;
                }
            }
            return null;
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
            string selectedSpecCached = null;
            if (config?.SelectedTileSpecs != null
                && config.SelectedTileSpecs.TryGetValue(room.RoomType ?? "", out var selSpecCachedVal)
                && !string.IsNullOrEmpty(selSpecCachedVal))
                selectedSpecCached = selSpecCachedVal;
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
                        ws.Cell(item.Row, 3).Value = Math.Round(qty, 2, MidpointRounding.AwayFromZero);
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
                        ws.Cell(item.Row, 3).Value = Math.Round(qty2, 2, MidpointRounding.AwayFromZero);
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
                        ws.Cell(item.Row, 3).Value = Math.Round(room.FloorArea, 2, MidpointRounding.AwayFromZero);
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

                    ws.Cell(item.Row, 3).Value = Math.Round(room.FloorArea, 2, MidpointRounding.AwayFromZero);
                    filled++;
                    Debug($"    填地面 行{item.Row}: [{item.Name}] 数量={room.FloorArea:F2}");
                }
                else if (IsWallItem(item.Name, config))
                {
                    ws.Cell(item.Row, 3).Value = Math.Round(room.WallArea, 2, MidpointRounding.AwayFromZero);
                    filled++;
                    Debug($"    填墙面 行{item.Row}: [{item.Name}] 数量={room.WallArea:F2}");
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
    }

    internal class TemplateItem
    {
        public int Row { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }
    }
}
