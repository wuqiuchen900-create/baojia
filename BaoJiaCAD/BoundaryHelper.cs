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
    }
}
