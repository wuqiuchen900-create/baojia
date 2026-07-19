using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

// CQ 单独 assembly-level 注册, 与 BaoJiaCAD.Commands (BJ) 共用同一 dll 各走各的命令表.
[assembly: CommandClass(typeof(BaoJiaCAD.CQ.CQCommands))]

namespace BaoJiaCAD.CQ
{
    /// <summary>
    /// CQ 拆墙对比命令入口 — 与 BJ 完全独立.
    /// v22: 跳跃面域/闭合几何, 直接走 1D 共线布尔差集 LINE diff.
    ///   1) 框选 / 锚点流程保留 v21 不变.
    ///   2) 主算法: 1D Track + ObjectId 溯源, 输出 DiffSegment (2D Line + SourceId + SourceIv).
    ///   3) 拆 / 建 红绿框画到 DWG (v21 行为).
    ///   4) ⭐ v22 new: ReplaceOriginalLines 在原图上 抹除 demolish 子段, 保留 remaining 部分.
    /// </summary>
    public class CQCommands
    {
        [CommandMethod("CQ")]
        public void ChaiQiang()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            var editor = doc.Editor;
            var db = doc.Database;

            // 集中托管所有 clone / 中间 Curve / 临时实体. finally 中统一 Dispose.
            // 注意: 成功 AppendEntity 写入 DWG 的 Line 不能归 trash (会触发 ePermanentlyErased).
            var trash = new List<DBObject>();
            List<CQHelper.DiffSegment> demolishSegments = null, addSegments = null;

