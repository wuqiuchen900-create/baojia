using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.EditorInput;

namespace BaoJiaCAD
{
    /// <summary>
    /// 走法 C：6 大类归纳 + 显示面板。
    /// 把所有识别出的房间按 6 类（客餐厅 / 卫生间 / 卧室 / 厨房 / 阳台 / 外花园）汇总展示。
    /// 注意：本模块不修改 Excel 模板 (dizhuan / fushi) 内部 group 结构，
    /// 仅作为“画家认知层”的面板 (启发示意)。
    /// Excel 模板填入仍走 ExcelExporter 原逻辑（平铺 group）。
    /// </summary>
    public static class CategoryPanel
    {
        /// <summary>🔧 v11: 8 大类 ——按用户口径顺序输出 (1客餐厅 2厨房类 3公卫[=卫生间] 4主卧 5主卫 6卧室类 + 阳台/外花园 append).</summary>
        public static readonly string[] SixCats =
            { "客餐厅", "厨房", "卫生间", "主卧", "主卫", "卧室", "阳台", "外花园" };

        private static readonly Regex FloorPrefixRegex = new Regex(
            @"^\s*(1F|2F|3F|4F|5F|一楼|二楼|三楼|四楼|五楼|一层|二层|三层|首层)\s*",
            RegexOptions.Compiled);

        /// <summary>
        /// 核心词（如 "1F主卧" → "主卧"）。
        /// </summary>
        public static string StripFloorPrefix(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return name ?? string.Empty;
            return FloorPrefixRegex.Replace(name.Trim(), string.Empty).Trim();
        }

        /// <summary>
        /// 把房间名映射到 6 大类。
        /// 优先级：外花园 > 独立阳台(扩到生活阳台/南阳台等) > 客餐厅(含"客餐厅"复合空间) > 卫生间 > 厨房 > 卧室
        /// </summary>
        public static string MapToSixCategory(string rawName, QuoteConfig config)
        {
            if (string.IsNullOrWhiteSpace(rawName)) return "其他";

            string core = StripFloorPrefix(rawName);

            if (config?.RoomTypeMaps != null)
            {
                foreach (var map in config.RoomTypeMaps)
                {
                    if (map.Keywords != null && map.Keywords.Any(k => core.Contains(k)))
                    {
                        // 只接受 6 大类 (SixCats 内的值), 防止 legacy config 走偏 (如旧版 书房 单独条目)
                        if (Array.IndexOf(SixCats, map.RoomType) >= 0)
                            return map.RoomType;
                        return "其他";
                    }
                }
            }
            return "其他";
        }

