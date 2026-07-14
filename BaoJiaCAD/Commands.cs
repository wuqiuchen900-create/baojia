using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(BaoJiaCAD.Commands))]
[assembly: ExtensionApplication(typeof(BaoJiaCAD.PluginInitializer))]

namespace BaoJiaCAD
{
    /// <summary>
    /// AutoCAD 命令入口
    /// </summary>
    public class Commands
    {
        // 🔧 v7 修复: 5 → 9 层同步 QuotePanel._floorAliases. 否则复式层 = 6/7/8/9 时 FloorAliases[floorIdx] 引发 IndexOutOfRangeException.
        private static readonly string[] FloorAliases = { "一楼", "二楼", "三楼", "四楼", "五楼", "六楼", "七楼", "八楼", "九楼" };

        /// <summary>
        /// 一键报价命令（面板输入参数 + 框选房间识别 + 导出 Excel）
        /// </summary>
        [CommandMethod("BJ")]
        public void BaoJia()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            var editor = doc.Editor;

            try
            {
                // 1. 加载配置
                QuoteConfig config = QuoteConfig.Load(GetConfigPath());

                // 2. 弹出参数面板
                string dwgName = Path.GetFileNameWithoutExtension(doc.Name);
                if (string.IsNullOrEmpty(dwgName)) dwgName = "未命名";

                var panel = new QuotePanel(config, dwgName);
                if (panel.ShowDialog() != DialogResult.OK)
                {
                    editor.WriteMessage("\n用户取消，命令退出。");
                    return;
                }

                double wallHeight = panel.WallHeight;
                bool isMultiFloor = panel.IsMultiFloor;
                int floorCount = isMultiFloor ? panel.FloorCount : 1;
                string projectName = panel.ProjectName;
                string overrideTemplate = panel.SelectedTemplate;

                // 面板防水参数回写到 config（覆盖 config.json 默认值，仅本次生效）
                panel.ApplyWaterproofToConfig(config);

                // 🔧 v14.2 修复: 面板 dropdown 选 的 模板 需 写回 config.TemplateSettings.ActiveTemplate.
                //   复式 路径走 config.SelectedFloorTemplates 不会读 ActiveTemplate, 但 单层 路径 是用 ActiveTemplate 作为 fallback (ExcelExporter v14 sourceTplV14).
                //   不写回 → ActiveTemplate 保留 stale (config.json 全局), 单层 + 客 None + mudiban special 检测 永远 fail.
                //   overrideTemplate 总是 非空 (panel.SelectedTemplate 已 ?? "dizhuan" fallback) — 不用 IsNullOrEmpty 重检。
                if (config?.TemplateSettings != null)
                    config.TemplateSettings.ActiveTemplate = overrideTemplate;

                // 面板瓷砖规格回写到 config.SelectedTileSpecs (运行时, 不持久化)
                //   - ExcelExporter.FillRoomData 读这个 dict + TileSpecOptions.Match 做 spec-blanking
                config.SelectedTileSpecs = panel.GetTileSpecSelections();
                if (config.SelectedTileSpecs.Count > 0)
                {
                    editor.WriteMessage($"\n[规格] 用户面板选 {config.SelectedTileSpecs.Count} 个类别:");
                    foreach (var kv in config.SelectedTileSpecs)
                        editor.WriteMessage($"\n[规格]   {kv.Key} -> {kv.Value}");
                }

                // 🔧 v7 面板每层 模板 回写到 config.SelectedFloorTemplates
                //   - 单层返空 dict, ExcelExporter 走原 _cmbTemplate 单路径
                //   - 复式返 floor→templateName, ExcelExporter 按 (floor, roomType) 加载对应 xlsx
                config.SelectedFloorTemplates = panel.GetFloorTemplateSelections();
                if (config.SelectedFloorTemplates.Count > 0)
                {
                    editor.WriteMessage($"\n[模板] v7 复式 每层 模板:");
                    foreach (var kv in config.SelectedFloorTemplates)
                        editor.WriteMessage($"\n[模板]   {kv.Key} -> {kv.Value}");
                }

                // 5. 循环识别每层
                var allRooms = new List<Room>();
                var confirmedBoundaries = new List<DetectedBoundary>();
                // 🔧 未识别文字 全楼层累计器 (供未识别 提醒: 命令列汇总 + YesNo 弹窗 用)
                var allSkipped = new List<SkippedTextInfo>();

                bool exporting = false;
                for (int floorIdx = 0; floorIdx < floorCount && !exporting; floorIdx++)
                {
                    string currentFloorAlias = isMultiFloor ? FloorAliases[floorIdx] : "";

                    bool redoThisFloor = true;
                    while (redoThisFloor)
                    {
                        redoThisFloor = false;

                        if (isMultiFloor)
                            editor.WriteMessage($"\n[第 {floorIdx + 1}/{floorCount} 层 - {currentFloorAlias}] 请框选本层所有房间（封闭墙线 + 房间名称文字）：");
                        else
                            editor.WriteMessage("\n请框选所有房间区域（封闭墙线 + 房间名称文字）：");

                        var sel = editor.GetSelection();
                        if (sel.Status != PromptStatus.OK)
                        {
                            editor.WriteMessage("\n未选择对象，命令取消。");
                            return;
                        }

                        var detector = new RoomDetector(config, wallHeight, confirmedBoundaries);
                        var result = detector.DetectRooms(editor, sel.Value, currentFloorAlias, isMultiFloor);

                        if (result.Rooms.Count == 0)
                        {
                            editor.WriteMessage("\n[空选] 本次没识别到任何房间，可能是墙线未闭合。请重新框选本层。");
                            PrintDiagnostics(editor, result);
                            redoThisFloor = true;
                            continue;
                        }

                        PrintDiagnostics(editor, result);

                        string choice = PromptAfterDetection(editor, isMultiFloor, floorIdx, floorCount);
                        if (choice == "CANCEL")
                        {
                            editor.WriteMessage("\n用户取消，命令退出。");
                            return;
                        }
                        switch (choice)
                        {
                            case "NEXT":
                                CommitFloor(confirmedBoundaries, allRooms, result, allSkipped);
                                break;
                            case "REDO":
                                redoThisFloor = true;
                                break;
                            case "EXPORT":
                                CommitFloor(confirmedBoundaries, allRooms, result, allSkipped);
                                exporting = true;
                                break;
                        }
                    }
                }

                if (allRooms.Count == 0)
                {
                    editor.WriteMessage("\n未识别到任何房间，命令结束。");
                    return;
                }

                // 🔧 未识别房间 提醒: 命令列汇总 + YesNo 弹窗 (Yes 继续导出 / No 取消 BJ) — 才能 让门窗洞口/复杂结构
                //     导致识别遗漏的文字 强制在用户眼前出现一次 才能知晓.
                if (!ShowSkippedReminderDialog(editor, allSkipped, allRooms.Count))
                {
                    editor.WriteMessage("\n[未识别提醒] 用户选择取消 BJ, 命令退出.");
                    return;
                }

                string outputPath = PromptForSavePath(editor, projectName);
                if (string.IsNullOrEmpty(outputPath))
                {
                    editor.WriteMessage("\n未选择输出路径，命令取消。");
                    return;
                }

                string templatePath = GetTemplatePath(config, isMultiFloor, overrideTemplate, editor);

                // 🔧 v7: 复式 + 面板每层选不同模板时, 按 floor 走各层 xlsx 路径. 主模板路径 (输出 wb 背景) = 1F 的路径.
                //   - isMultiFloor==false 或 SelectedFloorTemplates 空 → 走 v6 单模板路径
                //   - 复式 + 每层都选同一模板 (变同名字典, 重复) → 也走 v6 路径 (ParseTemplate 拿到全部)
                //   - 复式 + 多层 不同模板 → 走 v7 多模板路径
                string primaryTemplateName = overrideTemplate;
                Dictionary<string, string> floorTemplatePaths = new Dictionary<string, string>();
                if (isMultiFloor && config.SelectedFloorTemplates != null && config.SelectedFloorTemplates.Count > 0)
                {
                    foreach (var kv in config.SelectedFloorTemplates)
                    {
                        var perFloorPath = GetSpecificTemplatePath(config, kv.Value, editor);
                        if (!string.IsNullOrEmpty(perFloorPath))
                            floorTemplatePaths[kv.Key] = perFloorPath;
                    }
                    // 主模板 = 1F 的路径 (或第一可用层)
                    string oneFAlias = FloorAliases != null && FloorAliases.Length > 0 ? FloorAliases[0] : "一楼";
                    if (floorTemplatePaths.TryGetValue(oneFAlias, out var oneFPath))
                    {
                        templatePath = oneFPath;
                        primaryTemplateName = config.SelectedFloorTemplates.TryGetValue(oneFAlias, out var oneFTpl) ? oneFTpl : overrideTemplate;
                    }
                    else if (floorTemplatePaths.Count > 0)
                    {
                        // 1F 面板未选, 但其他层选了 — 抩一个能用的
                        KeyValuePair<string, string> firstKv = default;
                        bool got = false;
                        foreach (var kvp in floorTemplatePaths) { firstKv = kvp; got = true; break; }
                        if (got)
                        {
                            templatePath = firstKv.Value;
                            primaryTemplateName = config.SelectedFloorTemplates.TryGetValue(firstKv.Key, out var fbTpl) ? fbTpl : overrideTemplate;
                        }
                    }
                    if (floorTemplatePaths.Count > 1)
                    {
                        editor.WriteMessage($"\n[模板] v7 复式 多模板混合: {floorTemplatePaths.Count} 层各自加载路径:");
                        foreach (var kv in floorTemplatePaths)
                            editor.WriteMessage($"\n[模板]   {kv.Key} -> {Path.GetFileName(kv.Value)}");
                    }
                }

                // 走法 C: 6 大类归纳面板（在 Excel 导出前展示给用户）
                // 外花园卷材防水互斥面板：Y/N 选择后写入 Room.IsWaterproofedRoll + OutdoorGardenFormulas
                if (!CategoryPanel.AskOuterGardenWaterproof(editor, allRooms, config))
                {
                    editor.WriteMessage("\n[外花园] 用户取消，BJ 命令中止。");
                    return;
                }
                // 卫生间/厨房 默认公式 (免询问)
                CategoryPanel.AskBathroomKitchenFormulas(editor, allRooms, config);
                CategoryPanel.ShowSixCategories(editor, allRooms, wallHeight, config);

                var exporter = new ExcelExporter();
                exporter.Log = msg => editor.WriteMessage($"\n[报价] {msg}");
                exporter.Export(allRooms, projectName, templatePath, primaryTemplateName, floorTemplatePaths, outputPath, config);

                editor.WriteMessage($"\n报价单已生成：{outputPath}");
                editor.WriteMessage($"\n共识别 {allRooms.Count} 个房间（{(isMultiFloor ? floorCount : 1)} 层）。");
            }
            catch (System.Exception ex)
            {
                // 🔧 诊断模式 v4 (Round 3 总结): 完整 [ERROR-DIAG] dump 到 F2 trace + dialog 顶部携前 3 帧 stack.
                //   前 3 轮我瞎猜没点到 NRE 真正的 source line. 本轮 让用户复现一次 同时 dump 到 dialog 顶部 (用户不看 F2 也能抓住源) + F2 trace 供深度分析.
                //   重测后 请 复制 dialog 文本 或 F2 里 [ERROR-DIAG] 到下条 block 贴回来 — 就能明确 定位 NRE origin 上线.
                editor.WriteMessage("\n========== [ERROR-DIAG] ==========");
                editor.WriteMessage($"\n[ERROR-DIAG] ExceptionType: {ex.GetType().FullName}");
                editor.WriteMessage($"\n[ERROR-DIAG] Message: {ex.Message ?? "<null>"}");
                editor.WriteMessage($"\n[ERROR-DIAG] TargetSite: {ex.TargetSite?.ToString() ?? "<null>"}");
                editor.WriteMessage("\n[ERROR-DIAG] Stack:");
                editor.WriteMessage($"\n{ex.StackTrace ?? "<null>"}");
                // 内层异常链 限深 5 层 — 防 罕见自环 无限循环 (例 ObjectDisposedException chain).
                // 🔧 必须前 缀 System.Exception — Autodesk.AutoCAD.Runtime 也有 Exception 类, 不前 缀 编译 报歧义.
                System.Exception inner = ex.InnerException;
                for (int depth = 0; depth < 5 && inner != null; depth++, inner = inner.InnerException)
                {
                    editor.WriteMessage($"\n[ERROR-DIAG] -- Inner[{depth}]: {inner.GetType().FullName}: {inner.Message ?? "<null>"}");
                    editor.WriteMessage($"\n[ERROR-DIAG] -- Inner[{depth}] Stack:");
                    editor.WriteMessage($"\n{inner.StackTrace ?? "<null>"}");
                }
                editor.WriteMessage("\n====================================");
                editor.WriteMessage($"\n发生错误：{ex.Message}");
                // 根因子未起到定位作用（如 “Exception of type 'X' was thrown”）时透出底层 .InnerException.Message
                // 简化判定：外层 message 信息不足（如 "Exception of type 'X' was thrown."）一律穿透到 InnerException.Message.
                string surfaceMessage = !string.IsNullOrEmpty(ex.Message)
                    && ex.Message.Length >= 20
                    && !ex.Message.StartsWith("Exception of type")
                    ? ex.Message
                    : (ex.InnerException?.Message ?? ex.Message);
                // 🔧 v4 补充: 摘前 3 帧 stack 拼到 dialog body — 用户不看 F2 也能看到源头 (与 F2 trace 同步).
                string diagHead = "";
                if (!string.IsNullOrEmpty(ex.StackTrace))
                {
                    var lines = ex.StackTrace.Split('\n');
                    var sb = new System.Text.StringBuilder("\n\n调用栈 (前 3 帧, F2 看完整):");
                    for (int i = 0; i < lines.Length && i < 3; i++)
                        sb.Append('\n').Append(lines[i].TrimEnd());
                    diagHead = sb.ToString();
                }
                string extraHint = DiagnoseError(ex);
                System.Windows.Forms.MessageBox.Show(
                    $"报价生成失败：{surfaceMessage}\n\n请检查：\n1. 模板文件是否存放在正确路径\n2. 模板文件是否正在被 Excel 打开（先关闭 Excel 再试）\n3. config.json 配置是否正确{extraHint}{diagHead}",
                    "报价失败",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);

                // 本地 static 函数，给特定异常类型附加定位提示（不受类成员位置限制）.
                // C# 8.0 (csproj LangVersion=8.0) 支持 `static` local method 正向引用 + 捕获.
                static string DiagnoseError(System.Exception err)
                {
                    for (System.Exception e = err; e != null; e = e.InnerException)
                    {
                        string msg = e.Message ?? "";
                        if ((e.GetType().FullName ?? "").Contains("TypeInitializationException")
                            || msg.Contains("SixLabors") || msg.Contains("TableLoader"))
                        {
                            // ClosedXML 0.100+ 在 net48 上 SixLabors.Fonts 2.x 库初始化失败.
                            return "\n4. 【闭包依赖问题】 ClosedXML 0.100+ 所依赖的 SixLabors.Fonts 2.x 在 .NET Framework 4.8 上初始化失败。\n"
                                 + "   解决方案：在 BaoJiaCAD.csproj 中将 ClosedXML 退回到 0.97.0\n"
                                 + "   （该版本与 SixLabors.Fonts 1.x 配合，在 net48 上稳定运行）";
                        }
                    }
                    return "";
                }
            }
        }

