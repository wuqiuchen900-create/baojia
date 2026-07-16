using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace BaoJiaCAD
{
  /// <summary>
  /// 🔧 v17.3 【窗户面积】检测器 — 依用户最新指示重定稿.
  /// 用户指示 (摘):
  ///   「CH/ZH 直接就是 窗高 (mm), 任一 出现 就 直接 当窗高 × 窗宽 = 面积.
  ///     没 CH/ZH 才 用 DH + SH = 总高 − DH − SH = 窗高.
  ///     DH 是 窗下口 高, SH 是 窗上口 距天 高 (SH-only 信息不足 → skip).」
  ///
  /// 流程:
  ///   1. 扫全DWG Line, 仅 Layer=="窗户" OR 显式 ColorIndex==251 (独立 扫, 不调 CollectWindowLines)
  ///   2. 扫全DWG 所有 DBText/MText, regex 同时认 CH/ZH/DH/SH + 分隔符 (= : # 全角＝ 全角： -) REQUIRED
  ///   3. 对每room, 每个 winLine, IsPointInPolygonWithTolerance(winLine.mid, room.BoundaryPoly, 50mm) 归属
  ///   4. 对归属成功 winLine: 找 BO+1m 范围内最近 标签 → priority:
  ///      1) CH → 直接 当窗高 (CH + ZH 同框 → CH 优先)
  ///      2) ZH → 直接 当窗高
  ///      3) 没 CH/ZH → DH + SH → 高 = roomTotalHeight − DH − SH
  ///      4) 仅 DH → 高 = roomTotalHeight − DH (默认 通顶; per-room 仅 1 次 log 警告)
  ///      5) SH-only / 无 标签 → 0 (skip + 静默 Log)
  ///   5. area = widthM × heightM / 2 (÷2 公司规则), 累加 → Room.WindowArea (㎡)
  ///
  /// 不复用 WindowBoxDetector.CollectWindowLines / IsPointNearFiniteLine / DetectCurtainBoxLengths.
  /// </summary>
    public static class WindowAreaDetector
    {
        // 🔧 v17.3 round4 + v17.4: CH/ZH/DH/SH 同时 认 多 分隔 符 + IgnoreCase
        //   - v17.4: 改 private → internal 让 RoomDetector 复用 (汇 总 时 滤 掉 窗 户 标 签, 不 计 入 房 间 名 未识别)
        //   - IgnoreCase: 允 ch / zh / dh / sh 小 写 与 大 小 写 混 合
        //   - 分隔 符 REQUIRED, [+ 后 缀] 允 许 「CH := 1500」 类 连写 情形
        //   - = : # - (ASCII 半角), ＝  : (U+FF1D / U+FF1A 全 角)
        //   - NUMBER 含 . 浮点 容 误 标
        internal static readonly Regex WindowLabelRegex = new Regex(
            @"(?<key>CH|ZH|DH|SH)\s*[-:=#：＝]+\s*(?<value>\d+(?:\.\d+)?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private const double LabelSearchRangeMM = 1000.0;
        private const double BO_ToleranceMM = 50.0;

        public static void DetectWindowAreas(
            List<Room> rooms, Editor editor, QuoteConfig config, Action<string> log,
            // v17.5: scopeIds = null = 后 向 兼 容 (全 DWG 扫), 不 null = 仅 扫 user 框 选 中 entities.
            HashSet<ObjectId> scopeIds = null)
        {
            if (rooms == null || rooms.Count == 0) return;
            if (log == null) log = _ => { };

            // 🔧 v17.3 round3 reviewer C2/thinker A: 函 数 作 用 域 累 加 计 数 器 (供 末 尾 global summary)
            int totalHits = 0;
            int totalMisses = 0;
            int totalRoomsWithWindows = 0;
            int totalDhOnlyWindows = 0;
            int totalErrWindows = 0;

            using (var tr = editor.Document.Database.TransactionManager.StartTransaction())
            {
                var windowLines = CollectWindowLinesRaw(editor.Document.Database, tr, log);
                log("[窗户面积] DWG 扫描完成 — " + windowLines.Count + " 条窗线 (Layer='窗户' 或 显式 ColorIndex 251)");

                if (windowLines.Count == 0)
                {
                    foreach (var room in rooms)
                        if (room != null) room.WindowArea = 0.0;
                    tr.Commit();
                    return;
                }

                var textLabels = CollectTextLabelsRaw(editor.Document.Database, tr, log, scopeIds);
                log("[窗户面积] DWG 文本扫描完成 — " + textLabels.Count + " 个 CH/ZH/DH/SH 标签");
                // 🔧 v17.3 round3 reviewer C1: 入 口 文 档 化 log, 让 user 知 「无 分隔 符 CH1500 不 支 持」
                log("[窗户面积] 注意: 标签必须含分隔符 (= : # ＝ : - , 任选一); 无分隔符写法 (如 「CH1500」) 暂不支持");

                foreach (var room in rooms)
                {
                    if (room == null) continue;
                    if (room.BoundaryPolyline == null || room.BoundaryPolyline.IsDisposed)
                        continue;

                    var poly = room.BoundaryPolyline;
                    if (poly.NumberOfVertices < 3)
                    {
                        room.WindowArea = 0.0;
                        continue;
                    }

                    int n = poly.NumberOfVertices;
                    var boEdges = new List<(Point2d a, Point2d b)>();
                    for (int i = 0; i < n; i++)
                        boEdges.Add((poly.GetPoint2dAt(i), poly.GetPoint2dAt((i + 1) % n)));

                    int roomHits = 0;
                    int roomMisses = 0;
                    double totalAreaM2 = 0.0;
                    int roomScannedAll = 0;
                    int roomScannedInside = 0;
                    int roomScannedOutside = 0;
                    int roomDhOnlyCount = 0;       // C2: per-room DH-only 走 fallback 窗 数
                    int roomErrCount = 0;          // thinker A: per-room 异 常 数
                    bool roomDhOnlyWarned = false;  // Q8: per-room DH-only 警 告 去 重

                    foreach (var wLin in windowLines)
                    {
                        try
                        {
                            roomScannedAll++;
                            var wStart2d = new Point2d(wLin.StartPoint.X, wLin.StartPoint.Y);
                            var wEnd2d = new Point2d(wLin.EndPoint.X, wLin.EndPoint.Y);
                            var wMid = new Point2d(
                                (wStart2d.X + wEnd2d.X) * 0.5,
                                (wStart2d.Y + wEnd2d.Y) * 0.5);

                            if (!BoundaryHelper.IsPointInPolygonWithTolerance(wMid, poly, BO_ToleranceMM))
                            {
                                roomScannedOutside++;
                                continue;
                            }
                            roomScannedInside++;

                            double dx = wEnd2d.X - wStart2d.X;
                            double dy = wEnd2d.Y - wStart2d.Y;
                            double widthM = Math.Sqrt(dx * dx + dy * dy) / 1000.0;
                            if (widthM < 0.05) continue;

                            var resolved = ResolveWindowHeightMM(wMid, textLabels, boEdges, room.WallHeight);
                            if (resolved.HeightMM <= 0.0)
                            {
                                roomMisses++;
                                continue;
                            }

                            double heightM = resolved.HeightMM / 1000.0;
                            double areaM2 = widthM * heightM / 2.0;
                            totalAreaM2 += areaM2;
                            roomHits++;

                            log(string.Format(
                                "[窗户面积] 房 [{0}] 窗线 [{1}] 宽 {2:F3}m × 高 {3:F3}m / 2 = {4:F2} ㎡  [键:{5}]",
                                room.Name, wLin.Handle, widthM, heightM, areaM2,
                                resolved.SourceKey + (string.IsNullOrEmpty(resolved.SourceDetail)
                                    ? "" : " (" + resolved.SourceDetail + ")")));

                            // Q8: per-room DH-only 警 告 去 重; 第二 位 是 累 加 窗 数 (供 room always-log)
                            if (resolved.SourceKey == "DH-only")
                            {
                                roomDhOnlyCount++;
                                if (!roomDhOnlyWarned)
                                {
                                    roomDhOnlyWarned = true;
                                    log("[窗户面积] ⚠ 房 [" + room.Name + "] 至少 1 扇窗仅标了 DH, 按通顶 (总高 "
                                        + room.WallHeight + "mm) 估算窗高 — 若室内有吊顶, 实际应减吊顶厚");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            roomErrCount++;
                            // thinker A: 限 per-room 异常 log ≤3 行 (防 脏 数 据 100+ 窗 全 报)
                            if (roomErrCount <= 3)
                            {
                                log("[窗户面积] ⚠ 房 [" + room.Name + "] 窗线 [" + wLin.Handle + "] 处理失败: " + ex.Message);
                            }
                        }
                    }

                    room.WindowArea = Math.Round(totalAreaM2, 2, MidpointRounding.AwayFromZero);

                    totalHits += roomHits;
                    totalMisses += roomMisses;
                    totalDhOnlyWindows += roomDhOnlyCount;
                    totalErrWindows += roomErrCount;
                    if (totalAreaM2 > 0.0)
                    {
                        totalRoomsWithWindows++;
                        log(string.Format(
                            "[窗户面积] 房 [{0}] 总窗户面积 {1:F2} ㎡ (匹配 {2} 扇)",
                            room.Name, room.WindowArea, roomHits));
                    }
                    if (roomMisses > 0)
                    {
                        log("[窗户面积] 房 [" + room.Name + "] 有 " + roomMisses
                            + " 扇窗未识别高度标签 (面积按 0 计, 静默跳过)");
                    }
                    // C2: 房 级 always-log 加 DH-only 走 fallback 窗 数 + 异常 计数
                    log(string.Format(
                        "[窗户面积] 房 [{0}] 扫到 {1} 条窗线 (在 BO 内 {2}, 在 BO 外 {3}) - 匹配 {4} 扇, 无标签 {5} 扇, DH-only fallback {6} 扇, 异常 {7}, 共 {8:F2} ㎡",
                        room.Name, roomScannedAll, roomScannedInside, roomScannedOutside,
                        roomHits, roomMisses, roomDhOnlyCount, roomErrCount, room.WindowArea));
                }

                log("[窗户面积] 完成 - 房总数 " + rooms.Count
                    + ", 有窗 " + totalRoomsWithWindows
                    + ", 总配对 " + totalHits
                    + ", 总跳过(无标签) " + totalMisses
                    + ", 标签池 " + textLabels.Count
                    + ", DH-only 通顶 fallback " + totalDhOnlyWindows + " 扇"
                    + ", 异常 " + totalErrWindows);
                tr.Commit();
            }
        }

        private struct HeightResolved
        {
            public double HeightMM;
            public string SourceKey;     // "CH" / "ZH" / "DH+SH" / "DH-only" / "SH-only" / "DH+SH(neg)" / "DH-only(neg)"
            public string SourceDetail;
        }

        private static HeightResolved ResolveWindowHeightMM(
            Point2d windowMid,
            List<TextLabel> labels,
            List<(Point2d a, Point2d b)> boEdges,
            double roomTotalHeightMM)
        {
            // Pass 1: CH -> 直接 当窗高
            var ch = NearestLabel(windowMid, labels, "CH", boEdges);
            if (ch != null)
            {
                return new HeightResolved { HeightMM = ch.ValueMM, SourceKey = "CH", SourceDetail = "direct" };
            }

            // Pass 2: ZH -> 直接 当窗高
            var zh = NearestLabel(windowMid, labels, "ZH", boEdges);
            if (zh != null)
            {
                return new HeightResolved { HeightMM = zh.ValueMM, SourceKey = "ZH", SourceDetail = "direct" };
            }

            // Pass 3: 仅 DH+SH -> 减法 算高
            var dh = NearestLabel(windowMid, labels, "DH", boEdges);
            if (dh != null)
            {
                var sh = NearestLabel(windowMid, labels, "SH", boEdges);
                if (sh != null)
                {
                    double h = roomTotalHeightMM - dh.ValueMM - sh.ValueMM;
                    if (h > 0)
                    {
                        return new HeightResolved
                        {
                            HeightMM = h,
                            SourceKey = "DH+SH",
                            SourceDetail = roomTotalHeightMM + "-" + dh.ValueMM + "-" + sh.ValueMM + "=" + h
                        };
                    }
                    return new HeightResolved { HeightMM = 0, SourceKey = "DH+SH(neg)", SourceDetail = "skip" };
                }
                // Pass 3b: 仅 DH -> 默认 通顶 -> 高 = 总高 - DH
                double h2 = roomTotalHeightMM - dh.ValueMM;
                if (h2 > 0)
                {
                    return new HeightResolved
                    {
                        HeightMM = h2,
                        SourceKey = "DH-only",
                        SourceDetail = "totalHeight-DH=" + h2 + "(gapTop fallback)"
                    };
                }
                return new HeightResolved { HeightMM = 0, SourceKey = "DH-only(neg)", SourceDetail = "skip" };
            }

            // Pass 4: 仅 SH -> 信息不足 skip
            return new HeightResolved { HeightMM = 0, SourceKey = "SH-only", SourceDetail = "skip (info insufficient)" };
        }

        private static TextLabel NearestLabel(
            Point2d mid, List<TextLabel> labels, string keyFilter,
            List<(Point2d a, Point2d b)> boEdges)
        {
            TextLabel best = null;
            double bestDist = double.MaxValue;
            foreach (var lbl in labels)
            {
                if (lbl.Key != keyFilter) continue;
                double toBO = double.MaxValue;
                foreach (var (a, b) in boEdges)
                {
                    var d = BoundaryHelper.DistancePointToSegment(lbl.Position, a, b);
                    if (d < toBO) toBO = d;
                }
                if (toBO > LabelSearchRangeMM) continue;

                double toMid = Math.Sqrt(
                    (lbl.Position.X - mid.X) * (lbl.Position.X - mid.X) +
                    (lbl.Position.Y - mid.Y) * (lbl.Position.Y - mid.Y));
                if (toMid < bestDist)
                {
                    bestDist = toMid;
                    best = lbl;
                }
            }
            return best;
        }

        private class TextLabel
        {
            public string Key;
            public double ValueMM;
            public Point2d Position;
        }

        private static List<Line> CollectWindowLinesRaw(Database db, Transaction tr, Action<string> log,
            HashSet<ObjectId> scopeIds = null)
        {
            var result = new List<Line>();
            try
            {
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt == null) return result;
                var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (ms == null) return result;

                foreach (ObjectId id in ms)
                {
                    try
                    {
                        if (!id.ObjectClass.Name.Equals("AcDbLine", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var line = tr.GetObject(id, OpenMode.ForRead) as Line;
                        if (line == null) continue;

                        bool isOnWindowLayer = !string.IsNullOrWhiteSpace(line.Layer)
                            && line.Layer.Trim().Equals("窗户", StringComparison.OrdinalIgnoreCase);
                        bool isExplicitColor251 = line.ColorIndex == 251;
                        if (isOnWindowLayer || isExplicitColor251)
                            result.Add(line);
                    }
                    catch (Exception ex)
                    {
                        log("[窗户面积] ⚠ 跳过实体 " + id.ToString() + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log("[窗户面积] ⚠ ModelSpace 扫描失败: " + ex.Message);
            }
            return result;
        }

        private static List<TextLabel> CollectTextLabelsRaw(Database db, Transaction tr, Action<string> log,
            // v17.5: scopeIds = null = 后 向 兼 容 (全 DWG 扫), 不 null = 仅 扫 user 框 选 中 text 实体.
            HashSet<ObjectId> scopeIds = null)
        {
            var result = new List<TextLabel>();
            try
            {
                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                if (bt == null) return result;
                var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;
                if (ms == null) return result;

                foreach (ObjectId id in ms)
                {
                    try
                    {
                        // v17.5: scope filter — 同 步 line collector.
                        // 体 此 window-and-label 「同 scope」 表达 user 选 框 是 哪种 计 算 范围.
                        if (scopeIds != null && id != ObjectId.Null && !scopeIds.Contains(id)) continue;
                        string cls = id.ObjectClass.Name;
                        if (!cls.Equals("AcDbText", StringComparison.OrdinalIgnoreCase)
                            && !cls.Equals("AcDbMText", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var ent = tr.GetObject(id, OpenMode.ForRead);
                        if (ent == null) continue;

                        string raw = GetTextContent(ent);
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        string text = StripMTextFormatCodes(raw);
                        var matches = WindowLabelRegex.Matches(text);
                        if (matches.Count == 0) continue;

                        Point2d pos = GetTextPosition2d(ent);
                        foreach (Match m in matches)
                        {
                            string key = m.Groups["key"].Value.ToUpperInvariant();
                            if (!double.TryParse(m.Groups["value"].Value,
                                NumberStyles.Float, CultureInfo.InvariantCulture, out double valueMM))
                                continue;
                            result.Add(new TextLabel { Key = key, ValueMM = valueMM, Position = pos });
                        }
                    }
                    catch (Exception ex)
                    {
                        log("[窗户面积] ⚠ 跳过文本 " + id.ToString() + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log("[窗户面积] ⚠ 文本扫描失败: " + ex.Message);
            }
            return result;
        }

        // 🔧 v17.3 round4: 批 MText 富 文 本 中 「\\字母字串;」 格式 码
        //   不 再 删 「{...}」 整 块 (会 误 含 「{\fArial;CH=1960}」 类 含 KEY 字 串 的 内容 块)
        //   仅 删 \\X...?; 格式 码 自 身 — {} 在 strip 后 保 留, 但 { 不 在 regex key 字 集 内, 不 干 扰
        private static readonly Regex MTextFormatCodesRegex = new Regex(
            @"\\[a-zA-Z][^;]*;",
            RegexOptions.Compiled);

        private static string StripMTextFormatCodes(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            string cleaned = MTextFormatCodesRegex.Replace(raw, "");
            return cleaned.Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
        }

        private static string GetTextContent(DBObject ent)
        {
            if (ent is DBText dbText) return dbText.TextString;
            if (ent is MText mText) return mText.Contents;
            return string.Empty;
        }

        private static Point2d GetTextPosition2d(DBObject ent)
        {
            if (ent is DBText dbText)
                return new Point2d(dbText.Position.X, dbText.Position.Y);
            if (ent is MText mText)
            {
                var loc = mText.Location;
                return new Point2d(loc.X, loc.Y);
            }
            return new Point2d(0, 0);
        }
    }
}
