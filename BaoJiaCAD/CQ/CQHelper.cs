using System;
using System.Collections.Generic;
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
        /// 🔧 v21: 取 LINE + ARC. Polyline/Circle 等封闭元素业主几乎不用, 直接排除以减噪声.
        /// </summary>
        public static SelectionFilter BuildLineArcFilter()
        {
            return new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LINE,ARC")
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

        // ============ 取选择 → Clone (v21 保留不变) ============
        public static List<Curve> ExtractAndCloneCurves(Transaction tr, SelectionSet sel, Editor editor, List<DBObject> trashList)
        {
            var list = new List<Curve>();
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

        // ============ ⭐ v21 新: Track (共线轨道) ============
        /// <summary>
        /// Track = 一条 "无限长直线" 的几何实体表达. Owners 应该这样构造它:
        ///   - Dir: 单位方向向量. 标准化为 X>0 或 X=0 时 Y>0 (避免 AB/BA 顺反对冲).
        ///   - Normal: 单位垂直向量 (Dir 旋转 90° ccw).
        ///   - Origin: 任意一基点. 投影 Origin 到 Normal=0 的垂直面 (即"原点最近点") 使得同一条直线生成的 Origin 一致.
        /// OldSegments / NewSegments 是这条轨道上"业主画的所有线段", 已投影到 1D [t_start, t_end] 的 Interval.
        /// </summary>
        public class Track
        {
            public Point3d Origin;
            public Vector3d Dir;
            public Vector3d Normal;
            public List<Interval> OldIntervals = new List<Interval>();
            public List<Interval> NewIntervals = new List<Interval>();

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

        // ============ ⭐ v21 新: Line Diff 主入口 ============
        /// <summary>
        /// 1D 共线 布尔差集 主算法.
        ///   - 输入: oldCurves (原户型所有曲线, 在其原始坐标空间), newCurves (新户型, 也各自原始空间).
        ///   - newToOld: 把 newCurves 转换到老坐标系的矩阵 (DisplacementMatrix(baseOld - baseNew)).
        ///   - 输出:
        ///       demolishLines: 拆除线段 (老坐标系空间, 直接写到 DWG 拆墙红层, 不需 un-shift)
        ///       addLines:      新建线段 (在老坐标系空间返回, caller 需自己决定是否 un-shift 回新户型坐标空间)
        ///   - 算法总览:
        ///       1) 全部曲线 → 纯 Line (ARC 折线化, Polyline 按顶点 explode 成 Line, Z 清零)
        ///       2) newLines TransformBy(newToOld) → 全部到老坐标系
        ///       3) 共线分组 → tracks. oldLines 与 newLines 各自独立 group, 再合并到同一 track.
        ///       4) 每条 track 上, 1D 区间相减:
        ///          demolish = oldIntervalUnion - newIntervalUnion
        ///          add      = newIntervalUnion - oldIntervalUnion
        ///       5) 反投影 (1D Interval → 2D Line), 过滤掉长度 < 0.01mm 的 micro 碎片.
        /// </summary>
        public static void CompareLineLayouts(
            List<Curve> oldCurves,
            List<Curve> newCurves,
            Matrix3d newToOld,
            out List<Line> demolishLines,
            out List<Line> addLines)
        {
            demolishLines = new List<Line>();
            addLines = new List<Line>();
            if (oldCurves == null || newCurves == null) return;

            // 1) 全部曲线 → Line (ARC 折线化 + Polyline 顶点 split + Z 清零)
            var oldLines = ExplodeAllToLines(oldCurves);
            var newLines = ExplodeAllToLines(newCurves);

            // 2) newLines TransformBy → 老坐标系
            foreach (var l in newLines)
            {
                if (l == null || l.IsDisposed) continue;
                try { l.TransformBy(newToOld); }
                catch { /* skip */ }
            }

            // 3) 共线分组 → tracks
            var tracks = BuildAllTracks(oldLines, newLines);

            // 4) 每条 track 上 1D 区间相减
            foreach (var track in tracks)
            {
                // OVERKILL: 区间合并 (画重了 LINE / 微小缝隙 → 合并)
                var cleanOld = UnionIntervals(track.OldIntervals);
                var cleanNew = UnionIntervals(track.NewIntervals);
                if (cleanOld.Count == 0 && cleanNew.Count == 0) continue;

                // 1D 差集
                var dem = SubtractIntervals(cleanOld, cleanNew);  // 拆 = 老 − 新
                var add = SubtractIntervals(cleanNew, cleanOld);  // 建 = 新 − 老

                // 反投影回 2D Line
                foreach (var iv in dem)
                {
                    if (iv.Length < 1.0) continue;  // <1mm 一律视为碎片噪声
                    demolishLines.Add(IntervalToLine(track, iv));
                }
                foreach (var iv in add)
                {
                    if (iv.Length < 1.0) continue;
                    addLines.Add(IntervalToLine(track, iv));
                }
            }
        }

        // ============ ⭐ v21 新: 全部曲线 → 纯 Line (含 ARC 折线化, Polyline split, Z 清零) ============
        private static List<Line> ExplodeAllToLines(IEnumerable<Curve> curves)
        {
            var result = new List<Line>();
            if (curves == null) return result;
            foreach (var c in curves)
            {
                if (c == null || c.IsDisposed) continue;
                if (c is Line ln)
                {
                    result.Add(new Line(FlattenZ(ln.StartPoint), FlattenZ(ln.EndPoint)));
                }
                else if (c is Arc arc)
                {
                    // Arc 折线化: 按 ArcStepDeg / 总扫角 切分
                    var arcLines = ArcToLines(arc);
                    result.AddRange(arcLines);
                }
                else if (c is Polyline pl)
                {
                    var polyLines = PolylineToLines(pl);
                    result.AddRange(polyLines);
                }
                else if (c is Circle cir)
                {
                    // 业主几乎不用, 折线化为圆周长按 ArcStepDeg 步长切的 Line list
                    var radius = cir.Radius;
                    var center = FlattenZ(cir.Center);
                    double sweep = 2 * Math.PI;
                    double stepDeg = ArcStepDeg;
                    double stepRad = stepDeg * Math.PI / 180.0;
                    int steps = Math.Max(8, (int)Math.Ceiling(sweep / stepRad));
                    Point3d prevP = center + new Vector3d(radius, 0, 0);
                    for (int i = 1; i <= steps; i++)
                    {
                        double ang = (i / (double)steps) * 2 * Math.PI;
                        Point3d p = center + new Vector3d(radius * Math.Cos(ang), radius * Math.Sin(ang), 0);
                        result.Add(new Line(prevP, p));
                        prevP = p;
                    }
                }
                else
                {
                    // 其它类型 (Spline/...): 暂时忽略 (业主几乎不用). 想支持可在此加 explode.
                }
            }
            return result;
        }

        private static List<Line> ArcToLines(Arc arc)
        {
            // AutoCAD Arc in xy plane, sweep = EndAngle − StartAngle (ccw 正向). 跨 0 rad 补 2π.
            double sweep = arc.EndAngle - arc.StartAngle;
            if (sweep < 0) sweep += 2 * Math.PI;
            // Arc.Step 步进数 ≈ sweep / stepRad, 至少 4 段避免退化
            double stepRad = ArcStepDeg * Math.PI / 180.0;
            int steps = Math.Max(4, (int)Math.Ceiling(sweep / stepRad));
            var center = FlattenZ(arc.Center);
            double r = arc.Radius;
            Point3d prevP = CenterPointOnArc(arc);
            var lines = new List<Line>(steps);
            for (int i = 1; i <= steps; i++)
            {
                double ang = arc.StartAngle + sweep * (i / (double)steps);
                Point3d p = center + new Vector3d(r * Math.Cos(ang), r * Math.Sin(ang), 0);
                lines.Add(new Line(prevP, p));
                prevP = p;
            }
            return lines;
        }

        private static Point3d CenterPointOnArc(Arc arc)
        {
            var center = FlattenZ(arc.Center);
            double ang = arc.StartAngle;
            return center + new Vector3d(arc.Radius * Math.Cos(ang), arc.Radius * Math.Sin(ang), 0);
        }

        private static List<Line> PolylineToLines(Polyline pl)
        {
            var lines = new List<Line>();
            int n = pl.NumberOfVertices;
            if (n < 2) return lines;
            bool closed = pl.Closed;
            int segCount = closed ? n : n - 1;
            // Polyline vertex i 的坐标 (2D, Z=0)
            Point3d[] pts = new Point3d[n];
            for (int i = 0; i < n; i++)
            {
                var p2 = pl.GetPoint2dAt(i);
                pts[i] = new Point3d(p2.X, p2.Y, 0);
            }
            for (int i = 0; i < segCount; i++)
            {
                int next = (i + 1) % n;
                // v21.1: 读 bulge 处理弧段. |bulge| < 1e-6 当直线段.
                double bulge = pl.GetBulgeAt(i);
                if (Math.Abs(bulge) < 1e-6)
                {
                    lines.Add(new Line(pts[i], pts[next]));
                }
                else
                {
                    // 弧段 - 算出 center / radius / sweep 后折线化
                    Arc arc = BulgeSegmentToArc(pts[i], pts[next], bulge);
                    if (arc != null) lines.AddRange(ArcToLines(arc));
                    else lines.Add(new Line(pts[i], pts[next]));  // fallback 退化
                }
            }
            return lines;
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
        private static List<Track> BuildAllTracks(List<Line> oldLines, List<Line> newLines)
        {
            var tracks = new List<Track>();
            // 第一遍: oldLines 都进来建 track, 同时记录 intervals
            foreach (var l in oldLines)
            {
                if (l == null || l.Length < 1e-6) continue;
                var track = FindOrCreateTrack(tracks, l);
                if (track != null) AddInterval(track, l, isOld: true);
            }
            // 第二遍: newLines 重新遍历 (track list 已有 old 决定的结构, 看看能否 match)
            foreach (var l in newLines)
            {
                if (l == null || l.Length < 1e-6) continue;
                var track = FindOrCreateTrack(tracks, l);
                if (track != null) AddInterval(track, l, isOld: false);
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

        private static void AddInterval(Track track, Line line, bool isOld)
        {
            // 把 line 两端点投影到 track 的 1D t 坐标 (t = (pt - origin) . dot(dir))
            double t0 = (line.StartPoint - track.Origin).DotProduct(track.Dir);
            double t1 = (line.EndPoint - track.Origin).DotProduct(track.Dir);
            // snap 到 0.01mm 抗 浮点碎片
            t0 = Math.Round(t0, TSnapDigits);
            t1 = Math.Round(t1, TSnapDigits);
            var iv = new Interval(t0, t1);
            if (iv.IsDegenerate) return;
            if (isOld) track.OldIntervals.Add(iv);
            else track.NewIntervals.Add(iv);
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

        public static void SafeDispose(DBObject obj)
        {
            try { if (obj != null && !obj.IsDisposed) obj.Dispose(); }
            catch { /* best-effort */ }
        }
    }
}
