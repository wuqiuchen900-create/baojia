using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BaoJiaCAD
{
    /// <summary>
    /// 一个被识别出的房间边界快照（用于跨层去重 + 重选本层去重）
    /// </summary>
    public class DetectedBoundary
    {
        public Point3d Centroid { get; set; }
        public double Area { get; set; }
    }

    /// <summary>
    /// 一个被跳过的 CAD 文字记录（含坐标+原因），便于命令列提醒 + 弹窗展示 + 写 trace
    /// </summary>
    public class SkippedTextInfo
    {
        /// <summary>原始文字内容</summary>
        public string Text { get; set; }
        /// <summary>文字插入点 X (mm) — 便于在 CAD 里 ZOOM 过去定位</summary>
        public double X { get; set; }
        /// <summary>文字插入点 Y (mm)</summary>
        public double Y { get; set; }
        /// <summary>跳过原因: "无 RoomTypeMaps Keywords" / "墙线未闭合" / "空白文字"</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// 一次 DetectRooms 的完整结果（含诊断信息）
    /// </summary>
    public class DetectionResult
    {
        public List<Room> Rooms { get; set; } = new List<Room>();
        public List<DetectedBoundary> NewBoundaries { get; set; } = new List<DetectedBoundary>();
        // 🔧 升级: List<string> → List<SkippedTextInfo> (含坐标+原因) 便于弹出提醒
        public List<SkippedTextInfo> SkippedTexts { get; set; } = new List<SkippedTextInfo>();
        public List<string> BoundaryWarnings { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// 房间识别器
    /// </summary>
    public class RoomDetector
    {
        private readonly QuoteConfig _config;
        private readonly double _wallHeight;
        private readonly List<DetectedBoundary> _previousBoundaries;

        public RoomDetector(QuoteConfig config, double wallHeight,
            List<DetectedBoundary> previousBoundaries = null)
        {
            _config = config;
            _wallHeight = wallHeight;
            _previousBoundaries = previousBoundaries ?? new List<DetectedBoundary>();
        }

        /// <summary>
        /// 从用户框选内容中识别房间。
        /// floorOverride: 防呆流程已确认的当前层（如 "一楼"），非空时强制覆盖文字前缀。
        /// isMultiFloor: 是否复式模式（影响警告输出）。
        /// </summary>
        public DetectionResult DetectRooms(Editor editor, SelectionSet selectionSet,
            string floorOverride, bool isMultiFloor)
        {
            var result = new DetectionResult();
            var currentLayerHits = new List<DetectedBoundary>();  // 本轮（本层内）累积，用于 redo 时也命中
            var db = editor.Document.Database;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var textEntities = new List<Entity>();
                foreach (SelectedObject so in selectionSet)
                {
                    if (so == null) continue;
                    var ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent == null) continue;
                    if (ent is DBText || ent is MText)
                        textEntities.Add(ent);
                }

                foreach (var ent in textEntities)
                {
                    string text = GetTextContent(ent);
                    var pos = GetTextPosition(ent);  // 🔧 升级: 把坐标也带上 (供 ZOOM 用)
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        result.SkippedTexts.Add(new SkippedTextInfo { Text = "(空白文字)", X = pos.X, Y = pos.Y, Reason = "空白" });
                        continue;
                    }

                    string roomType = MatchRoomType(text);
                    if (string.IsNullOrEmpty(roomType))
                    {
                        // 🔧 修复 #5: 未匹配关键词时跳过并给出警告，方便用户扩充 Keywords
                        result.SkippedTexts.Add(new SkippedTextInfo { Text = text, X = pos.X, Y = pos.Y, Reason = "无 RoomTypeMaps Keywords" });
                        result.Warnings.Add($"【{text}】未匹配任何房间类型关键词，已跳过。如需识别，请在 config.json 的 RoomTypeMaps 中扩充 Keywords。");
                        continue;
                    }

                    Point3d seedPoint = GetTextPoint(ent);
                    Polyline boundary = BoundaryHelper.TraceBoundary(editor, seedPoint);
                    if (boundary == null)
                    {
                        result.SkippedTexts.Add(new SkippedTextInfo { Text = text, X = pos.X, Y = pos.Y, Reason = "墙线未闭合 (BO 无法追踪到闭合多段线; 多段线/曲线不能闭合时常出现)" });
                        result.Warnings.Add($"【{text}】墙线未闭合 (BO 追踪失败)，已跳过。");
                        continue;
                    }

                    var (area, perimeter) = BoundaryHelper.GetAreaAndPerimeter(boundary);
                    var centroid = GetCentroid(boundary);

                    if (IsDuplicate(centroid, area, currentLayerHits, out var dupMsg))
                    {
                        result.BoundaryWarnings.Add(dupMsg ?? "重复边界已跳过");
                        boundary.Dispose();
                        continue;
                    }

                    string floorFromText = ExtractFloor(text);
                    string finalFloor;
                    if (!string.IsNullOrEmpty(floorOverride))
                    {
                        // 防呆已确认当前层：强制覆盖
                        if (!string.IsNullOrEmpty(floorFromText) && floorFromText != floorOverride)
                            result.Warnings.Add(
                                $"【{text}】文字楼层前缀「{floorFromText}」与当前层「{floorOverride}」不一致，按当前层「{floorOverride}」归入");
                        finalFloor = floorOverride;
                    }
                    else
                    {
                        if (isMultiFloor && string.IsNullOrEmpty(floorFromText))
                            result.Warnings.Add(
                                $"【{text}】未包含楼层前缀，将 fallback 到「一楼」");
                        finalFloor = floorFromText;
                    }

                    var room = new Room
                    {
                        Name = text,
                        RoomType = roomType,
                        FloorLevel = finalFloor,
                        FloorArea = area,
                        Perimeter = perimeter,
                        WallHeight = _wallHeight,
                        // 🔧 v16: 内存 Polyline 克隆 — AutoCAD temp polyline 随 tr 而死.
                        //   WindowBoxDetector 在 DetectRooms 之后 Read polyline → 必须克隆到自有实例 (Commands 终态 Dispose 清理).
                        BoundaryPolyline = ClonePolylineToMemory(boundary)
                    };

                    GenerateQuoteItems(room);
                    result.Rooms.Add(room);
                    var hit = new DetectedBoundary { Centroid = centroid, Area = area };
                    result.NewBoundaries.Add(hit);
                    currentLayerHits.Add(hit);  // 本层累积：重选本层时同一房间也会被命中

                    boundary.Dispose();
                }

                tr.Commit();
            }

            return result;
        }

        private string GetTextContent(Entity ent)
        {
            if (ent is DBText dbText) return dbText.TextString;
            if (ent is MText mText) return mText.Text;
            return string.Empty;
        }

        private Point3d GetTextPoint(Entity ent)
        {
            if (ent is DBText dbText) return dbText.Position;
            if (ent is MText mText) return mText.Location;
            return Point3d.Origin;
        }

        /// <summary>
        /// 🔧 升级: 拿文字插入点 (X,Y) 返回 double 元组 — 给 SkippedTextInfo 用, 便于命令列 + 弹窗显示 "位置 X=…, Y=…" 方便 ZOOM 过去。
        /// </summary>
        private (double X, double Y) GetTextPosition(Entity ent)
        {
            var pt = GetTextPoint(ent);
            return (pt.X, pt.Y);
        }

        /// <summary>
        /// 🔧 v16: AutoCAD temp Polyline (TraceBoundary 返回) → 自有 in-memory Polyline.
        ///   - AutoCAD temp entities 随 transaction 而死, WindowBoxDetector 在 DetectRooms 之后跑时原实例已不存在.
        ///   - 用 vertices 重建一个不挂 BlockTable 的 Polyline, 完全自己控制 Dispose 周期.
        ///   - 保留 GetPoint2dAt / Closed / GetClosestPointTo (几何检测需要).
        ///   - 调用方负责 Dispose (Commands.BaoJia 终态统一清掉 allRooms 中每个 room.BoundaryPolyline).
        /// </summary>
        private static Autodesk.AutoCAD.DatabaseServices.Polyline ClonePolylineToMemory(
            Autodesk.AutoCAD.DatabaseServices.Polyline src)
        {
            if (src == null) return null;
            var mem = new Autodesk.AutoCAD.DatabaseServices.Polyline();
            int n = src.NumberOfVertices;
            for (int i = 0; i < n; i++)
                mem.AddVertexAt(i, src.GetPoint2dAt(i), src.GetBulgeAt(i), 0, 0);
            mem.Closed = src.Closed;
            return mem;
        }

        private Point3d GetCentroid(Polyline pline)
        {
            try
            {
                var extents = pline.GeometricExtents;
                return new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) / 2.0,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) / 2.0,
                    0);
            }
            catch
            {
                double x = 0, y = 0;
                int count = pline.NumberOfVertices;
                // 🔧 修复 #8: 防止退化多段线(NumberOfVertices==0)产生 NaN
                if (count == 0) return Point3d.Origin;
                for (int i = 0; i < count; i++)
                {
                    var pt = pline.GetPoint2dAt(i);
                    x += pt.X;
                    y += pt.Y;
                }
                return new Point3d(x / count, y / count, 0);
            }
        }

        /// <summary>
        /// 检查重复：本层（currentLayerHits）→ 跨层已确认（_previousBoundaries）。
        /// 本层命中写"本层重复（重选时也命中）"，跨层命中写"跨层重复"。
        /// </summary>
        private bool IsDuplicate(Point3d centroid, double area,
            List<DetectedBoundary> currentLayerHits, out string warn)
        {
            warn = null;
            // 🔧 修复 #9: 放宽距离容差 100→500mm，避免复式楼上下层房间误判跨层重复
            const double distanceTolerance = 500.0;
            const double areaTolerance = 0.1;

            foreach (var g in currentLayerHits)
            {
                double dx = g.Centroid.X - centroid.X;
                double dy = g.Centroid.Y - centroid.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < distanceTolerance && Math.Abs(g.Area - area) < areaTolerance)
                {
                    warn = "本层重复：本次框选内已识别相同位置/面积";
                    return true;
                }
            }

            foreach (var g in _previousBoundaries)
            {
                double dx = g.Centroid.X - centroid.X;
                double dy = g.Centroid.Y - centroid.Y;
                double d = Math.Sqrt(dx * dx + dy * dy);
                if (d < distanceTolerance && Math.Abs(g.Area - area) < areaTolerance)
                {
                    warn = "跨层重复：与已确认楼层位置/面积相近，已跳过";
                    return true;
                }
            }

            return false;
        }

        private string MatchRoomType(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var map in _config.RoomTypeMaps)
            {
                if (map.Keywords != null && map.Keywords.Any(k => text.Contains(k)))
                    return map.RoomType;
            }
            // 🔧 修复 #5: 无关键词匹配时返回 null，让调用方将其归入 skipped，
            // 避免 CAD 标注文字（如 "标高"、"C-1"）被误识别为房间。
            // 如需捕获非标准房间名，请在 config.json 的 RoomTypeMaps 中扩充 Keywords。
            return null;
        }

        private string ExtractFloor(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || _config.TemplateSettings?.FloorAliasMap == null)
                return "";
            var match = Regex.Match(text, @"(?:^\s*|[^一-龥\d])(1F|2F|3F|4F|5F|一楼|二楼|三楼|四楼|五楼|一层|二层|三层|首层)(?=\s|$|[^一-龥\d])");
            if (!match.Success) return "";
            string raw = match.Groups[1].Value;
            return _config.TemplateSettings.FloorAliasMap.TryGetValue(raw, out var norm) ? norm : raw;
        }

        private void GenerateQuoteItems(Room room)
        {
            foreach (var configItem in _config.QuoteItems)
            {                    double quantity = 0;
                    switch (configItem.CalcRule)
                    {
                        case "Floor":
                            quantity = room.FloorArea;
                            break;
                        case "CeilingAndWall":
                            quantity = room.WallArea;
                            break;
                        case "CurtainBox":
                            // 🔧 v16.1: 这是 占位值 (DetectRooms 时 还没 WindowBoxDetector → CurtainBoxLength=0).
                            //   Commands.BaoJia 在 Export 前 调 UpdateRoomItemsCurtainBox 把它刷成真实米数.
                            quantity = 0.0;
                            break;
                        default:
                            quantity = room.FloorArea;
                            break;
                    }

                room.Items.Add(new QuoteItem
                {
                    Name = configItem.Name,
                    Unit = configItem.Unit,
                    Quantity = quantity,
                    MaterialPrice = configItem.IsSummaryItem ? 0 : configItem.MaterialPrice,
                    LaborPrice = configItem.IsSummaryItem ? 0 : configItem.LaborPrice,
                    IsSummaryItem = configItem.IsSummaryItem,
                    Description = $"[{room.Name}] {configItem.Description}"
                });
            }
        }
    }
}
