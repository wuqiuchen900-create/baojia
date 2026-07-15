using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace BaoJiaCAD
{
    /// <summary>
    /// CAD 边界检测辅助类
    /// </summary>
    public static class BoundaryHelper
    {
        /// <summary>
        /// 在指定点追踪闭合边界
        /// </summary>
        /// <param name="editor">CAD 编辑器</param>
        /// <param name="point">种子点</param>
        /// <returns>闭合多段线，若失败返回 null</returns>
        public static Polyline TraceBoundary(Editor editor, Point3d point)
        {
            DBObjectCollection result = null;
            try
            {
                // 使用 TraceBoundary 模拟 BO 命令
                result = editor.TraceBoundary(point, true);
                if (result != null && result.Count > 0)
                {
                    foreach (DBObject ent in result)
                    {
                        if (ent is Polyline pline && pline.Closed)
                        {
                            // 返回前释放集合中其他对象
                            foreach (DBObject other in result)
                            {
                                if (!ReferenceEquals(other, ent))
                                {
                                    other.Dispose();
                                }
                            }
                            result.Dispose();
                            result = null; // 防止 finally 重复释放
                            return pline;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                editor.WriteMessage($"\n追踪边界异常: {ex.Message}");
            }
            finally
            {
                // 🔧 修复 #1: 逐个释放 DBObjectCollection 内的临时 CAD 实体，防止内存泄漏
                if (result != null)
                {
                    foreach (DBObject obj in result)
                    {
                        obj.Dispose();
                    }
                    result.Dispose();
                }
            }
            return null;
        }

        /// <summary>
        /// 检查框选区域内的墙线是否闭合
        /// </summary>
        public static bool IsRegionClosed(Editor editor, Point3d seedPoint)
        {
            var boundary = TraceBoundary(editor, seedPoint);
            if (boundary != null)
            {
                // 🔧 修复 #2: 释放 TraceBoundary 返回的 Polyline，防止资源泄漏
                boundary.Dispose();
                return true;
            }
            return false;
        }

        /// <summary>
        /// 获取多段线的面积（M2）和周长（M）
        /// </summary>
        public static (double Area, double Perimeter) GetAreaAndPerimeter(Polyline pline)
        {
            // CAD 默认单位为 mm
            // 面积 mm2 -> M2
            double areaM2 = pline.Area / 1_000_000.0;
            // 长度 mm -> M
            double perimeterM = pline.Length / 1000.0;
            return (areaM2, perimeterM);
        }

        // 🔧 v17.2: 点 P 到线段 A-B 的最短距离 (mm). Ray-casting 中 作为 「点是否贴边」判断.
        //   吻 CAD API (GetClosestPointTo) 存在 路径 不 稳。能 在 墙 净.
        public static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
        {
            var ab = new Vector2d(b.X - a.X, b.Y - a.Y);
            double abLen2 = ab.X * ab.X + ab.Y * ab.Y;
            if (abLen2 < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            var ap = new Vector2d(p.X - a.X, p.Y - a.Y);
            double t = (ap.X * ab.X + ap.Y * ab.Y) / abLen2;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            double fx = a.X + t * ab.X;
            double fy = a.Y + t * ab.Y;
            double dx = p.X - fx, dy = p.Y - fy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        // 🔧 v17.2: 点 P 是否在 polyline 闭口 内 (带容差). 用 于 窗线中点 归属 房间 判断.
        //   - 纯 ray-casting 默认 在 多边形 边 上的 点 会 因 浮点 误差 判 OUTSIDE — 贴BO边线 窗线 会被 漏判.
        //   - 容差 tolMM 吃 进 这些 贴边 点. 默认 50mm 够 中小 住宅 房间. 太 大 会 误吃 邻房 闸 户墙 内 25mm 范 点。
        //   - 调用 Caller: WindowAreaDetector 主 循环. WallBoxLength m17.8 中 不 本 helper (其 为 涂料 表面 点对多边形 检测)
        //   返回 true仅作供 、 GetClosestPointTo() 起同途, 调用 供 安全。
        public static bool IsPointInPolygonWithTolerance(Point2d pt, Polyline polyline, double tolMM = 50.0)
        {
            if (polyline == null || polyline.IsDisposed) return false;
            int n = polyline.NumberOfVertices;
            if (n < 3) return false;
            // 第一轮: 检查 点 是否 贴 polyline 的 某个 segment 边 (≤ tolMM). 贴边  直接 返 true.
            for (int i = 0; i < n; i++)
            {
                var a = polyline.GetPoint2dAt(i);
                var b = polyline.GetPoint2dAt((i + 1) % n);
                if (DistancePointToSegment(pt, a, b) <= tolMM) return true;
            }
            // 第二轮: 标准 ray-casting (从 pt 向 +X 射线 跨过 多少 边).
            bool inside = false;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = polyline.GetPoint2dAt(i);
                var pj = polyline.GetPoint2dAt(j);
                bool intersect = ((pi.Y > pt.Y) != (pj.Y > pt.Y))
                    && (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) / (pj.Y - pi.Y) + pi.X);
                if (intersect) inside = !inside;
            }
            return inside;
        }
    }
}