        /// <summary>
        /// 以 6 大类口径输出汇总面板到命令列。
        /// 包含地面/墙顶面积合计（墙顶 = 地面 + 周长 × 高度）——后续可直接作案面计算入口。
        /// </summary>
        public static void ShowSixCategories(Editor editor, List<Room> rooms, double wallHeight, QuoteConfig config)
        {
            if (editor == null) throw new ArgumentNullException(nameof(editor));

            editor.WriteMessage("\n=== 🔧v11 房间 8 大类归纳面板 ===");
            if (rooms == null || rooms.Count == 0)
            {
                editor.WriteMessage("\n[无房间数据]");
                return;
            }

            var bucketed = rooms
                .GroupBy(r => MapToSixCategory(r.Name, config))
                .ToDictionary(g => g.Key, g => g.OrderBy(r => r.FloorLevel + "|" + r.Name).ToList());

            foreach (var cat in SixCats)
            {
                var rs = bucketed.TryGetValue(cat, out var list) ? list : new List<Room>();
                double floorTotal = rs.Sum(r => r.FloorArea);
                double perimTotal = rs.Sum(r => r.Perimeter);
                // 🔧 v19: 复用 Room.WallArea getter (per-room wallHeight 已在 RoomDetector 设). 单一公式源头 — v17.x 扣减 改动 自动跟.
                double wallTotal = rs.Sum(r => r.WallArea);

                editor.WriteMessage($"\n[{cat}]  {rs.Count} 个房间");
                editor.WriteMessage($"\n        地面合计: {floorTotal:F2} ㎡");
                editor.WriteMessage($"\n        墙顶合计: {wallTotal:F2} ㎡");

                // 复式模式：按楼层子组输出，便于 user 核对层间分布
                if (rs.GroupBy(r => r.FloorLevel).Count() > 1)
                {
                    foreach (var fg in rs.GroupBy(r => string.IsNullOrEmpty(r.FloorLevel) ? "未指定" : r.FloorLevel)
                                          .OrderBy(g => g.Key))
                    {
                        double fG = fg.Sum(r => r.FloorArea);
                        // 🔧 v19: 各房 r.WallArea 直接 拉 — 同 fg 同一 floor 同 wallHeight 一般 自然 一致.
                        double wG = fg.Sum(r => r.WallArea);
                        editor.WriteMessage($"\n    [{fg.Key}] {fg.Count()} 个房间  地面 {fG:F2} ㎡  墙顶 {wG:F2} ㎡");
                    }
                }

                foreach (var r in rs.OrderBy(r => r.FloorLevel + "|" + r.Name))
                {
                    // 🔧 v19: 直接 读 r.WallArea (per-room wallHeight getter, 复式 同 floor 同 wallHeight 自然一致).
                    double wall = r.WallArea;
                    editor.WriteMessage(
                        $"\n    - [{r.FloorLevel}] {r.Name} (地面 {r.FloorArea:F2} ㎡, 墙顶 {wall:F2} ㎡)");
                }
            }

            if (bucketed.TryGetValue("其他", out var others) && others.Count > 0)
            {
                editor.WriteMessage($"\n[其他]  {others.Count} 个房间 未匹配6大类 (需人工检查):");
                foreach (var r in others)
                {
                    editor.WriteMessage($"\n    - [{r.FloorLevel}] {r.Name}");
                }
            }

            editor.WriteMessage("\n=== 面板结束 ===\n");
        }

        /// <summary>
        /// 是为外花园房间（按用户口径：前/后/XX花园、露台、XX露台、入户花园）。
        /// </summary>
        public static bool IsOuterGarden(string roomName)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return false;
            string core = StripFloorPrefix(roomName);
            return core.Contains("花园") || core.Contains("露台");
        }

        /// <summary>
        /// 外花园卷材防水互斥面板：
        /// - 多个外花园时先问 统一答 (Y) 还是 逐个问 (P)
        /// - Y 按 8/9 项公式 (地面+周长×0.8米高)； N 按 10/11/12 (10/11=地面面积; 12=周长×0.3米高)
        /// - ESC 返回 false 让 caller 中止 BJ
        /// 同时返回 bool 表示 处理是否完成 (ESC 中途取消 → false)
        /// </summary>
        public static bool AskOuterGardenWaterproof(Editor editor, List<Room> rooms, QuoteConfig config)
        {
            if (editor == null || rooms == null || rooms.Count == 0) return true;
            var defs = config?.BathroomKitchenDefaults;
            double rollHeight = defs?.OutdoorGardenRollHeight ?? 0.8;
            double nonRollHeight = defs?.OutdoorGardenNonRollHeight ?? 0.6;

            var outerRooms = rooms
                .Where(r => r.IsWaterproofedRoll == null && IsOuterGarden(r.Name))
                .ToList();
            if (outerRooms.Count == 0) return true;

            editor.WriteMessage($"\n[外花园互斥面板] 检测到 {outerRooms.Count} 个外花园房间：");
            foreach (var r in outerRooms)
                editor.WriteMessage($"\n  - [{r.FloorLevel}] {r.Name}  地面 {r.FloorArea:F2}㎡  周长 {r.Perimeter:F2}m");

            bool useGlobal = true;
            if (outerRooms.Count > 1)
            {
                editor.WriteMessage("\n\n多个外花园？[Y统一规则 / P逐个询问] <Y>：");
                var prefOpt = new PromptStringOptions("\n[Y统一 / P逐个] <Y>: ")
                {
                    AllowSpaces = false,
                    DefaultValue = "Y"
                };
                var prefRes = editor.GetString(prefOpt);
                if (prefRes.Status != PromptStatus.OK) return false;
                var pref = (prefRes.StringResult ?? "").Trim().ToUpperInvariant();
                useGlobal = !(pref == "P" || pref == "PER");
            }

            if (useGlobal)
            {
                bool? ans = AskRollForRoom(editor, outerRooms[0]);
                if (ans == null) return false;
                bool roll = ans.Value;
                foreach (var r in outerRooms)
                    WriteOutdoorGardenFormulas(r, roll, rollHeight, nonRollHeight, editor);
            }
            else
            {
                foreach (var r in outerRooms)
                {
                    bool? ans = AskRollForRoom(editor, r);
                    if (ans == null) return false;
                    WriteOutdoorGardenFormulas(r, ans.Value, rollHeight, nonRollHeight, editor);
                }
            }
            return true;
        }

