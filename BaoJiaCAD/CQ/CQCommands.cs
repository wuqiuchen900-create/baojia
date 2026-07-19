using System;
using System.Collections.Generic;
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
    /// v21: 跳跃面域/闭合几何, 直接走 1D 共线布尔差集 LINE diff.
    ///   - 业主画线 -> 视为"无限长轨道" -> 共线分组 -> 1D 区间相减.
    ///   - 拆 = 老轨 - 新轨; 建 = 新轨 - 老轨.
    ///   - 业主左右并排: 拆画原户型位置 (红), 建 un-shift 回新户型原位 (绿).
    /// </summary>
    public class CQCommands
    {
        [CommandMethod("CQ")]
        public void ChaiQiang()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            var editor = doc.Editor;
            var db = doc.Database;

            // 集中托管所有 clone / 中间 Line / 临时实体. finally 中统一 Dispose.
            // 注意: 成功 AppendEntity 写入 DWG 的 Line 不能归 trash (Disposing DWG-owned entity 会触发 ePermanentlyErased).
            var trash = new List<DBObject>();
            List<Line> demolishLines = null, addLines = null;

            try
            {
                editor.WriteMessage("\n========== CQ 拆墙对比 v21 (1D Line Diff) ==========");

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
                    // 6) 提取 + Clone selections (trash 集中管)
                    var oldRaw = CQHelper.ExtractAndCloneCurves(tr, selOld, editor, trash);
                    var newRaw = CQHelper.ExtractAndCloneCurves(tr, selNew, editor, trash);
                    editor.WriteMessage(string.Format(
                        "\n[CQ] v21 提取: 原户型 {0} 条 / 新户型 {1} 条 (图层过滤 = LINE+ARC).",
                        oldRaw.Count, newRaw.Count));

                    // 7) 跳 JoinConnectedCurves / 跳 Region — 直接走主入口

                    // 8/9) 主算法: 1D 共线布尔差集. newCurves 内部用 matNewToOld 平移叠到老坐标系.
                    //   demolishLines 在 old coords (直接写拆墙红层)
                    //   addLines      在 old coords (接下来 un-shift 回新户型原位)
                    //   v21.1 兜底: 若主算法异常抛, demolish/add 已生成 Line 也要归 trash 让 finally Dispose 防泄漏.
                    var matNewToOld = CQHelper.DisplacementMatrix(
                        CQHelper.ComputeLayoutOffset(baseOld.Value, baseNew.Value));
                    try
                    {
                        CQHelper.CompareLineLayouts(
                            oldRaw, newRaw, matNewToOld,
                            out demolishLines, out addLines);
                    }
                    catch (System.Exception ex)
                    {
                        editor.WriteMessage("\n[CQ] ! 1D Line Diff 异常: " + ex.Message);
                        if (demolishLines != null)
                            foreach (var l in demolishLines) trash.Add(l);
                        if (addLines != null)
                            foreach (var l in addLines) trash.Add(l);
                        return;
                    }

                    if (demolishLines == null || addLines == null
                        || (demolishLines.Count == 0 && addLines.Count == 0))
                    {
                        editor.WriteMessage(
                            "\n[CQ] ! 1D Line Diff 无结果 (两侧几何完全一致或选为空). 业主可检查: 锚点是否同墙角 / 选择是否正确.");
                        return;
                    }
                    editor.WriteMessage(string.Format(
                        "\n[CQ] v21 Line Diff: 拆墙 {0} 段 / 砌墙 {1} 段.",
                        demolishLines.Count, addLines.Count));

                    // 10) addLines un-shift 回 新户型坐标系 (业主左右并排场景)
                    var matOldToNew = matNewToOld.Inverse();
                    foreach (var l in addLines)
                    {
                        if (l == null || l.IsDisposed) continue;
                        try { l.TransformBy(matOldToNew); }
                        catch { CQHelper.SafeDispose(l); }
                    }

                    // 11) 统计 (按长度累加)
                    double delLenM = TotalLengthMM(demolishLines) / 1000.0;
                    double addLenM = TotalLengthMM(addLines) / 1000.0;
                    editor.WriteMessage("\n========== CQ 拆墙对比结果 ==========");
                    editor.WriteMessage(string.Format(
                        "\n  拆除墙体总长: {0:F2} m ({1} 段)",
                        delLenM, demolishLines.Count));
                    editor.WriteMessage(string.Format(
                        "\n  新建墙体总长: {0:F2} m ({1} 段)",
                        addLenM, addLines.Count));
                    editor.WriteMessage(string.Format(
                        "\n  净变化:   {0:+0.00;-0.00;0.00} m (新建 − 拆除)",
                        addLenM - delLenM));
                    editor.WriteMessage("\n=====================================");

                    // 12) 创建永久图层
                    CQHelper.EnsureLayer(CQHelper.LayerDemolish, db, tr,
                        CQHelper.LayerColor(CQHelper.LayerDemolish));
                    CQHelper.EnsureLayer(CQHelper.LayerAdd, db, tr,
                        CQHelper.LayerColor(CQHelper.LayerAdd));

                    // 13) 写入 DWG 永久实体. 成功 AppendEntity 的不归 trash (DWG-managed), 失败归 trash.
                    var btr = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
                    int delSeg = CQHelper.WriteDiffLinesToLayer(demolishLines, CQHelper.LayerDemolish, tr, btr, trash);
                    int addSeg = CQHelper.WriteDiffLinesToLayer(addLines, CQHelper.LayerAdd, tr, btr, trash);
                    editor.WriteMessage(string.Format(
                        "\n[CQ] 写 DWG 永久实体: 拆墙(红) {0} 段 -> 图层 \"{1}\" (原户型位置), 砌墙(绿) {2} 段 -> 图层 \"{3}\" (新户型原位).",
                        delSeg, CQHelper.LayerDemolish, addSeg, CQHelper.LayerAdd));

                    tr.Commit();
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
                    "\n2. 框选是否仅包含 LINE/ARC (其他类型已被过滤)" +
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

        private static double TotalLengthMM(IEnumerable<Line> lines)
        {
            double s = 0;
            foreach (var l in lines)
            {
                if (l == null || l.IsDisposed) continue;
                s += l.Length;
            }
            return s;
        }
    }
}