        /// <summary>
        /// 把当前层成功的识别结果提交到全局列表 + 跨层去重缓存
        /// allSkipped: 本层未识别文字 (仅 NEXT/EXPORT 时合并, REDO 抛弃) — 供最终弹窗汇总用.
        /// </summary>
        private static void CommitFloor(List<DetectedBoundary> confirmed,
            List<Room> allRooms, DetectionResult result,
            List<SkippedTextInfo> allSkipped)
        {
            confirmed.AddRange(result.NewBoundaries);
            allRooms.AddRange(result.Rooms);
            if (allSkipped != null && result.SkippedTexts != null && result.SkippedTexts.Count > 0)
                allSkipped.AddRange(result.SkippedTexts);
        }

        private void PrintDiagnostics(Editor editor, DetectionResult result)
        {
            editor.WriteMessage("\n---- 本次识别 ----");
            if (result.Rooms.Count > 0)
            {
                editor.WriteMessage($"\n  [OK] 成功识别 {result.Rooms.Count} 个房间：");
                foreach (var r in result.Rooms)
                {
                    string f = string.IsNullOrEmpty(r.FloorLevel) ? "[无前缀]" : $"[{r.FloorLevel}]";
                    editor.WriteMessage($"\n    {f} {r.Name} (地面 {r.FloorArea:F2} ㎡)");
                }
            }
            if (result.SkippedTexts.Count > 0)
            {
                // 🔧 未识别 醒目块: 全列出 (不再 truncate 到 5), 含坐标便于 CAD 里 ZOOM 过去
                editor.WriteMessage($"\n  [!! ⚠ 未识别] {result.SkippedTexts.Count} 个文字未匹配, 已跳过 (不计入报价单):");
                int maxShow = 50;  // 防 CAD 命令列刷屏
                for (int i = 0; i < result.SkippedTexts.Count && i < maxShow; i++)
                {
                    var s = result.SkippedTexts[i];
                    editor.WriteMessage($"\n    - 「{s.Text}」@(X={s.X:F1}mm Y={s.Y:F1}mm) — {s.Reason}");
                }
                if (result.SkippedTexts.Count > maxShow)
                {
                    editor.WriteMessage($"\n    ... (另外 {result.SkippedTexts.Count - maxShow} 条, 略)");
                }
                editor.WriteMessage($"\n  ⚠ 上述已跳过文字未计入本次报价. 若需识别, 在 config.json RoomTypeMaps 扩充 Keywords 后重跑 BJ.");
            }
            if (result.BoundaryWarnings.Count > 0)
            {
                editor.WriteMessage($"\n  [边界] {result.BoundaryWarnings.Count} 个警告:");
                foreach (var t in result.BoundaryWarnings) editor.WriteMessage($"\n    - {t}");
            }
            if (result.Warnings.Count > 0)
            {
                editor.WriteMessage($"\n  [警告] {result.Warnings.Count} 个:");
                foreach (var t in result.Warnings) editor.WriteMessage($"\n    - {t}");
            }
            editor.WriteMessage("\n-------------------");
        }

