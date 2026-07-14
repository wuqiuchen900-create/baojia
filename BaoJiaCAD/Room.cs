using System;
using System.Collections.Generic;

namespace BaoJiaCAD
{
    /// <summary>
    /// 识别出的房间信息
    /// </summary>
    public class Room
    {
        /// <summary>房间名称，来自 CAD 中的文字</summary>
        public string Name { get; set; }

        /// <summary>房间类型：客厅、厨房、卧室等</summary>
        public string RoomType { get; set; }

        /// <summary>楼层（如 "一楼"/"二楼"/""），用于复式多层匹配</summary>
        public string FloorLevel { get; set; } = "";

        /// <summary>地面面积，单位 M2</summary>
        public double FloorArea { get; set; }

        /// <summary>房间边界周长，单位 M</summary>
        public double Perimeter { get; set; }

        /// <summary>墙面计算高度，单位 mm</summary>
        public double WallHeight { get; set; } = 2800.0;

        /// <summary>墙面工程量 = 地面面积 + 周长 × 高度</summary>
        public double WallArea => FloorArea + Perimeter * (WallHeight / 1000.0);

        /// <summary>该房间对应的报价项目</summary>
        public List<QuoteItem> Items { get; set; } = new List<QuoteItem>();

        /// <summary>外花园是否卷材防水 (互斥项)。null=未被询问; true=卷材走 8/9 项; false=走 10/11/12 项</summary>
        public bool? IsWaterproofedRoll { get; set; } = null;

        /// <summary>外花园特殊公式子项名 → 数量（仅外花园且 IsWaterproofedRoll 有值时填入）</summary>
        public Dictionary<string, double> OutdoorGardenFormulas { get; set; }
            = new Dictionary<string, double>();

        /// <summary>房间内通用公式（卫生间/厨房贴瓷片、防水保护层、背胶 等子项名 → 数量）</summary>
        public Dictionary<string, double> ItemFormulas { get; set; }
            = new Dictionary<string, double>();

        /// <summary>🔧 v16: 房间 BO 边界的 内存 Polyline 克隆 (WindowBoxDetector 用来精定位窗户→墙段 映射).
        ///   RoomDetector.DetectRooms 创建; WindowBoxDetector 用完; Commands.BaoJia 终态 Dispose 清理.
        ///   不依赖 AutoCAD temp entities 生命周期 (后者随 tr 而死), 不 Append 到 BlockTable, 完全内存对象.</summary>
        public Autodesk.AutoCAD.DatabaseServices.Polyline BoundaryPolyline { get; set; }

        /// <summary>🔧 v16: 窗帘盒总长 (米) — 该房间所有「覆盖窗户的墙段」长度之和.
        ///   默认 0.0; WindowBoxDetector.DetectCurtainBoxLengths 后 填入. 浮空窗户不计入.</summary>
        public double CurtainBoxLength { get; set; } = 0.0;
    }

    /// <summary>
    /// 报价项目
    /// </summary>
    public class QuoteItem
    {
        public string Name { get; set; }
        public string Unit { get; set; }
        public double Quantity { get; set; }
        public double MaterialPrice { get; set; }
        public double MaterialTotal => Quantity * MaterialPrice;
        public double LaborPrice { get; set; }
        public double LaborTotal => Quantity * LaborPrice;
        public bool IsSummaryItem { get; set; }
        public string Description { get; set; }
    }
}
