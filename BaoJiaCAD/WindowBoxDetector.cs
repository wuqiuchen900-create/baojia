using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace BaoJiaCAD
{
    /// <summary>
    /// v16 窗帘盒 (curtain box) 长度检测器.
    /// 流程:
    ///   1. 扫 DWG ModelSpace 所有 Line — Layer == "窗户" 或 显式 ColorIndex 251 (ByLayer 256 跳过).
    ///   2. 对每个房间, 在 boundary polyline segments (闭口) 中找 与窗线 colinear 的墙段:
    ///      - 窗线方向 vs 墙段方向 余弦相似度 >= 0.99
    ///      - 窗线中点 --> 墙段「有限线段」(proj ∈ [-tol, segLen+tol]) 垂直距离 <= EndpointToleranceMM
    ///        (v16.2.1: 中点 midpoint 取代 v16.2 的 endpoint 双检, 更耐碎片墙 — 例 1500mm 窗 / 5×300mm 段 不漏判)
    ///      - 旧版 (v16.1-) 窗线两端点 --> 墙段无限延长线 垂直距离 <= 100mm. 平行偏移 假阳性严重, 已废弃.
    ///   3. 整段 polyline segment 标记 "covered", 累加段长 (mm) -> 转米 -> room.CurtainBoxLength.
    ///   4. 顺延/倒退累积 (v16.1) 沿 colinear 邻居段 累加, dedup by HashSet.
    /// </summary>
    public static class WindowBoxDetector
    {
        // 端点 -> 墙段无限延伸线 垂直距离容差 (mm). 超此值视为 "浮空" 不计入.
        private const double EndpointToleranceMM = 100.0;
        // 窗线方向 vs 墙段方向 余弦相似度下限 (>=0.99 即共线). 正负方向反也算 (取 abs).
        private const double ColinearCosMin = 0.99;
        // 🔧 v16.2 fix 1: 端点投影必须落在墙段 [0, segLen] 范围内 (容差 -tol..+tol). 避免平行偏移 / 长墙外的
        //   false positive (e.g. 40mm 装饰 tick 在长墙的无限延长线上 命中墙). 改成 有限线段 匹配.
        // 🔧 v16.2 fix 2: 最短窗线长度 (mm). 真实窗户 ≥ 600mm (标准推拉窗/平开窗), < 400mm 几乎都是装饰/
        //   标注/墙垛短线. 用这个常量过滤掉大多数假阳性, 防止 short stub 污染 coveredSeg.
        private const double MinWindowLengthMM = 400.0;

        public static void DetectCurtainBoxLengths(
            List<Room> rooms, Editor editor, QuoteConfig config, Action<string> log)
        {
            if (rooms == null || rooms.Count == 0) return;
            if (log == null) log = _ => { };

            int totalHits = 0;
            int totalFloating = 0;
            int totalRoomsWithWindows = 0;

            using (var tr = editor.Document.Database.TransactionManager.StartTransaction())
            {
                var windowLines = CollectWindowLines(editor.Document.Database, tr, log);
                log("[窗帘盒] DWG 扫描完成 — " + windowLines.Count + " 条窗线 (Layer = \"窗户\" 或 显式 ColorIndex 251)");

                if (windowLines.Count == 0)
                {
                    foreach (var room in rooms)
                        if (room != null) room.CurtainBoxLength = 0.0;
                    tr.Commit();
                    return;
                }

                foreach (var room in rooms)
                {
                    if (room == null) continue;
                    if (room.BoundaryPolyline == null || room.BoundaryPolyline.IsDisposed)
                        continue;

                    var pline = room.BoundaryPolyline;
                    if (pline.NumberOfVertices < 2)
                    {
                        room.CurtainBoxLength = 0.0;
                        continue;
                    }

                    int roomHits = 0;
                    int roomFloat = 0;
                    double totalMM = 0.0;
                    var coveredSeg = new HashSet<int>();

                    foreach (var wLin in windowLines)
                    {
                        try
                        {
                            var wStart2d = new Point2d(wLin.StartPoint.X, wLin.StartPoint.Y);
                            var wEnd2d = new Point2d(wLin.EndPoint.X, wLin.EndPoint.Y);
                            var wDelta = new Vector2d(wEnd2d.X - wStart2d.X, wEnd2d.Y - wStart2d.Y);
                            double wLen = wDelta.Length;
                            // 🔧 v16.2 fix 2: 短线 (< 400mm) 当装饰/标注线而不是窗户, 不参与 coveredSeg 累加.
                            if (wLen < MinWindowLengthMM) continue;

                            var wDir = new Vector2d(wDelta.X / wLen, wDelta.Y / wLen);
                            // 🔧 v16.2.1: 改 endpoint check 为 midpoint check 入口 — 更耐高度碎片化墙.
                            //   例: 1500mm 窗线跨 5 段各 300mm 共线墙, 双端点 check 每段都越界会全部漏判.
                            //   midpoint 落入中间一段 → 该段 先 match → 后续 顺延/倒退 (v16.1) 把邻居 colinear 段 也
                            //   累进 coveredSeg, 总长 = 整面墙 (符合 用户的 图一 EF / 图二 CD 期望).
                            var wMid = new Point2d(
                                (wStart2d.X + wEnd2d.X) * 0.5,
                                (wStart2d.Y + wEnd2d.Y) * 0.5);

                            bool matched = false;
                            int n = pline.NumberOfVertices;
                            for (int i = 0; i < n; i++)
                            {
                                var segA = pline.GetPoint2dAt(i);
                                var segB = pline.GetPoint2dAt((i + 1) % n);
                                var segDelta = new Vector2d(segB.X - segA.X, segB.Y - segA.Y);
                                double segLen = segDelta.Length;
                                if (segLen < 1.0) continue;

                                var segDir = new Vector2d(segDelta.X / segLen, segDelta.Y / segLen);

                                double cosA = Math.Abs(wDir.DotProduct(segDir));
                                if (cosA < ColinearCosMin) continue;

                                // 🔧 v16.2.1 核心匹配检查: 单点 midpoint check.
                                //   - proj ∈ [−tol, segLen+tol] 仍是 有限线段 检查 (平行偏移假阳性 仍 捕获),
                                //   - 但只需 1 个点 (midpoint) 在段范围内 → 高碎片墙也能正确命中.
                                //   v16.2 双端 check 在 1500mm 窗 + 5×300mm 段 场景下 每段都越界 → 全部漏判;
                                //   v16.2.1 改单中点 → 命中中点所在段 → 顺延/倒退 把邻居 colinear 段 也 累进 coveredSeg.
                                if (!IsPointNearFiniteLine(wMid, segA, segDir, segLen, EndpointToleranceMM)) continue;

                                coveredSeg.Add(i);
                                roomHits++;
                                matched = true;
                                log("[窗帘盒] 房 [" + room.Name + "] 窗线 [" + wLin.Handle + "] (长 " + wLen.ToString("F1") + "mm) 命中墙段 #" + i + " (长 " + segLen.ToString("F1") + "mm)");

                                // 🔧 v16.1 fix (reviewer #1): 在 polyline 中沿窗方向 顺延/倒退 累加 colinear 邻居段.
                                //   修「AG + GH + HB 三段都被画到」时只 1 段 被计 undercount.
                                //   同时 闭口 polyline 用 safety < n 限 步 防 死循环 (向一个方向转一整圈).
                                //   顺延:
                                {
                                    int nextIdx = (i + 1) % n;
                                    int safety = 0;
                                    while (safety++ < n)
                                    {
                                        var nsA = pline.GetPoint2dAt(nextIdx);
                                        var nsB = pline.GetPoint2dAt((nextIdx + 1) % n);
                                        var nsDelta = new Vector2d(nsB.X - nsA.X, nsB.Y - nsA.Y);
                                        double nsLen = nsDelta.Length;
                                        if (nsLen < 1.0) break;
                                        var nsDir = new Vector2d(nsDelta.X / nsLen, nsDelta.Y / nsLen);
                                        if (Math.Abs(wDir.DotProduct(nsDir)) < ColinearCosMin) break;
                                        coveredSeg.Add(nextIdx);
                                        log("[窗帘盒] ↪ 顺延覆盖墙段 #" + nextIdx + " 长 " + nsLen.ToString("F1") + "mm (窗同向)");
                                        nextIdx = (nextIdx + 1) % n;
                                    }
                                }
                                // 倒退:
                                {
                                    int prevIdx = (i - 1 + n) % n;
                                    int safety = 0;
                                    while (safety++ < n)
                                    {
                                        var nsA = pline.GetPoint2dAt(prevIdx);
                                        var nsB = pline.GetPoint2dAt((prevIdx + 1) % n);
                                        var nsDelta = new Vector2d(nsB.X - nsA.X, nsB.Y - nsA.Y);
                                        double nsLen = nsDelta.Length;
                                        if (nsLen < 1.0) break;
                                        var nsDir = new Vector2d(nsDelta.X / nsLen, nsDelta.Y / nsLen);
                                        if (Math.Abs(wDir.DotProduct(nsDir)) < ColinearCosMin) break;
                                        coveredSeg.Add(prevIdx);
                                        log("[窗帘盒] ↩ 倒退覆盖墙段 #" + prevIdx + " 长 " + nsLen.ToString("F1") + "mm (窗同向)");
                                        prevIdx = (prevIdx - 1 + n) % n;
                                    }
                                }

                                break;
                            }

                            // 🔧 v16.2 fix 3: 收敛 ⚠ 浮空 日志刷屏. 之前 每个 (room, wLin) 未命中 都打一行 —
                            //   9 房 × 500 lines ≈ 4500 行日志湮没有用信息. 改成 在房间 loop 结束后 房级汇总 一行,
                            //   roomFloat 仍然累计 进 totalFloating (兼容 末尾 [窗帘盒] 完成 统计 字段名).
                            //   调试时 想看具体 哪些窗线浮空 → 全局 matchedLineIds 在 foreach rooms 结束后 log 列出 Top N.
                            if (!matched)
                            {
                                roomFloat++;
                            }
                        }
                        catch (Exception ex)
                        {
                            log("[窗帘盒] ⚠ 房 [" + room.Name + "] 窗线 [" + wLin.Handle + "] 处理失败: " + ex.Message);
                        }
                    }

                    int n2 = pline.NumberOfVertices;
                    for (int i = 0; i < n2; i++)
                    {
                        if (!coveredSeg.Contains(i)) continue;
                        var a = pline.GetPoint2dAt(i);
                        var b = pline.GetPoint2dAt((i + 1) % n2);
                        // 🔧 v16.1 fix: 不调用 a.DistanceTo(b) (build CS1061). 直接 sqrt 的平方根.
                        double dxSeg = a.X - b.X;
                        double dySeg = a.Y - b.Y;
                        totalMM += Math.Sqrt(dxSeg * dxSeg + dySeg * dySeg);
                    }
                    room.CurtainBoxLength = totalMM / 1000.0;

                    totalHits += roomHits;
                    totalFloating += roomFloat;
                    if (totalMM > 0)
                    {
                        totalRoomsWithWindows++;
                        log("[窗帘盒] 房 [" + room.Name + "] 总窗帘盒 " + room.CurtainBoxLength.ToString("F2") + " m (覆盖 " + coveredSeg.Count + " 段)");
                    }

                    // 🔧 v16.2 房级 浮空 汇总 (一条 log / 房, 替代之前 每个 (room, wLin) 未命中 一行)
                    if (roomFloat > 0)
                    {
                        log("[窗帘盒] 房 [" + room.Name + "] 有 " + roomFloat + " 条窗线不在本房墙段上 (浮空, 不计入)");
                    }
                }

                log("[窗帘盒] 完成 — 房总数 " + rooms.Count + ", 有窗 " + totalRoomsWithWindows + ", 总配对 " + totalHits + ", 浮空 " + totalFloating);
                tr.Commit();
            }
        }

        // 🔧 v16.1 fix (reviewer #5): GenerateQuoteItems 在 DetectRooms 时 CurtainBoxLength=0, room.Items[i].Quantity 也=0.
        //   WindowBoxDetector 跑完 后 同步刷新 之. ExcelExporter.FillRoomData 走 IsCurtainBoxItem 路径已 直接 读 room.CurtainBoxLength,
        //   这里 是 为 任何 可能 用 room.Items 的 UI/报告 (e.g. CategoryPanel, future 弹出) 保鲜.
        public static void UpdateRoomItemsCurtainBox(List<Room> rooms)
        {
            if (rooms == null) return;
            foreach (var room in rooms)
            {
                if (room == null || room.Items == null) continue;
                foreach (var item in room.Items)
                {
                    if (item != null && item.Name == "窗帘盒")
                        item.Quantity = room.CurtainBoxLength;
                }
            }
        }

        // 扫 ModelSpace 中所有 Line entities, 过滤 (Layer = 窗户 OR 显式 ColorIndex 251).
        private static List<Line> CollectWindowLines(Database db, Transaction tr, Action<string> log)
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
                        log("[窗帘盒] ⚠ 跳过实体 " + id.ToString() + ": " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                log("[窗帘盒] ⚠ ModelSpace 扫描失败: " + ex.Message);
            }
            return result;
        }

        // 🔧 v16.2 fix 核心: 点 P 是否落在 起点 A + 方向 dir + 长度 segLen 这条「有限线段」上 (两端各 tol mm 余量).
        //   区别于之前的 IsPointNearInfiniteLine —— 旧版只检查 垂直距离, 不限制 投影范围, 所以平行偏移 5m 远的线
        //   也会被算成 "在这面墙上". 新版强制 proj ∈ [−tol, segLen+tol], 平行偏移 / 长墙外 的 假阳性 全干掉.
        //   用 squared 距离 比较 (此 build 上 Point2d.DistanceTo 会 CS1061).
        private static bool IsPointNearFiniteLine(Point2d p, Point2d a, Vector2d dir, double segLen, double tol)
        {
            var apDelta = new Vector2d(p.X - a.X, p.Y - a.Y);
            double proj = apDelta.DotProduct(dir);
            // 投影必须在 [−tol, segLen+tol] 范围内. 否则视为不在本墙段上.
            if (proj < -tol || proj > segLen + tol) return false;

            double fx = a.X + proj * dir.X;
            double fy = a.Y + proj * dir.Y;
            double dx = p.X - fx;
            double dy = p.Y - fy;
            return (dx * dx + dy * dy) <= (tol * tol);
        }
    }
}