        // 默认门窗洞口减平米 (现在走 config.BathroomKitchenDefaults, 不再硬编码)
        // 贴瓷片/瓷砖背胶 高 / 防水高 都走 config.BathroomKitchenDefaults.TileHeight / WaterproofHeight

        /// <summary>
        /// 卫生间/厨房/阳台默认公式 (免询问)。参数 defs 由 config.BathroomKitchenDefaults 提供,
        /// 可以由 config.json 调整, 不需重编。
        /// - 卫生间: 地面防水/地面防水保护层 = 地面面积; 墙面防水层 = 周长×defs.WaterproofHeight(2.0m);
        ///          墙面贴瓷片 = 周长×defs.TileHeight - defs.DefaultDoorDeduct - defs.DefaultWindowDeduct;
        ///          刷背胶 = 同贴瓷片
        /// - 厨房: 地面防水/地面防水保护层 = 地面面积; 墙面防水层 = 周长×defs.KitchenWaterproofHeight(0.6m);
        ///         墙面贴瓷片 = 周长×defs.TileHeight; 瓷砖背胶 = 同贴瓷片
        /// - 阳台: 地面防水/地面防水保护层 = 地面面积; 墙面防水层 = 周长×defs.BalconyWaterproofHeight(0.6m)
        /// 写入 Room.ItemFormulas
        /// </summary>
        public static void AskBathroomKitchenFormulas(Editor editor, List<Room> rooms, QuoteConfig config)
        {
            if (editor == null || rooms == null || rooms.Count == 0 || config == null) return;
            var defs = config.BathroomKitchenDefaults;
            if (defs == null) return;
            int touched = 0;
            foreach (var r in rooms)
            {
                string cat = MapToSixCategory(r.Name, config);
                if (cat != "卫生间" && cat != "主卫" && cat != "厨房" && cat != "阳台") continue;
                if (r.ItemFormulas == null)
                    r.ItemFormulas = new Dictionary<string, double>();
                else
                    r.ItemFormulas.Clear();

                double perim = r.Perimeter;
                if (cat == "卫生间" || cat == "主卫")
                {
                    r.ItemFormulas["地面防水处理"] = r.FloorArea;
                    r.ItemFormulas["地面防水保护层"] = r.FloorArea;
                    r.ItemFormulas["墙面防水层"] = perim * defs.WaterproofHeight;
                    // 🔧 修复 #6: Math.Max 防止极小房间贴瓷片面积计算为负数
                    double tileQty = Math.Max(0, perim * defs.TileHeight - defs.DefaultDoorDeduct - defs.DefaultWindowDeduct);
                    r.ItemFormulas["墙面贴瓷片"] = tileQty;
                    r.ItemFormulas["墙面瓷砖刷背胶"] = tileQty;
                    double tile = 0;
                    r.ItemFormulas.TryGetValue("墙面贴瓷片", out tile);
                    double wf = r.ItemFormulas["墙面防水层"];
                    editor.WriteMessage($"\n  [{r.FloorLevel}] {r.Name}({cat}): 贴瓷片={tile:F2}㎡, 墙面防水层={wf:F2}㎡");
                    touched++;
                }
                else if (cat == "厨房")
                {
                    r.ItemFormulas["地面防水处理"] = r.FloorArea;
                    r.ItemFormulas["地面防水保护层"] = r.FloorArea;
                    r.ItemFormulas["墙面防水层"] = perim * defs.KitchenWaterproofHeight;
                    double tileQty = Math.Max(0, perim * defs.TileHeight);
                    r.ItemFormulas["墙面贴瓷片"] = tileQty;
                    r.ItemFormulas["墙面瓷砖刷背胶"] = tileQty;
                    double tile = 0;
                    r.ItemFormulas.TryGetValue("墙面贴瓷片", out tile);
                    double wf = r.ItemFormulas["墙面防水层"];
                    editor.WriteMessage($"\n  [{r.FloorLevel}] {r.Name}({cat}): 贴瓷片={tile:F2}㎡, 墙面防水层={wf:F2}㎡");
                    touched++;
                }
                else  // 阳台
                {
                    r.ItemFormulas["地面防水处理"] = r.FloorArea;
                    r.ItemFormulas["地面防水保护层"] = r.FloorArea;
                    r.ItemFormulas["墙面防水层"] = perim * defs.BalconyWaterproofHeight;
                    double wf = r.ItemFormulas["墙面防水层"];
                    editor.WriteMessage($"\n  [{r.FloorLevel}] {r.Name}({cat}): 墙面防水层={wf:F2}㎡");
                    touched++;
                }
            }
            if (touched > 0)
                editor.WriteMessage($"\n[卫生间/厨房/阳台默认公式] 已自动填 {touched} 个房间.\n");
        }



