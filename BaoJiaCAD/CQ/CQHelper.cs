using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace BaoJiaCAD.CQ
{
    /// <summary>
    /// CQ (拆墙对比) v21 — 纯线段级 diff (1D 共线 布尔差集).
    ///   - 放弃 面域/Region 路径 (业主单线 + 门洞不闭合 + AutoCAD `Region.CreateFromCurves` 对门洞缺口拒绝)
    ///   - 业主画线 → 视为"轨道" → 共线分组 → 1D 区间相减
    ///   - 拆 = 老轨 - 新轨（差集 = 老轨独有段, 物理上就是"老图多出来的墙")
    ///   - 建 = 新轨 - 老轨（差集 = 新图独有段）
    /// 🔧 子目录 BaoJiaCAD/CQ/, 同 dll 不同 namespace, 方便后续 CQ 独立维护或升级.
    /// </summary>
    public static class CQHelper
    {
        // ============ 图层 / 容差常量 ============
        /// <summary>DWG 中用来画"拆除线段"的永久图层 (红色)</summary>
        public const string LayerDemolish = "CQ_拆除_红";
        /// <summary>DWG 中用来画"新建线段"的永久图层 (绿色)</summary>
        public const string LayerAdd = "CQ_砌墙_绿";
        /// <summary>共线 角度容差 (rad): 1°, 防止业主手画微微歪一丁点</summary>
        public const double AngleTolRad = 0.0175;
        /// <summary>共线 偏移容差 (mm): 3mm, 远超这个 → 即便几何方向一致也算"两条独立墙"</summary>
        public const double OffsetTolMM = 3.0;
        /// <summary>Arc 折线化 角度步长 (deg): 一个 Arc 化成 360/5=72 段线</summary>
        public const double ArcStepDeg = 5.0;
        /// <summary>1D 坐标 t 的精度位数 (取 0.01mm 抗 1e-9 浮点碎片)</summary>
        public const int TSnapDigits = 2;
        /// <summary>前置清洗 (Join 碎线) 容差 (mm): 比正式 diff 容差宽, 业主图规范</summary>
        public const double PreCleanJoinToleranceMM = 90.0;
        /// <summary>两共用锚点距离超过此值预警 (mm). 不阻断.</summary>
        public const double BasepointDistanceWarnMM = 10000.0;

        // ============ 📦 框选过滤器 ============
        /// <summary>
        /// 🔧 v22: 取 LINE / ARC / LWPOLYLINE / POLYLINE. Polyline 是业主常画的元素 (含 bulge 圆弧顶).
        /// Circle/Ellipse/Spline 依旧不在框选范围 (v21 已不写 explode, 不在本期范围).
        /// </summary>
        public static SelectionFilter BuildLineArcFilter()
        {
            return new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LINE,ARC,LWPOLYLINE,POLYLINE")
            });
        }

        // ============ 交互 (v21 保留不变) ============
        public static SelectionSet AskUserSel(Editor editor, string title)
        {
            editor.WriteMessage($"\n[CQ] 请框选 ({title})：");
            var opts = new PromptSelectionOptions
            {
                MessageForAdding = $"\n选择 ({title})：",
                MessageForRemoval = "\n移除对象："
            };
            var res = editor.GetSelection(opts, BuildLineArcFilter());
            return res.Status == PromptStatus.OK ? res.Value : null;
        }

        public static Point3d? AskUserBasepoint(Editor editor, string label)
        {
            var opts = new PromptPointOptions($"\n[CQ] 点选「{label}」 (建议选墙角交叉点):")
            {
                AllowNone = false
            };
            var res = editor.GetPoint(opts);
            return res.Status == PromptStatus.OK ? res.Value : (Point3d?)null;
        }

        // ============ 取选择 → Clone (v22 +源溯源) ============
        /// <summary>
        /// 🔧 v22: 增加 out Dictionary<Curve, ObjectId> 输出, 用于主算法 trace back 到 原 DWG ObjectId.
        /// </summary>
        public static List<Curve> ExtractAndCloneCurves(
            Transaction tr, SelectionSet sel, Editor editor, List<DBObject> trashList,
            out Dictionary<Curve, ObjectId> curveToSource)
        {
            var list = new List<Curve>();
            curveToSource = new Dictionary<Curve, ObjectId>();
            if (sel == null) return list;
            foreach (ObjectId id in sel.GetObjectIds())
            {
                try
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead);
                    if (ent is Curve c && c != null && !c.IsDisposed)
                    {
                        var clone = (Curve)c.Clone();
                        list.Add(clone);
                        trashList.Add(clone);
                        // ReferenceEquals: Curve.Clone() creates a new instance, list.Count 1:1 with source Map.
                        curveToSource[clone] = id;
                    }
                }
                catch (System.Exception ex)
                {
                    editor?.WriteMessage($"\n[CQ] ! 跳过不可读 entity id={id}: {ex.Message}");
                }
            }
            return list;
        }

        // ============ 📦 v21 前置清洗: 90mm 宽 JoinConnectedCurves ============
        // v20 留作 "图形规范化", 仅做清洗用. 主算法已不再依赖 (直接走 LINE diff).
        // 保留原 v20 函数主体, 但只保留 Polylines 给上层 explode 用.
        public struct JoinResult
        {
            public List<Curve> Polylines;
            public List<Curve> PassThrough;
            public List<Curve> Unconnected;
        }

        // ============ ⭐ v21 新: 1D Interval 数据结构 ============
        public struct Interval : IComparable<Interval>
        {
            public double Start;
            public double End;
            public Interval(double s, double e)
            {
                Start = Math.Min(s, e);
                End = Math.Max(s, e);
            }
            public double Length => End - Start;
            public bool IsDegenerate => End - Start < 1e-9;
            public int CompareTo(Interval other) => Start.CompareTo(other.Start);
            public override string ToString() => $"[{Start:F2}, {End:F2}]";
        }

        // ============ ⭐ v22 新: TrackedSegment (1D Interval + 源 溯源) ============
        /// <summary>
        /// Track 的 "未合并前" 线段粒度. 除 1D Interval 外, 还记录:
        ///   - SourceId: 原图 DWG 实体 ObjectId (Line / Polyline / Arc / 等)
        ///   - SourceIv: 这条线段在原图源 Curve 的本地 1D 进度 (distance-along-curve).
        ///               如 Line: [0, length]. 如 Polyline sub-vertex: [i*avgLen, (i+1)*avgLen].
        /// 业主原图发生 拆区 时, 反溯这个 segment 能知道 "拆的是 哪个原 Line 的 哪个子区".
        /// </summary>
        public struct TrackedSegment
        {
            public Interval Interval;       // 1D 在 Track (cross-户型对齐后)
            public ObjectId SourceId;       // 原图 DWG 实体 id
            public Interval SourceIv;       // 1D 在 原 Curve (distance-based)
        }

        // ============ ⭐ v22 新: ExplodedLine (炸后 Line + SourceId + SourceIv) ============
        /// <summary>
        /// ExplodeAllToExplodedLines 返回的中间结构. 等同于 Line + 源溯源 tuple, 进 Track 后还会重拍成 TrackedSegment.
        /// </summary>
        public struct ExplodedLine
        {
            public Line Line;
            public ObjectId SourceId;
            public Interval SourceIv;
        }

        // ============ ⭐ v22 新: DiffSegment (1D 差集输出 = 2D Line + 源溯源) ============
        /// <summary>
        /// 1D 共线布尔差集 出来的 红/绿 Line (绘制到 DWG 用) + 其溯源 (供 原 Line 删除 用).
        ///   - demolishSegments: SourceId 必有, 上层可用 ReplaceOriginalLines 反算保留 sub-lines 后擦除原 Line.
        ///   - addSegments: SourceId = ObjectId.Null (新建 没 源), 直接 AppendEntity 到新户型位置.
        /// </summary>
        public class DiffSegment
        {
            public Line Line;
            public ObjectId SourceId;
            public Interval TrackIv;
            public Interval SourceIv;
            public bool IsDemolish;
        }

        // ============ ⭐ v21 新: Track (共线轨道) ============
        /// <summary>
        /// Track = 一条 "无限长直线" 的几何实体表达. Owners 应该这样构造它:
        ///   - Dir: 单位方向向量. 标准化为 X>0 或 X=0 时 Y>0 (避免 AB/BA 顺反对冲).
        ///   - Normal: 单位垂直向量 (Dir 旋转 90° ccw).
        ///   - Origin: 任意一基点. 投影 Origin 到 Normal=0 的垂直面 (即"原点最近点") 使得同一条直线生成的 Origin 一致.
        /// OldIntervals / NewIntervals 是合并后的纯 1D 区间, OldSegments / NewSegments 是 合并前 的源溯源序列.
        /// </summary>
        public class Track
        {
            public Point3d Origin;
            public Vector3d Dir;
            public Vector3d Normal;
            public List<Interval> OldIntervals = new List<Interval>();
            public List<Interval> NewIntervals = new List<Interval>();
            // v22: 源溯源并行序列 (position-aligned with OldIntervals/NewIntervals)
            public List<TrackedSegment> OldSegments = new List<TrackedSegment>();
            public List<TrackedSegment> NewSegments = new List<TrackedSegment>();

            public string Summary()
            {
                double lenOld = TotalLength(OldIntervals);
                double lenNew = TotalLength(NewIntervals);
                return $"dir=({Dir.X:F3},{Dir.Y:F3}) #old={OldIntervals.Count} ({lenOld:F0}mm) #new={NewIntervals.Count} ({lenNew:F0}mm)";
            }
            private static double TotalLength(List<Interval> ivs)
            {
                double s = 0; foreach (var iv in ivs) s += iv.Length; return s;
            }
        }

        // ============ ⭐ v22 新: Line Diff 主入口 (带 SourceIv 溯源版) ============
        /// <summary>
        /// 1D 共线 布尔差集 主算法.
        ///   - 输入: oldCurves (原户型所有曲线, 在其原始坐标空间), newCurves (新户型, 也各自原始空间).
        ///   - curveToSource: clone → 原 ObjectId. 给 v22 ReplaceOriginalLines 反算保留子段.
        ///   - newToOld: 把 newCurves 转换到老坐标系的矩阵 (DisplacementMatrix(baseOld - baseNew)).
        ///   - 输出: demolishSegments / addSegments (DiffSegment 列表), Line (绘制) + SourceIv (溯源).
        ///   - 算法总览:
        ///       1) 全部曲线 → ExplodedLine (ARC 折线化, Polyline 顶点 split, Z 清零, SourceIv = 本地距离区间)
        ///       2) newExploded TransformBy(newToOld) → 全部到老坐标系
        ///       3) 共线分组 → tracks. oldExploded 与 newExploded 各自独立 group, 再合并到同一 track.
        ///       4) 每条 track 上, per-OldSegment 1D 区间相减 (保留 1:1 SourceId):
        ///          demolish_segment = oldSeg.Interval - cleanNewUnion → SourceIv 由旧 SubIv 按比例 reset.
        ///       5) 反投影 (1D Interval → 2D Line), 过滤掉长度 < 1mm 的 micro 碎片.
        /// </summary>
        public static void CompareLineLayouts(
            List<Curve> oldCurves,
            List<Curve> newCurves,
            Dictionary<Curve, ObjectId> curveToSource,
            Matrix3d newToOld,
            out List<DiffSegment> demolishSegments,
            out List<DiffSegment> addSegments)
        {
            demolishSegments = new List<DiffSegment>();
            addSegments = new List<DiffSegment>();
            if (oldCurves == null || newCurves == null) return;

            // 1) Explode, 携带 SourceId + SourceIv
            var oldLines = ExplodeAllToExplodedLines(oldCurves, curveToSource);
            var newLines = ExplodeAllToExplodedLines(newCurves, curveToSource);

            // 2) newLines TransformBy(old坐标系)
            foreach (var el in newLines)
            {
                if (el.Line == null || el.Line.IsDisposed) continue;
                try { el.Line.TransformBy(newToOld); }
                catch { /* skip */ }
            }

            // 3) 构建 Tracks
            var tracks = BuildAllTracks(oldLines, newLines);

            // 4) 每条 track 上 1D 区间相减 (per-segment, 保留 SourceId + SourceIv).
            foreach (var track in tracks)
            {
                if (track.OldSegments.Count == 0 && track.NewSegments.Count == 0) continue;

                var cleanOld = UnionIntervals(track.OldIntervals);
                var cleanNew = UnionIntervals(track.NewIntervals);

                // Demolish: per OldSegment (SourceId 已知)
                foreach (var oldSeg in track.OldSegments)
                {
                    var demOnThis = SubtractIntervals(new List<Interval> { oldSeg.Interval }, cleanNew);
                    foreach (var iv in demOnThis)
                    {
                        if (iv.Length < 1.0) continue;
                        var demFracS = oldSeg.Interval.Length < 1e-9 ? 0.0 : (iv.Start - oldSeg.Interval.Start) / oldSeg.Interval.Length;
                        var demFracE = oldSeg.Interval.Length < 1e-9 ? 0.0 : (iv.End - oldSeg.Interval.Start) / oldSeg.Interval.Length;
                        var demSrcIv = new Interval(
                            oldSeg.SourceIv.Start + demFracS * oldSeg.SourceIv.Length,
                            oldSeg.SourceIv.Start + demFracE * oldSeg.SourceIv.Length);
                        demolishSegments.Add(new DiffSegment
                        {
                            Line = IntervalToLine(track, iv),
                            SourceId = oldSeg.SourceId,
                            TrackIv = iv,
                            SourceIv = demSrcIv,
                            IsDemolish = true,
                        });
                    }
                }

                // Add: per NewSegment (SourceId 可能为 Null, 新图不一定追踪原 ObjectId)
                foreach (var newSeg in track.NewSegments)
                {
                    var addOnThis = SubtractIntervals(new List<Interval> { newSeg.Interval }, cleanOld);
                    foreach (var iv in addOnThis)
                    {
                        if (iv.Length < 1.0) continue;
                        var addFracS = newSeg.Interval.Length < 1e-9 ? 0.0 : (iv.Start - newSeg.Interval.Start) / newSeg.Interval.Length;
                        var addFracE = newSeg.Interval.Length < 1e-9 ? 0.0 : (iv.End - newSeg.Interval.Start) / newSeg.Interval.Length;
                        var addSrcIv = new Interval(
                            newSeg.SourceIv.Start + addFracS * newSeg.SourceIv.Length,
                            newSeg.SourceIv.Start + addFracE * newSeg.SourceIv.Length);
                        addSegments.Add(new DiffSegment
                        {
                            Line = IntervalToLine(track, iv),
                            SourceId = newSeg.SourceId,
                            TrackIv = iv,
                            SourceIv = addSrcIv,
                            IsDemolish = false,
                        });
                    }
                }
            }
        }

        // ============ ⭐ v22 新: 全部曲线 → ExplodedLine (源溯源 版) ============
        /// <summary>
        /// v22 改造: 不再返回裸 List<Line>, 返回 ExplodedLine list (Line + SourceId + SourceIv).
        ///   - SourceIv: 线段 在 原 Curve 的 本地 1D 距离 区间. Line: [0, length]; Polyline sub-segment: [cum, cum+segLen].
        ///   - 这里累积 "cumDist" 作为子段 在 原 Curve 上的 起点位置 (为后续 反推 保留子段 提供 中间表示).
        /// </summary>
        private static List<ExplodedLine> ExplodeAllToExplodedLines(IEnumerable<Curve> curves, Dictionary<Curve, ObjectId> curveToSource)
        {
            var result = new List<ExplodedLine>();
            if (curves == null) return result;
            foreach (var c in curves)
            {
                if (c == null || c.IsDisposed) continue;
                ObjectId srcId = ObjectId.Null;
                if (curveToSource != null && curveToSource.TryGetValue(c, out var found)) srcId = found;

                if (c is Line ln)
                {
                    double len = ln.Length;
                    if (len < 1e-6) continue;
                    result.Add(new ExplodedLine
                    {
                        Line = new Line(FlattenZ(ln.StartPoint), FlattenZ(ln.EndPoint)),
                        SourceId = srcId,
                        SourceIv = new Interval(0, len),
                    });
                }
                else if (c is Arc arc)
                {
                    double arcLen = arc.Length;
                    if (arcLen < 1e-6) continue;
                    var arcLines = ArcToExploded(arc, 0.0, srcId);
                    result.AddRange(arcLines);
                }
                else if (c is Polyline pl)
                {
                    var polyLines = PolylineToExploded(pl, srcId);
                    result.AddRange(polyLines);
                }
                else if (c is Circle cir)
                {
                    // Circle 业主几乎不用. 折线化, SourceIv = [0, 半径*2π].
                    double radius = cir.Radius;
                    double totalLen = 2 * Math.PI * radius;
                    var center = FlattenZ(cir.Center);
                    double sweep = 2 * Math.PI;
                    double stepRad = ArcStepDeg * Math.PI / 180.0;
                    int steps = Math.Max(8, (int)Math.Ceiling(sweep / stepRad));
                    double stepLen = totalLen / steps;
                    Point3d prevP = center + new Vector3d(radius, 0, 0);
                    for (int i = 1; i <= steps; i++)
                    {
                        double ang = (i / (double)steps) * 2 * Math.PI;
                        Point3d p = center + new Vector3d(radius * Math.Cos(ang), radius * Math.Sin(ang), 0);
                        double s = (i - 1) * stepLen;
                        double e = i * stepLen;
                        result.Add(new ExplodedLine
                        {
                            Line = new Line(prevP, p),
                            SourceId = srcId,
                            SourceIv = new Interval(s, e),
                        });
                        prevP = p;
                    }
                }
                else
                {
                    // 其它 (Spline/...): 暂时忽略 (业主几乎不用).
                }
            }
            return result;
        }

        private static List<ExplodedLine> ArcToExploded(Arc arc, double cumBaseDist, ObjectId srcId)
        {
            double sweep = arc.EndAngle - arc.StartAngle;
            if (sweep < 0) sweep += 2 * Math.PI;
            double stepRad = ArcStepDeg * Math.PI / 180.0;
            int steps = Math.Max(4, (int)Math.Ceiling(sweep / stepRad));
            var center = FlattenZ(arc.Center);
            double r = arc.Radius;
            Point3d prevP = center + new Vector3d(r * Math.Cos(arc.StartAngle), r * Math.Sin(arc.StartAngle), 0);
            var result = new List<ExplodedLine>(steps);
            double arcLen = 0;  // 累积 在 [0, arc.Length] 上的 距离, 以 步长 推进
            for (int i = 1; i <= steps; i++)
            {
                double ang = arc.StartAngle + sweep * (i / (double)steps);
                Point3d p = center + new Vector3d(r * Math.Cos(ang), r * Math.Sin(ang), 0);
                double prevAng = arc.StartAngle + sweep * ((i - 1) / (double)steps);
                double prevSegLen = r * sweep / steps;
                result.Add(new ExplodedLine
                {
                    Line = new Line(prevP, p),
                    SourceId = srcId,
                    SourceIv = new Interval(cumBaseDist + arcLen, cumBaseDist + arcLen + prevSegLen),
                });
                arcLen += prevSegLen;
                prevP = p;
            }
            return result;
        }

        // v22: Polyline 炸为 ExplodedLine. 累计 Cumulative Distance 作为 SourceIv 起点.
        private static List<ExplodedLine> PolylineToExploded(Polyline pl, ObjectId srcId)
        {
            var result = new List<ExplodedLine>();
            int n = pl.NumberOfVertices;
            if (n < 2) return result;
            bool closed = pl.Closed;
            int segCount = closed ? n : n - 1;
            Point3d[] pts = new Point3d[n];
            for (int i = 0; i < n; i++)
            {
                var p2 = pl.GetPoint2dAt(i);
                pts[i] = new Point3d(p2.X, p2.Y, 0);
            }
            double cumDist = 0;
            for (int i = 0; i < segCount; i++)
            {
                int next = (i + 1) % n;
                double bulge = pl.GetBulgeAt(i);
                double chordLen = pts[i].DistanceTo(pts[next]);
                if (Math.Abs(bulge) < 1e-6)
                {
                    result.Add(new ExplodedLine
                    {
                        Line = new Line(pts[i], pts[next]),
                        SourceId = srcId,
                        SourceIv = new Interval(cumDist, cumDist + chordLen),
                    });
                    cumDist += chordLen;
                }
                else
                {
                    Arc arc = BulgeSegmentToArc(pts[i], pts[next], bulge);
                    if (arc != null)
                    {
                        double arcLen = arc.Length;
                        var arcLines = ArcToExploded(arc, cumDist, srcId);
                        result.AddRange(arcLines);
                        cumDist += arcLen;
                    }
                    else
                    {
                        result.Add(new ExplodedLine
                        {
                            Line = new Line(pts[i], pts[next]),
                            SourceId = srcId,
                            SourceIv = new Interval(cumDist, cumDist + chordLen),
                        });
                        cumDist += chordLen;
                    }
                }
            }
            return result;
        }

        // v21.1: 从两个端点 + bulge 推 AutoCAD Arc (center, radius, sweep).
        //   bulge = tan(sweep/4), sweep 是 ccw 弧度 (sign = sign(bulge)).
        private static Arc BulgeSegmentToArc(Point3d p1, Point3d p2, double bulge)
        {
            try
            {
                double chordLen = p1.DistanceTo(p2);
                if (chordLen < 1e-9) return null;
                double sweep = 4.0 * Math.Atan(bulge);  // ccw sweep, sign = sign(bulge)
                if (Math.Abs(sweep) < 1e-6) return null;
                double radius = chordLen / (2.0 * Math.Sin(Math.Abs(sweep) / 2.0));
                Vector3d chordDir = (p2 - p1) / chordLen;
                // perp_ccw = (-dy, dx) 是 chord 的 ccw 90° 旋转. 在 AutoCAD y-up 系中即"左侧".
                Vector3d perpUnit = new Vector3d(-chordDir.Y, chordDir.X, 0);
                // sagitta: chord_mid 沿 -perp_ccw 偏移 bulge 倍半弦 = 中心在凸的反方向.
                double sag = (chordLen / 2.0) * bulge;
                var midPt = new Point3d((p1.X + p2.X) / 2.0, (p1.Y + p2.Y) / 2.0, 0);
                var center = midPt - perpUnit * sag;
                double startAngle = Math.Atan2(p1.Y - center.Y, p1.X - center.X);
                double endAngle = startAngle + sweep;  // sweep>0 → endAngle > startAngle (ccw 正向)
                return new Arc(center, new Vector3d(0, 0, 1), radius, startAngle, endAngle);
            }
            catch { return null; }
        }

        private static Point3d FlattenZ(Point3d p) => new Point3d(p.X, p.Y, 0);

        // ============ ⭐ v21 新: 共线分组 -> tracks ============
        // 1. 先把每一根 Line 都按"自带的端点" + "标准化方向" 决定它"应该归到哪个 track".
        //    - Track 一标准化方向: 把方向向量归一化, 强制 X>0; X≈0 时 Y>0.
        //    - Track 一标准化位置: 把 Line 的一个端点垂直投影到 Track.Normal=0 平面上当作 Origin.
        //    - 这样业主对同一条直线 AB/BA / 任意点起都生成同一组 (Origin, Dir+Normal).
        // 2. 把 oldLines / newLines 分类全部并到这些 tracks.
        // 3. 严格 3mm 容差: track 错位超过 3mm 即便方向一致也算两条独立墙 (家装常见: 横梁略错位 = 砸墙后重新砌).
        private static List<Track> BuildAllTracks(List<ExplodedLine> oldLines, List<ExplodedLine> newLines)
        {
            var tracks = new List<Track>();
            // 第一遍: oldLines 都进来建 track, 同时记录 intervals
            foreach (var el in oldLines)
            {
                if (el.Line == null || el.Line.Length < 1e-6) continue;
                var track = FindOrCreateTrack(tracks, el.Line);
                if (track != null) AddInterval(track, el.Line, el.SourceId, el.SourceIv, isOld: true);
            }
            // 第二遍: newLines 重新遍历 (track list 已有 old 决定的结构, 看看能否 match)
            foreach (var el in newLines)
            {
                if (el.Line == null || el.Line.Length < 1e-6) continue;
                var track = FindOrCreateTrack(tracks, el.Line);
                if (track != null) AddInterval(track, el.Line, el.SourceId, el.SourceIv, isOld: false);
            }
            return tracks;
        }

        private static Track FindOrCreateTrack(List<Track> tracks, Line line)
        {
            // 标准化 line 的方向作为候选 Dir
            var dir = CanonicalDirection(line.StartPoint, line.EndPoint);
            var normal = new Vector3d(-dir.Y, dir.X, 0);
            // track 的 Origin 选 line.StartPoint Z=0 (off-set 检查不依赖 Origin 精确在线上).
            var basePt = FlattenZ(line.StartPoint);
            var candidateOrigin = FlattenZ(line.EndPoint);

            // 在已有 tracks 找方向一致 + off-line 距离 < OffsetTolMM
            foreach (var t in tracks)
            {
                // 角度一致性检查 (dir 标准化后 X>0, 直接 dot 检查)
                double dot = Math.Abs(dir.X * t.Dir.X + dir.Y * t.Dir.Y);
                if (dot < Math.Cos(AngleTolRad)) continue;  // 角度差 > AngleTolRad → 不同 track

                // 计算 line 的两个端点到 track.Normal 轴的 off-set
                double offA = (line.StartPoint - t.Origin).DotProduct(t.Normal);
                double offB = (line.EndPoint - t.Origin).DotProduct(t.Normal);
                double offBase = (basePt - t.Origin).DotProduct(t.Normal);
                double offCand = (candidateOrigin - t.Origin).DotProduct(t.Normal);
                double maxOff = Math.Max(Math.Max(Math.Abs(offA), Math.Abs(offB)),
                                 Math.Max(Math.Abs(offBase), Math.Abs(offCand)));
                if (maxOff > OffsetTolMM) continue;

                return t;
            }
            // 没找到 → 新建 track
            var nt = new Track
            {
                Origin = basePt,
                Dir = dir,
                Normal = normal
            };
            tracks.Add(nt);
            return nt;
        }

        private static Vector3d CanonicalDirection(Point3d a, Point3d b)
        {
            var v = new Vector3d(b.X - a.X, b.Y - a.Y, 0);
            if (v.Length < 1e-9) return new Vector3d(1, 0, 0);  // 退化
            v = v / v.Length;
            // 强制 X>0; 若 X≈0 则 Y>0
            if (v.X < -1e-9) return new Vector3d(-v.X, -v.Y, v.Z);
            if (Math.Abs(v.X) < 1e-9 && v.Y < 0) return new Vector3d(-v.X, -v.Y, v.Z);
            return v;
        }

        // 🔧 v21.1: NormalProject 已 cleanup (改用 FlattenZ 内联调用).

        private static void AddInterval(Track track, Line line, ObjectId sourceId, Interval sourceIv, bool isOld)
        {
            // 把 line 两端点投影到 track 的 1D t 坐标 (t = (pt - origin) . dot(dir))
            double t0 = (line.StartPoint - track.Origin).DotProduct(track.Dir);
            double t1 = (line.EndPoint - track.Origin).DotProduct(track.Dir);
            // snap 到 0.01mm 抗 浮点碎片
            t0 = Math.Round(t0, TSnapDigits);
            t1 = Math.Round(t1, TSnapDigits);
            var iv = new Interval(t0, t1);
            if (iv.IsDegenerate) return;
            if (isOld)
            {
                track.OldIntervals.Add(iv);
                track.OldSegments.Add(new TrackedSegment { Interval = iv, SourceId = sourceId, SourceIv = sourceIv });
            }
            else
            {
                track.NewIntervals.Add(iv);
                track.NewSegments.Add(new TrackedSegment { Interval = iv, SourceId = sourceId, SourceIv = sourceIv });
            }
        }

        // ============ ⭐ v21 新: 1D 区间合并 (OVERKILL) ============
        /// <summary>
        /// 把"重叠 + 微小缝隙"的多段 interval 合成 1 段.
        /// e.g. [0,100] + [50,80] + [110,200] → [0,200] (130 与 110 缝隙小于 OffsetTolMM 合并).
        /// </summary>
        private static List<Interval> UnionIntervals(List<Interval> input)
        {
            if (input == null || input.Count == 0) return new List<Interval>();
            // 拷贝 & 按区间起点排序
            var sorted = new List<Interval>(input);
            sorted.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<Interval>();
            Interval cur = sorted[0];
            for (int i = 1; i < sorted.Count; i++)
            {
                var iv = sorted[i];
                if (iv.Start <= cur.End + OffsetTolMM)
                {
                    // 重叠或紧贴 → 合并
                    cur.End = Math.Max(cur.End, iv.End);
                }
                else
                {
                    if (!cur.IsDegenerate) merged.Add(cur);
                    cur = iv;
                }
            }
            if (!cur.IsDegenerate) merged.Add(cur);
            return merged;
        }

        // ============ ⭐ v21 新: 1D 区间差集 (Subtract A − B) ============
        /// <summary>
        /// 经典 1D 重叠切割: A − B = A 中去掉 B 重叠的部分.
        /// </summary>
        private static List<Interval> SubtractIntervals(List<Interval> a, List<Interval> b)
        {
            var result = new List<Interval>();
            if (a == null || a.Count == 0) return result;
            if (b == null || b.Count == 0) { foreach (var iv in a) result.Add(iv); return result; }

            foreach (var ivA in a)
            {
                // 当前剩余区间 = [curS, curE], 依次用 B 切割
                var remainings = new List<Interval> { ivA };
                foreach (var ivB in b)
                {
                    if (ivB.End <= ivA.Start || ivB.Start >= ivA.End) continue;  // B 与 A 不相交
                    var tmp = new List<Interval>();
                    foreach (var rem in remainings)
                    {
                        if (ivB.End <= rem.Start || ivB.Start >= rem.End)
                        {
                            tmp.Add(rem);  // rem 与 ivB 不相交 → 原样保留
                            continue;
                        }
                        // 相交, 切
                        if (ivB.Start > rem.Start)
                            tmp.Add(new Interval(rem.Start, ivB.Start));
                        if (ivB.End < rem.End)
                            tmp.Add(new Interval(ivB.End, rem.End));
                    }
                    remainings = tmp;
                    if (remainings.Count == 0) break;
                }
                foreach (var rem in remainings)
                    if (!rem.IsDegenerate) result.Add(rem);
            }
            return result;
        }

        // ============ ⭐ v21 新: 1D Interval → 2D Line 反投影 ============
        private static Line IntervalToLine(Track track, Interval iv)
        {
            Point3d p0 = track.Origin + track.Dir * iv.Start;
            Point3d p1 = track.Origin + track.Dir * iv.End;
            return new Line(p0, p1);
        }

        // ============ v21 保留: 基点距预警 / 平移矩阵 ============
        public static bool NeedsBasepointDistanceWarn(Point3d a, Point3d b, double warnMM = BasepointDistanceWarnMM)
        {
            return a.DistanceTo(b) > warnMM;
        }

        public static Vector3d ComputeLayoutOffset(Point3d baseOld, Point3d baseNew)
        {
            return new Vector3d(baseOld.X - baseNew.X, baseOld.Y - baseNew.Y, baseOld.Z - baseNew.Z);
        }

        public static Matrix3d DisplacementMatrix(Vector3d v) => Matrix3d.Displacement(v);

        // ============ v21 改造: DWG 写入 (List<Line> 而不是 Region) ============
        /// <summary>
        /// 🔧 v21 改造: 直接写入 List<Line> 到指定 layer 色 (绕开 Region.Explode).
        ///   - 每条 Line 设 Layer + Color, AppendEntity → AddNewlyCreatedDBObject.
        ///   - 不归 caller finally, 因为 caller 调本函数前已经 trash.Add(list) 防 leak.
        /// </summary>
        public static int WriteDiffLinesToLayer(List<Line> lines, string layerName, Transaction tr, BlockTableRecord btr, List<DBObject> trash)
        {
            if (lines == null || lines.Count == 0) return 0;
            Color color = LayerColor(layerName);
            int written = 0;
            foreach (var line in lines)
            {
                if (line == null || line.IsDisposed) continue;
                try
                {
                    line.Layer = layerName;
                    line.Color = color;
                    // 🔧 v22.2: 拆 / 建 overlay 线型 改 ACAD_ISO03W100 (ISO 虚线). 
                    //   ACAD_ISO03W100 是 autoCAD 默认安装的 标准 linetype, 大多数 DWG 都 有.
                    //   如果该 DWG 中 不 装载, AutoCAD 回退 为 CONTINUOUS (不报错, 但业主 会看不到 虚线 样式).
                    //   LinetypeScale = 8 (业主 要求). 经 验 业主 实测 后, 如需 玄  比例 可 改 .
                    line.Linetype = "ACAD_ISO03W100";
                    line.LinetypeScale = 8.0;
                    // v21.1 fix: AppendEntity + AddNewlyCreatedDBObject 后, entity 归 DWG-managed.
                    //   caller finally 中不能再 Dispose (会触发 ePermanentlyErased 把刚写的抹掉).
                    //   成功的不入 trash. 失败的(append 异常)入 trash 让 caller finally 清理.
                    btr.AppendEntity(line);
                    tr.AddNewlyCreatedDBObject(line, true);
                    written++;
                }
                catch
                {
                    if (trash != null) trash.Add(line);  // 失败归 trash
                }
            }
            return written;
        }

        // ============ ⭐ v22 新: ReplaceOriginalLines (B+F 路线实现) ============
        /// <summary>
        /// 反推 "保留 sub-intervals" 替换 原图 DWG 实体. 流程 (v22):
        ///   1) Group demolishSegments by SourceId.
        ///   2) 对每个 源 Curve:
        ///       a) 获取 fullLen via Curve.GetLength().
        ///       b) demMerged = UnionIntervals(demOnSrc).
        ///       c) keptList = SubtractIntervals([0, fullLen], demMerged). 即 owner 原图保留段.
        ///       d) 从 demMerged 边界 凑 split point (剔除 ≤1e-3 靠近端点), 转参数 GetParameterAtDistance.
        ///       e) GetSplitCurves(splitParams) → DBObjectCollection fragments.
        ///       f) 每 fragment 用 midDist 判 是 kept 还是 dem.
        ///       g) kept fragment: 继承 Layer/Color/Linetype/LineWeight → XData → AppendEntity + AddNewlyCreatedDBObject.
        ///       h) dem fragment: 本函数手 Dispose (不入 trash, 免 GetSplitCurves 返回物被 caller 误删).
        ///       i) srcCurve.Erase(true) 擦原 实体.
        /// 边界防护:
        ///   - 拆点贴近源端点 ±1e-3mm 跳过 (免 eInvalid).
        ///   - tr.GetObject 抛 / GetLength 抛 / GetSplitCurves 抛 → 该源 跳过 (不擦免 业主原图被擦后光光).
        ///   - keptList == 全长 (例如业主拆除仅贴端点 ≤1e-3mm) → 该源不处理.
        ///   - kept fragment append 全失败 → 不擦源 (兜原貌).
        /// 资源:
        ///   - dem 片段 本函数手 Dispose 防泄漏.
        ///   - 原 srcCurve 不入 trash (tr 在 using scope Dispose).
        ///   - appendEntity 成功的 kept Piece 入 DWG (caller finally 不要 Dispose).
        /// </summary>
        public static int ReplaceOriginalLines(List<DiffSegment> demolishSegments, Transaction tr, BlockTableRecord btr, Editor editor, List<DBObject> trash)
        {
            if (demolishSegments == null || demolishSegments.Count == 0) return 0;
            if (tr == null || btr == null) return 0;

            // Group by SourceId, only valid demolish segments with non-trivial SourceIv
            var bySource = new Dictionary<ObjectId, List<Interval>>();
            foreach (var seg in demolishSegments)
            {
                if (seg == null) continue;  // v22.1: 去除 IsDemolish 过滤 — 函数在 demolish/add 两组 DiffSegment 上 泛型 動作
                if (seg.SourceId.IsNull || seg.SourceId.IsErased || seg.SourceId.IsEffectivelyErased) continue;
                if (seg.SourceIv.Length < 1e-6) continue;
                if (!bySource.TryGetValue(seg.SourceId, out var lst))
                {
                    lst = new List<Interval>();
                    bySource[seg.SourceId] = lst;
                }
                lst.Add(seg.SourceIv);
            }
            if (bySource.Count == 0) return 0;

            int replacedCount = 0;
            int keepAppended = 0;
            int demDiscarded = 0;
            int errCount = 0;

            foreach (var kv in bySource)
            {
                ObjectId srcId = kv.Key;
                List<Interval> demOnSrc = kv.Value;
                Curve srcCurve = null;
                try
                {
                    var obj = tr.GetObject(srcId, OpenMode.ForRead);
                    srcCurve = obj as Curve;
                    if (srcCurve == null || srcCurve.IsDisposed) continue;
                }
                catch { errCount++; continue; }

                double fullLen;
                try { fullLen = srcCurve.GetDistAtPoint(srcCurve.EndPoint); }
                catch { errCount++; continue; }
                if (fullLen <= 0) continue;

                // Compute kept sub-intervals on source (=full range minus demolish).
                var demMerged = UnionIntervals(demOnSrc);
                var fullRange = new List<Interval> { new Interval(0.0, fullLen) };
                var keptList = SubtractIntervals(fullRange, demMerged);

                // Quick no-op case: source completely kept (e.g. demolish only at endpoints ≤1e-3mm)
                if (keptList.Count == 1 && Math.Abs(keptList[0].Length - fullLen) < 1e-6)
                {
                    continue;  // nothing to replace
                }

                // Build split-parameter collection from dem boundaries (within-±1e-3 endpoint tolerance).
                var splitParamsColl = new DoubleCollection();
                foreach (var iv in demMerged)
                {
                    if (iv.Start > 1e-3 && iv.Start < fullLen - 1e-3)
                    {
                        double p;
                        try { p = srcCurve.GetParameterAtDistance(iv.Start); }
                        catch { continue; }
                        if (!double.IsNaN(p) && !double.IsInfinity(p)) splitParamsColl.Add(p);
                    }
                    if (iv.End > 1e-3 && iv.End < fullLen - 1e-3)
                    {
                        double p;
                        try { p = srcCurve.GetParameterAtDistance(iv.End); }
                        catch { continue; }
                        if (!double.IsNaN(p) && !double.IsInfinity(p)) splitParamsColl.Add(p);
                    }
                }

                // GetSplitCurves → DBObjectCollection (fragments BETWEEN split points).
                DBObjectCollection pieces = null;
                if (splitParamsColl.Count > 0)
                {
                    try { pieces = srcCurve.GetSplitCurves(splitParamsColl); }
                    catch
                    {
                        editor?.WriteMessage(
                            "\n[CQ] ! 源 id=" + srcId.ToString() +
                            " GetSplitCurves 抛错, 该源 不擦除 保留 原貌.");
                        errCount++;
                        continue;
                    }
                }

                // Classify each fragment as kept (midDist within keptList) or dem.
                List<DBObject> demPieces = new List<DBObject>();
                int thisKeptAppended = 0;
                if (pieces != null && pieces.Count > 0)
                {
                    foreach (DBObject pieceObj in pieces)
                    {
                        if (pieceObj == null || pieceObj.IsDisposed) continue;
                        Curve pieceCurve = pieceObj as Curve;
                        if (pieceCurve == null) { SafeDispose(pieceObj); continue; }

                        // midDist: 中点 在 原 source 上 的 本地距离
                        double midDist = -1;
                        try
                        {
                            double sDist = srcCurve.GetDistAtPoint(pieceCurve.StartPoint);
                            double eDist = srcCurve.GetDistAtPoint(pieceCurve.EndPoint);
                            midDist = (sDist + eDist) * 0.5;
                        }
                        catch { midDist = -1; }

                        bool isKept = midDist >= 0 &&
                            keptList.Any(k => midDist >= k.Start - 1e-6 && midDist <= k.End + 1e-6);

                        if (!isKept) { demPieces.Add(pieceObj); continue; }

                        // Inherit Layer/Color/Linetype/LineWeight + XData
                        try { pieceCurve.Layer = srcCurve.Layer; } catch { }
                        try { pieceCurve.Color = srcCurve.Color; } catch { }
                        try { pieceCurve.Linetype = srcCurve.Linetype; } catch { }
                        try { pieceCurve.LineWeight = srcCurve.LineWeight; } catch { }
                        try
                        {
                            var xdBuf = srcCurve.GetXDataForApplication("*");
                            if (xdBuf != null) pieceCurve.XData = xdBuf;
                        }
                        catch { /* XData missing is OK */ }

                        // Append to DWG - 失败 -> dispose, 不擦原
                        try
                        {
                            btr.AppendEntity(pieceCurve);
                            tr.AddNewlyCreatedDBObject(pieceCurve, true);
                            thisKeptAppended++;
                        }
                        catch
                        {
                            try { if (!pieceObj.IsDisposed) pieceObj.Dispose(); } catch { }
                            errCount++;
                        }
                    }
                }

                // 决策: keptList 有但 thisKeptAppended = 0 → kept append 全失败, 不擦原 (兑 免 黑 撞)
                bool fullyDemolished = keptList.Count == 0;
                if (!fullyDemolished && thisKeptAppended == 0 && pieces != null && pieces.Count > 0)
                {
                    editor?.WriteMessage(
                        "\n[CQ] ! 源 id=" + srcId.ToString() +
                        " kept fragment append 全失败, 不擦除 保持原貌.");
                    foreach (var p in demPieces) SafeDispose(p);
                    demDiscarded += demPieces.Count;
                    errCount++;
                    continue;
                }

                // Erase original.
                bool sourceErased = false;
                try
                {
                    srcCurve.UpgradeOpen();
                    srcCurve.Erase(true);
                    sourceErased = true;
                }
                catch
                {
                    errCount++;
                    // Erase 失败: 清 dem pieces, 不计 replace.
                    foreach (var p in demPieces) SafeDispose(p);
                    demDiscarded += demPieces.Count;
                    continue;
                }

                if (sourceErased)
                {
                    replacedCount++;
                    keepAppended += thisKeptAppended;
                }

                // Dispose dem pieces (它们 是 GetSplitCurves 创建的临时对象, 不属 DWG).
                foreach (var p in demPieces) SafeDispose(p);
                demDiscarded += demPieces.Count;
            }

            if (editor != null)
            {
                editor.WriteMessage(string.Format(
                    "\n[CQ] 原图 替换: {0} 个 实体 被擦除 (其中 保留重建 {1} 条), {2} 段 dem 弃, {3} 错.",
                    replacedCount, keepAppended, demDiscarded, errCount));
            }
            return replacedCount;
        }

        // ============ v21 保留: 图层 + Color + Dispose 工具 ============
        public static Color LayerColor(string layerName)
        {
            if (layerName == LayerDemolish) return Color.FromRgb(255, 0, 0);
            if (layerName == LayerAdd) return Color.FromRgb(0, 200, 0);
            return Color.FromRgb(255, 255, 0);
        }

        public static ObjectId EnsureLayer(string layerName, Database db, Transaction tr, Color color)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(layerName)) return lt[layerName];
            var ltr = new LayerTableRecord
            {
                Name = layerName,
                Color = color
            };
            lt.UpgradeOpen();
            ObjectId id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        // ============ ⭐ v22.2: CheckLinetypeExists (纯检查, 不  手动 装载 — AutoCAD fallback) ============
        /// <summary>
        /// ACAD_ISO03W100 是 ISO standard linetype (在 acadiso.lin), 不 所有 业主 DWG 都 装 装 .
        /// 这里 仅 检查是否存在 并 log, 不  手动 装载 (SymbolUtilities API 路径 跨 车主 AutoCAD 版本 不 一 不).
        /// owner Line.Linetype 设置 为 name 后 — 若 name 不 在 LinetypeTable 中, AutoCAD 静默 回退 CONTINUOUS.
        /// </summary>
        public static bool CheckLinetypeExists(string name, Database db, Transaction tr, Editor editor = null)
        {
            try
            {
                var ltt = (LinetypeTable)tr.GetObject(db.LinetypeTableId, OpenMode.ForRead);
                if (!ltt.Has(name))
                {
                    editor?.WriteMessage(
                        "\n[CQ] ! linotype \"" + name + "\" 不在 当前 DWG 中. 拆/建 overlay 将回退 CONTINUOUS (虚线效果 失 效)."
                      + "\n[CQ] 快 速 修复: 在 AutoCAD 中 打开 DWG, 运行 NETLOAD 该 lin (或 acadiso.lin) 后 重 设 该 Lots."
                    );
                    return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void SafeDispose(DBObject obj)
        {
            try { if (obj != null && !obj.IsDisposed) obj.Dispose(); }
            catch { /* best-effort */ }
        }
    }
}