        private string PromptAfterDetection(Editor editor, bool isMultiFloor, int floorIdx, int floorCount)
        {
            bool hasMoreFloors = isMultiFloor && (floorIdx + 1 < floorCount);
            string prompt = hasMoreFloors
                ? "\n[下一层 C] / [重选本层 R] / [开始报价 E] / [取消 Q]:"
                : "\n[重选本层 R] / [开始报价 E] / [取消 Q]:";

            while (true)
            {
                var opts = new PromptStringOptions(prompt);
                var res = editor.GetString(opts);
                if (res.Status == PromptStatus.Cancel || res.Status == PromptStatus.None)
                    return "CANCEL";
                string input = (res.StringResult ?? "").Trim();
                if (input.Length == 0) { editor.WriteMessage("\n输入为空。"); continue; }

                if (hasMoreFloors &&
                    (input.Equals("C", StringComparison.OrdinalIgnoreCase) || input == "下一层"))
                    return "NEXT";
                if (input.Equals("R", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "重选本层", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "重选", StringComparison.OrdinalIgnoreCase))
                    return "REDO";
                if (input.Equals("E", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "开始报价", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(input, "导出", StringComparison.OrdinalIgnoreCase))
                    return "EXPORT";
                if (input.Equals("Q", StringComparison.OrdinalIgnoreCase) || input == "取消")
                    return "CANCEL";

                editor.WriteMessage("\n输入无效。" + prompt);
            }
        }

        private string PromptForSavePath(Editor editor, string dwgName)
        {
            var prompt = new PromptSaveFileOptions("保存报价单")
            {
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                InitialFileName = $"{dwgName}_报价单_{DateTime.Now:yyyyMMdd}.xlsx"
            };
            var result = editor.GetFileNameForSave(prompt);
            return result.Status == PromptStatus.OK ? result.StringResult : null;
        }

        /// <summary>
        /// 🔧 v7 per-floor template path 解析 — 按 config.TemplateSettings.Templates[tplName] 拼 tplDir/fileName 返回完整 xlsx 路径.
        ///   与 GetTemplatePath 区别: 这里是「指定名字→路径」, 不存在 ActiveTemplate/面板 override 优先级.
        ///   文件不存在返 null (调用方决定 fallback, 通常是 skip 该层).
        /// </summary>
        private string GetSpecificTemplatePath(QuoteConfig config, string tplName, Editor editor)
        {
            if (config == null || string.IsNullOrEmpty(tplName)) return null;
            string dllDir = Path.GetDirectoryName(typeof(Commands).Assembly.Location);
            var ts = config.TemplateSettings;
            string tplDir = (ts != null && !string.IsNullOrEmpty(ts.TemplateFolderPath))
                ? ts.TemplateFolderPath
                : dllDir;
            if (ts?.Templates == null) return null;
            if (!ts.Templates.TryGetValue(tplName, out var fileName)) return null;
            string p = Path.Combine(tplDir, fileName);
            if (File.Exists(p)) return p;
            editor.WriteMessage($"\n[模板] v7 per-floor {tplName} 期望文件 {fileName} 不存在 ({p})");
            return null;
        }

        private string GetConfigPath()
        {
            string dllPath = typeof(Commands).Assembly.Location;
            string dllDir = Path.GetDirectoryName(dllPath);
            string configPath = Path.Combine(dllDir, "config.json");
            if (File.Exists(configPath)) return configPath;
            return Path.Combine(Directory.GetCurrentDirectory(), "config.json");
        }

        /// <summary>
        /// 获取模板文件路径。
        /// isMultiFloor=true 时优先尝试 fushi（带楼层分组的复式模板）。
        /// 否则走 config.TemplateSettings.ActiveTemplate。
        /// 🔧 修复 #3: TemplateFolderPath 为空时默认回退到 DLL 目录。
        /// 最后回退到 dllDir/template.xlsx（兼容老配置）。
        /// </summary>
        private string GetTemplatePath(QuoteConfig config, bool isMultiFloor, string overrideTemplate, Editor editor)
        {
            string dllDir = Path.GetDirectoryName(typeof(Commands).Assembly.Location);
            var ts = config?.TemplateSettings;
            // 修复 #3: TemplateFolderPath 为空时使用 DLL 目录作为默认模板文件夹
            string tplDir = (ts != null && !string.IsNullOrEmpty(ts.TemplateFolderPath))
                ? ts.TemplateFolderPath
                : dllDir;

            // 0. 检测用户是否主动改了面板下拉 (overrideTemplate != ActiveTemplate 表示改过).
            //    不区分会产生 1 个 regression: 复式 + 不动下拉 -> 旧行为默认 fushi, 新逻辑会无限选 dizhuan.
            bool userChangedTemplate =
                !string.IsNullOrEmpty(overrideTemplate)
                && overrideTemplate != (ts?.ActiveTemplate ?? "");

            // 1. 保留老行为: 用户未改下拉 + 复式 -> 默认 fushi
            if (!userChangedTemplate && isMultiFloor
                && ts != null
                && ts.Templates != null
                && ts.Templates.TryGetValue("fushi", out var fushiDefaultFile))
            {
                string fpDefault = Path.Combine(tplDir, fushiDefaultFile);
                if (File.Exists(fpDefault))
                {
                    editor.WriteMessage($"\n[模板] 复式默认 (面板未改) -> 使用 {fushiDefaultFile} (含楼层分组)");
                    return fpDefault;
                }
                // 文件缺失: 醒目 breadcrumb (旧行为保留), 不静默退化到 dizhuan
                editor.WriteMessage(
                    $"\n[模板] !! 复式期望 fushi 但路径 {tplDir} 不存在, " +
                    $"fallback 到面板选择 ({ts.ActiveTemplate ?? "未配置"})");
            }

            // 2. 优先级最高: 面板下拉选择 (overrideTemplate / ActiveTemplate) - 复式也允许 mudiban/zhubaojiao
            //    用户口述: "复式 ≠ 必然 fushi" - 选什么走什么
            string activeTemplate = !string.IsNullOrEmpty(overrideTemplate) ? overrideTemplate : ts?.ActiveTemplate;
            if (ts != null
                && ts.Templates != null
                && !string.IsNullOrEmpty(activeTemplate)
                && ts.Templates.TryGetValue(activeTemplate, out var fileName))
            {
                string p = Path.Combine(tplDir, fileName);
                if (File.Exists(p))
                {
                    editor.WriteMessage($"\n[模板] 面板选择 {activeTemplate} -> 使用 {fileName}");
                    if (isMultiFloor && activeTemplate != "fushi")
                    {
                        editor.WriteMessage(
                            $"\n[模板] 注: {activeTemplate} 原型区非复式结构. " +
                            $"ProcessRooms 按 Room.FloorLevel 自检测多楼 + ResolveTemplates 回退到无楼层前缀的原型. " +
                            $"如有异常请选 fushi.");
                    }
                    return p;
                }
                editor.WriteMessage($"\n[模板] !! 面板选 {activeTemplate} 但文件 {fileName} 不存在, 走 fushi 兜底");
            }

            // 3. 复式兜底 (面板选的文件丢失时, 用 fushi 救场 - 提供楼层适配性)
            if (isMultiFloor && ts != null
                && ts.Templates != null
                && ts.Templates.TryGetValue("fushi", out var fushiFile))
            {
                string fpFallback = Path.Combine(tplDir, fushiFile);
                if (File.Exists(fpFallback))
                {
                    editor.WriteMessage($"\n[模板] 复式兜底 -> 使用 {fushiFile}");
                    return fpFallback;
                }
                editor.WriteMessage(
                    $"\n[模板] !! 复式期望 {fushiFile} 但路径 {tplDir} 不存在, " +
                    $"fallback 到 ActiveTemplate={ts?.ActiveTemplate}");
            }

            // 4. 终极 fallback 老 dllDir/template.xlsx
            string fallback = Path.Combine(dllDir, "template.xlsx");
            if (File.Exists(fallback))
            {
                editor.WriteMessage($"\n[模板] fallback 到 {fallback}（兼容老 dllDir 模板）");
                return fallback;
            }

            throw new FileNotFoundException(
                $"模板文件未找到。请检查 config.json 的 TemplateSettings 是否有效，或将 template.xlsx 放在插件 DLL 同级目录:\n{dllDir}");
        }

        /// <summary>
        /// 🔧 未识别 提醒: 命令列最终汇总块 + YesNo MessageBox 弹窗 (Yes=继续导出, No=取消 BJ)
        /// 当 allSkipped 不空时调用, 让用户决定是接受现状 还是 中止去 config.json 补 Keywords。
        /// 弹窗默认按钮是「继续导出」 (Yes), —不 是「取消」 — 以免误操作丢失已完成的工作。
        /// </summary>
        private bool ShowSkippedReminderDialog(Editor editor, List<SkippedTextInfo> allSkipped, int roomCount)
        {
            if (allSkipped == null || allSkipped.Count == 0) return true;

            // 1. 命令列 吼一次 (供 F2 trace / IDE 输出用)
            editor.WriteMessage("\n========== ⚠ 识别遗漏 · 最终汇总 ==========");
            editor.WriteMessage($"\n  本次 BJ 共识别 {roomCount} 个房间 (已计入报价), 另 {allSkipped.Count} 个文字未识别:");
            int maxShow = 30;
            for (int i = 0; i < allSkipped.Count && i < maxShow; i++)
            {
                var s = allSkipped[i];
                editor.WriteMessage($"\n    {i + 1}. 「{s.Text}」@(X={s.X:F1}mm Y={s.Y:F1}mm) — {s.Reason}");
            }
            if (allSkipped.Count > maxShow)
            {
                editor.WriteMessage($"\n    ... 另有 {allSkipped.Count - maxShow} 项 (略)");
            }
            editor.WriteMessage("\n===========================================");

            // 2. 弹窗 YesNo (默认 Yes = 继续导出, 用户可点 No 中止 BJ 回去加 Keywords)
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"⚠ BJ 识别结果:");
            sb.AppendLine($"  - 已识别 {roomCount} 个房间 (计入报价)");
            sb.AppendLine($"  - 未识别 {allSkipped.Count} 个文字 (已跳过, 不计入报价)");
            sb.AppendLine();
            sb.AppendLine("未识别清单:");
            int showDetail = Math.Min(20, allSkipped.Count);
            for (int i = 0; i < showDetail; i++)
            {
                var s = allSkipped[i];
                sb.AppendLine($"  {i + 1}. 「{s.Text}」  (位置 X={s.X:F0}mm Y={s.Y:F0}mm)  — {s.Reason}");
            }
            if (allSkipped.Count > showDetail)
            {
                sb.AppendLine($"  ... 另有 {allSkipped.Count - showDetail} 项 (略)");
            }
            sb.AppendLine();
            sb.AppendLine("[是(默认)] 继续导出 (xlsx 不含未识别文字) ");
            sb.AppendLine("[否]       取消 BJ, 您可去 config.json RoomTypeMaps 扩充 Keywords 后重跑");

            var res = System.Windows.Forms.MessageBox.Show(
                sb.ToString(),
                "⚠ 识别遗漏 (未计入本次报价)",
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Warning,
                System.Windows.Forms.MessageBoxDefaultButton.Button1 /* Yes = 继续 */
            );
            if (res == System.Windows.Forms.DialogResult.Yes)
            {
                editor.WriteMessage("\n[未识别提醒] 用户选择继续导出.");
                return true;
            }
            editor.WriteMessage("\n[未识别提醒] 用户选择取消 BJ.");
            return false;
        }
    }

    public class PluginInitializer : IExtensionApplication
    {
        public void Initialize() { }
        public void Terminate() { }
    }
}