            try
            {
                editor.WriteMessage("\n========== CQ 拆墙对比 v22 (1D Line Diff + 原图抹除) ==========");

                // 1) 框选 原户型墙线
                var selOld = CQHelper.AskUserSel(editor, "原户型墙线 (左)");
                if (selOld == null || selOld.Count == 0)
                {
                    editor.WriteMessage("\n[CQ] 原户型选择为空, 已取消.");
                    return;
                }

                // 2) 原户型共用锚点
                var baseOld = CQHelper.AskUserBasepoint(editor, "原户型共用锚点");
                if (baseOld == null) { editor.WriteMessage("\n[CQ] 已取消."); return; }

                // 3) 框选 新户型墙线
                var selNew = CQHelper.AskUserSel(editor, "新户型墙线 (右)");
                if (selNew == null || selNew.Count == 0)
                {
                    editor.WriteMessage("\n[CQ] 新户型选择为空, 已取消.");
                    return;
                }

                // 4) 新户型共用锚点
                var baseNew = CQHelper.AskUserBasepoint(editor, "新户型共用锚点");
                if (baseNew == null) { editor.WriteMessage("\n[CQ] 已取消."); return; }

                // 5) 共用锚点距预警 (不阻断)
                if (CQHelper.NeedsBasepointDistanceWarn(baseOld.Value, baseNew.Value))
                {
                    editor.WriteMessage(string.Format(
                        "\n[⚠ CQ] 两共用锚点距 {0:F0}mm > 10000mm, 请确认是同一墙角.",
                        baseOld.Value.DistanceTo(baseNew.Value)));
                }

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    // 6) 提取 + Clone selections (curveToSource 源溯源映射)
                    var oldRaw = CQHelper.ExtractAndCloneCurves(tr, selOld, editor, trash, out var oldCurveToSource);
                    var newRaw = CQHelper.ExtractAndCloneCurves(tr, selNew, editor, trash, out var newCurveToSource);
                    editor.WriteMessage(string.Format(
                        "\n[CQ] v22 提取: 原户型 {0} 条 / 新户型 {1} 条 (filter=LINE+ARC+LWPOLYLINE+POLYLINE).",
                        oldRaw.Count, newRaw.Count));

                    // 合并源溯源映射 (key=Curve clone, value=原 ObjectId)
                    var sharedCurveToSource = new Dictionary<Curve, ObjectId>(oldCurveToSource);
                    foreach (var kv in newCurveToSource) sharedCurveToSource[kv.Key] = kv.Value;

                    // 7) 跳 JoinConnectedCurves / 跳 Region — 直接走主入口

                    // 8/9) 主算法: 1D 共线布尔差集 + ObjectId 溯源.
                    //   demolishSegments 在 old coords (写拆墙红层 + ReplaceOriginalLines 抹原图)
                    //   addSegments      在 old coords (后续 un-shift 回新户型原位)
                    var matNewToOld = CQHelper.DisplacementMatrix(
                        CQHelper.ComputeLayoutOffset(baseOld.Value, baseNew.Value));
                    try
                    {
                        CQHelper.CompareLineLayouts(
                            oldRaw, newRaw, sharedCurveToSource, matNewToOld,
                            out demolishSegments, out addSegments);
                    }
                    catch (System.Exception ex)
                    {
                        editor.WriteMessage("\n[CQ] ! 1D Line Diff 异常: " + ex.Message);
                        if (demolishSegments != null)
                            foreach (var s in demolishSegments) if (s != null && s.Line != null) trash.Add(s.Line);
                        if (addSegments != null)
                            foreach (var s in addSegments) if (s != null && s.Line != null) trash.Add(s.Line);
                        return;
                    }

                    if (demolishSegments == null || addSegments == null
                        || (demolishSegments.Count == 0 && addSegments.Count == 0))
                    {
                        editor.WriteMessage(
                            "\n[CQ] ! 1D Line Diff 无结果 (两侧几何完全一致或选为空). 业主检查: 锚点是否同墙角 / 选择是否正确.");
                        return;
                    }
                    editor.WriteMessage(string.Format(
                        "\n[CQ] v22 Line Diff: 拆墙 {0} 段 / 砌墙 {1} 段.",
                        demolishSegments.Count, addSegments.Count));

                    // 10) addLines un-shift 回 新户型坐标系 (业主左右并排场景)
                    var matOldToNew = matNewToOld.Inverse();
                    foreach (var s in addSegments)
                    {
                        if (s == null || s.Line == null || s.Line.IsDisposed) continue;
                        try { s.Line.TransformBy(matOldToNew); }
                        catch { CQHelper.SafeDispose(s.Line); }
                    }

                    // 11) 统计 (按长度累加)
                    double delLenM = TotalLengthMM(demolishSegments) / 1000.0;
                    double addLenM = TotalLengthMM(addSegments) / 1000.0;
                    editor.WriteMessage("\n========== CQ 拆墙对比结果 ==========");
                    editor.WriteMessage(string.Format("\n  拆除墙体总长: {0:F2} m ({1} 段)", delLenM, demolishSegments.Count));
                    editor.WriteMessage(string.Format("\n  新建墙体总长: {0:F2} m ({1} 段)", addLenM, addSegments.Count));
                    editor.WriteMessage(string.Format("\n  净变化:   {0:+0.00;-0.00;0.00} m (新建 − 拆除)", addLenM - delLenM));
                    editor.WriteMessage("\n=====================================");

                    // 12) 创建永久图层 + 检查 linotype (v22.2: 拆/建 overlay 使用 ACAD_ISO03W100 虚线 + scale 8)
                    CQHelper.EnsureLayer(CQHelper.LayerDemolish, db, tr, CQHelper.LayerColor(CQHelper.LayerDemolish));
                    CQHelper.EnsureLayer(CQHelper.LayerAdd, db, tr, CQHelper.LayerColor(CQHelper.LayerAdd));
                    CQHelper.CheckLinetypeExists("ACAD_ISO03W100", db, tr, editor);  // 仅检查, AutoCAD 自己 fallback

                    // 13) 写入 DWG 永久实体 (demolish红 + add绿). 成功的不归 trash, 失败归 trash.
                    var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    var demolishLineList = demolishSegments
                        .Where(s => s != null && s.Line != null && !s.Line.IsDisposed)
                        .Select(s => s.Line).ToList();
                    var addLineList = addSegments
                        .Where(s => s != null && s.Line != null && !s.Line.IsDisposed)
                        .Select(s => s.Line).ToList();
                    int delSeg = CQHelper.WriteDiffLinesToLayer(demolishLineList, CQHelper.LayerDemolish, tr, btr, trash);
                    int addSeg = CQHelper.WriteDiffLinesToLayer(addLineList, CQHelper.LayerAdd, tr, btr, trash);
                    editor.WriteMessage(string.Format(
                        "\n[CQ] 写 DWG 红绿框: 拆墙 {0} 段 -> 图层 \"{1}\" (原户型), 砌墙 {2} 段 -> 图层 \"{3}\" (新户型原位).",
                        delSeg, CQHelper.LayerDemolish, addSeg, CQHelper.LayerAdd));

                    // 14) ⭐ v22 new: 抹除原图 demolish sub-intervals, 保留剩余.
                    //     对每条 demolish DiffSegment (具 SourceId) → 反算保留 sub-interval → Erase 原 + 重建保留.
                    int replaceDemCount = CQHelper.ReplaceOriginalLines(demolishSegments, tr, btr, editor, trash);

                    // 14b) ⭐ v22.1 new: 对新户型也对称抹除 — '绿覆盖区 底图 抹除, kept (与原户型共有白墙) 保留'.
                    //     ReplaceOriginalLines 实际是 泛型: 任一组 带 SourceId 的 DiffSegment 都可.
                    //     addSegments 都 pointing 到 新户型 source — 抹除 add区 + 重建 kept区.
                    int replaceAddCount = CQHelper.ReplaceOriginalLines(addSegments, tr, btr, editor, trash);

                    editor.WriteMessage(string.Format(
                        "\n[CQ] 原图 处理 完成: 旧户型 “拆区 抹除 + kept 重建” {0} 个; 新户型 “add 区 抹除 + kept 重建” {1} 个.",
                        replaceDemCount, replaceAddCount));

                    tr.Commit();
                    editor.WriteMessage("\n========== CQ 拆墙对比 v22 完成 ==========");
                }
            }
            catch (System.Exception ex)
            {
                string surfaceMessage = !string.IsNullOrEmpty(ex.Message)
                    && ex.Message.Length >= 20
                    && !ex.Message.StartsWith("Exception of type")
                    ? ex.Message
                    : (ex.InnerException?.Message ?? ex.Message);
                editor.WriteMessage("\n========== [CQ ERROR-DIAG] ==========");
                editor.WriteMessage("\n[ERROR-DIAG] ExceptionType: " + ex.GetType().FullName);
                editor.WriteMessage("\n[ERROR-DIAG] Message: " + (ex.Message ?? "<null>"));
                editor.WriteMessage("\n[ERROR-DIAG] Stack:");
                editor.WriteMessage("\n" + (ex.StackTrace ?? "<null>"));
                editor.WriteMessage("\n=====================================");
                System.Windows.Forms.MessageBox.Show(
                    "CQ 拆墙对比失败: " + surfaceMessage +
                    "\n\n请检查:" +
                    "\n1. 两户型共用锚点是否选同一墙角" +
                    "\n2. 框选是否仅包含 LINE/ARC/LWPOLYLINE/POLYLINE (其他类型过滤)" +
                    "\n3. DWG 是否只读或被外部锁定",
                    "CQ 失败",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Error);
            }
            finally
            {
                foreach (var obj in trash)
                {
                    CQHelper.SafeDispose(obj);
                }
            }
        }

        private static double TotalLengthMM(IEnumerable<CQHelper.DiffSegment> segs)
        {
            double s = 0;
            foreach (var seg in segs)
            {
                if (seg == null || seg.Line == null || seg.Line.IsDisposed) continue;
                s += seg.Line.Length;
            }
            return s;
        }
    }
}