        /// <summary>询问单个房间是否卷材。ESC 返回 null。</summary>
        private static bool? AskRollForRoom(Editor editor, Room room)
        {
            var opt = new PromptStringOptions($"\n[{room.FloorLevel}] {room.Name} 是否卷材防水? (Y/N) <N>: ")
            {
                AllowSpaces = false,
                DefaultValue = "N"
            };
            while (true)
            {
                PromptResult res = editor.GetString(opt);
                if (res.Status != PromptStatus.OK) return null;
                string input = (res.StringResult ?? "").Trim().ToUpperInvariant();
                if (input == "" || input == "N" || input == "NO") return false;
                if (input == "Y" || input == "YES") return true;
                editor.WriteMessage("\n输入无效，请回 Y 或 N。");
            }
        }

        /// <summary>依据选择 写入 公式字典 + IsWaterproofedRoll。</summary>
        private static void WriteOutdoorGardenFormulas(Room r, bool roll, double rollHeight, double nonRollHeight, Editor editor)
        {
            r.IsWaterproofedRoll = roll;
            if (r.OutdoorGardenFormulas == null)
                r.OutdoorGardenFormulas = new Dictionary<string, double>();
            else
                r.OutdoorGardenFormulas.Clear();

            if (roll)
            {
                double qty = r.FloorArea + r.Perimeter * rollHeight;
                r.OutdoorGardenFormulas["地面自粘防水卷材"] = qty;
                r.OutdoorGardenFormulas["卷材防水保护层"] = qty;
            }
            else
            {
                r.OutdoorGardenFormulas["地面防水处理"] = r.FloorArea;
                r.OutdoorGardenFormulas["地面防水保护层"] = r.FloorArea;
                r.OutdoorGardenFormulas["墙面防水层"] = r.Perimeter * nonRollHeight;
            }
            editor.WriteMessage($"\n  → [{r.FloorLevel}] {r.Name} = {(roll ? "卷材(8/9)" : "非卷材(10/11/12)")}");
        }
    }
}
