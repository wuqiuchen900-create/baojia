using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace BaoJiaCAD
{
    /// <summary>
    /// 插件整体配置
    /// </summary>
    public class QuoteConfig
    {
        /// <summary>公司名称</summary>
        public string CompanyName { get; set; } = "广东帝豪装饰集团巴中分公司（工程预算单）";

        /// <summary>公司地址</summary>
        public string CompanyAddress { get; set; } = "公司地址:巴中市黄家沟居然之家四楼2-4-009号    电话:0827-3888300";

        /// <summary>默认墙面高度，单位 mm</summary>
        public double DefaultWallHeight { get; set; } = 2800.0;

        /// <summary>房间类型关键字映射</summary>
        public List<RoomTypeMap> RoomTypeMaps { get; set; } = new List<RoomTypeMap>();

        /// <summary>报价项目配置</summary>
        public List<QuoteItemConfig> QuoteItems { get; set; } = new List<QuoteItemConfig>();

        /// <summary>模板配置（面板功能）：模板目录、当前模板、关键词匹配规则等</summary>
        public TemplateSettingsConfig TemplateSettings { get; set; } = new TemplateSettingsConfig();

        /// <summary>厨卫 / 特殊房间默认测算参数 (门窗洞减、贴瓷片高、防水高等) 走 config.json 可调</summary>
        public BathroomKitchenDefaults BathroomKitchenDefaults { get; set; } = new BathroomKitchenDefaults();

        /// <summary>
        /// 本次报价运行时用户选的瓷砖规格 (由 Commands 从面板写入, ExcelExporter 读取; 不持久化).
        /// 键编码 (ExcelExporter.FillRoomData 中按以下优先级回退查找 k1 → k2 → k3):
        ///   k1 (主路径) — v6 多楼层: "{FloorLevel}|{RoomType}" (e.g. "一楼|客餐厅"). 同 层同房 间 没填 → 走 k2.
        ///   k2 (全局兑底) — "|{RoomType}" (e.g. "|客餐厅"). 没填 → 走 k3.
        ///   k3 (向后兼容) — v5 老版单楼层面板 只有 {RoomType} (e.g. "客餐厅"). 仍生效.
        /// 值规则: panel.GetTileSpecSelections() 输出的是 spec.Value (e.g. "sp750-1500-LR") 或
        ///         面板中选的 NONE 选项("&lt;NONE&gt;") — 看到 "<NONE>" ExcelExporter 会 将 selectedSpecCached=null, 跳过 PHASE A.
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> SelectedTileSpecs { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 🔧 v7 本次报价运行时 用户面板选的 各层 模板 (复式跨层模板混合, e.g. 1F=dizhuan / 2F=mudiban / 3F=dizhuan).
        /// 键: 楼层别名 (e.g. "一楼", "二楼", "三楼", ..., "九楼" — 与 QuotePanel._floorAliases 同步).
        /// 值: config.TemplateSettings.Templates 字典 的 key (e.g. "dizhuan", "mudiban", "fushi", "zhubaojiao").
        /// 由 Commands 从面板写入, ExcelExporter 读取 — 用于每层独立加载对应 xlsx + 克隆 prototype.
        /// 单层 (IsMultiFloor=false) 时 不使用, 走原 _cmbTemplate 全局单模板.
        /// 不会被 Save() (不持久化, 每次 BJ 重填).
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, string> SelectedFloorTemplates { get; set; } = new Dictionary<string, string>();

        /// <summary>
        /// 从 JSON 文件加载配置
        /// </summary>
        public static QuoteConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                var defaultConfig = CreateDefault();
                defaultConfig.Save(path);
                return defaultConfig;
            }

            var json = File.ReadAllText(path);
            var cfg = JsonConvert.DeserializeObject<QuoteConfig>(json);
            // 向下兼容：旧配置没有 TemplateSettings 时填默认值
            if (cfg.TemplateSettings == null)
                cfg.TemplateSettings = new TemplateSettingsConfig();
            if (cfg.BathroomKitchenDefaults == null)
                cfg.BathroomKitchenDefaults = new BathroomKitchenDefaults();
            // 向下兼容：旧配置 BathroomKitchenDefaults 缺少新增字段时回填默认值
            // (Newtonsoft.Json 对缺失的 double 属性默认 0.0，会导致厨房/阳台/外花园防水静默归零)
            if (cfg.BathroomKitchenDefaults.KitchenWaterproofHeight <= 0)
                cfg.BathroomKitchenDefaults.KitchenWaterproofHeight = 0.6;
            if (cfg.BathroomKitchenDefaults.BalconyWaterproofHeight <= 0)
                cfg.BathroomKitchenDefaults.BalconyWaterproofHeight = 0.6;
            if (cfg.BathroomKitchenDefaults.TileHeight <= 0)
                cfg.BathroomKitchenDefaults.TileHeight = 2.4;
            if (cfg.BathroomKitchenDefaults.WaterproofHeight <= 0)
                cfg.BathroomKitchenDefaults.WaterproofHeight = 2.0;
            if (cfg.BathroomKitchenDefaults.OutdoorGardenRollHeight <= 0)
                cfg.BathroomKitchenDefaults.OutdoorGardenRollHeight = 0.8;
            if (cfg.BathroomKitchenDefaults.OutdoorGardenNonRollHeight <= 0)
                cfg.BathroomKitchenDefaults.OutdoorGardenNonRollHeight = 0.6;
            return cfg;
        }

        /// <summary>
        /// 保存为 JSON 文件
        /// </summary>
        public void Save(string path)
        {
            var json = JsonConvert.SerializeObject(this, Formatting.Indented);
            File.WriteAllText(path, json);
        }

        /// <summary>
        /// 创建默认配置
        /// </summary>
        public static QuoteConfig CreateDefault()
        {
            return new QuoteConfig
            {
                CompanyName = "广东帝豪装饰集团巴中分公司（工程预算单）",
                CompanyAddress = "公司地址:巴中市黄家沟居然之家四楼2-4-009号    电话:0827-3888300",
                DefaultWallHeight = 2800.0,
                RoomTypeMaps = new List<RoomTypeMap>
                {
                    new RoomTypeMap { Keywords = new List<string> { "花园", "露台", "庭院", "天井", "花池", "花坛" }, RoomType = "外花园" },
                    new RoomTypeMap { Keywords = new List<string> { "客餐厅", "客厅", "餐厅", "楼梯", "起居", "门厅", "玄关", "过道", "走廊", "入户", "堂屋", "阁楼", "地下室", "吧台", "家庭厅", "阳光房" }, RoomType = "客餐厅" },
                    new RoomTypeMap { Keywords = new List<string> { "阳台" }, RoomType = "阳台" },
                    // 🔧 v11: 主卫 RoomTypeMap 必须在 卫生间 之前 — ClassifyRoom first-match-wins, "主卧卫生间" 类房间归主卫 bucket 而非公卫.
                    new RoomTypeMap { Keywords = new List<string> { "主卫", "主卧内卫", "主卧卫生间", "主人卫生间" }, RoomType = "主卫" },
                    new RoomTypeMap { Keywords = new List<string> { "卫", "厕所", "洗手间" }, RoomType = "卫生间" },
                    new RoomTypeMap { Keywords = new List<string> { "厨" }, RoomType = "厨房" },
                    // 🔧 主卧 RoomTypeMap 必须在 卧室 之前 — ClassifyRoom first-match-wins,
                    //    若主卧 keyword 在「卧」后 则「主卧、衣帽间」模板 group 会被错配到 卧室.
                    new RoomTypeMap { Keywords = new List<string> { "主卧", "主人房", "主人卧室" }, RoomType = "主卧" },
                    new RoomTypeMap { Keywords = new List<string> { "卧", "书房", "父母", "儿童", "小孩", "保姆", "衣帽间", "储物间", "茶室", "钢琴房", "老人房", "女儿房", "儿子房", "客房", "电竞房", "影音室", "健身房", "棋牌室", "麻将室", "酒窖", "红酒", "画室", "工作室", "佛堂", "桑拿", "KTV", "多功能室", "休闲厅", "收藏室", "台球室" }, RoomType = "卧室" }
                },
                QuoteItems = new List<QuoteItemConfig>
                {
                    new QuoteItemConfig
                    {
                        Name = "墙顶面基层加固",
                        Unit = "m²",
                        MaterialPrice = 2.0,
                        LaborPrice = 4.0,
                        CalcRule = "CeilingAndWall",
                        Description = "鸟巢界面剂或德高界面剂、人工及机具费。"
                    },
                    new QuoteItemConfig
                    {
                        Name = "墙面基层处理（鸟巢腻子）",
                        Unit = "m²",
                        MaterialPrice = 19.0,
                        LaborPrice = 13.0,
                        CalcRule = "CeilingAndWall",
                        Description = "鸟巢水性嵌缝石膏补顶缝、瑞安健康找平腻子底层、瑞安健康腻子面层、白布、砂纸、牛皮纸、人工及机具费。"
                    },
                    // 🔧 v16: 窗帘盒 (curtain box). 数量 = room.CurtainBoxLength (米, 由 WindowBoxDetector 自动检测填充).
                    //   模板需建一行名含「窗帘盒」的子项. ExcelExporter.FillRoomData 走 IsCurtainBoxItem 独立路径.
                    new QuoteItemConfig
                    {
                        Name = "窗帘盒",
                        Unit = "m",
                        MaterialPrice = 0.0,
                        LaborPrice = 0.0,
                        CalcRule = "CurtainBox",
                        Description = "按房间内覆盖窗户的墙段总长(米)计。WindowBoxDetector 自动扫 DWG (Layer=\"窗户\" 或 显式 ColorIndex 251) 检测。"
                    }
                },
                TemplateSettings = new TemplateSettingsConfig
                {
                    TemplateFolderPath = "",
                    ActiveTemplate = "dizhuan",
                    Templates = new Dictionary<string, string>
                    {
                        { "dizhuan", "dizhuan.xlsx" },
                        { "mudiban", "mudiban.xlsx" },
                        { "fushi", "fushi.xlsx" },
                        { "zhubaojiao", "zhubaojiao.xlsx" }
                    },
                    FloorAliasMap = new Dictionary<string, string>
                    {
                        { "1F", "一楼" }, { "2F", "二楼" }, { "3F", "三楼" },
                        { "4F", "四楼" }, { "5F", "五楼" },
                        { "一层", "一楼" }, { "二层", "二楼" }, { "三层", "三楼" },
                        { "首层", "一楼" }
                    },
                    RoomTypeFallbackMap = new Dictionary<string, string>
                    {
                        { "阳台", "客餐厅" },
                        { "外花园", "客餐厅" },
                        // 🔧 主卧 fallback 到 客餐厅: 有模板 没主卧 group 时 默认都走 客餐厅 prototype
                        //   (eg dizhuan/mudiban 模板只有客餐厅 + 厨房 之类, 不会混主卧). 主床 / 主卫 错配会静默隐除.
                        // 若未来 dizhuan 模板加上了 主卧/主卫 独立 group, 可以从 config.json 删除该行.
                        { "主卧", "客餐厅" },
                        // 🔧 v11: 主卫 fallback 到 卫生间 — dizhuan/mudiban 模板没独立主卫 group, 共用「二 卫生间」 prototype. 主卫 rooms 仍会用「卫生间」 group 克隆.
                        { "主卫", "卫生间" }
                    },
                    FloorItemKeywords = new List<string> { "地面保护", "铺地砖", "地砖", "地板", "正铺" },
                    WallItemKeywords = new List<string> { "墙顶面基层加固", "墙面基层处理", "鸟巢腻子", "芬琳芬华", "五合一", "内墙乳胶漆" },
                    TileSpecOptions = new Dictionary<string, List<TileSpecOption>>
                    {
                        ["客餐厅"] = new List<TileSpecOption>
                        {
                            // 顺序无关, 因为 Match 用「全部命中」做精准过滤:
                            //   sp300-800 要求「正铺」AND「300-800MM」都存在 → 与菱铺(300-800) 不歧义
                            //   spDiamond 只要「菱铺」 → 单独命中
                            new TileSpecOption { Label = "正铺 300-800MM (人工32)",   Value = "sp300-800",  Match = new List<string> { "正铺", "300-800MM" } },
                            new TileSpecOption { Label = "正铺 600*1200MM (人工48)",  Value = "sp600-1200", Match = new List<string> { "正铺", "600*1200" } },
                            new TileSpecOption { Label = "正铺 750*1500MM (人工71)",  Value = "sp750-1500", Match = new List<string> { "正铺", "750*1500" } },
                            new TileSpecOption { Label = "正铺 800*1600MM (人工81)",  Value = "sp800-1600", Match = new List<string> { "正铺", "800*1600" } },
                            new TileSpecOption { Label = "正铺 900*1800MM (人工91)",  Value = "sp900-1800", Match = new List<string> { "正铺", "900*1800" } },
                            new TileSpecOption { Label = "菱铺 300-800MM (人工48)",   Value = "spDiamond",  Match = new List<string> { "菱铺" } }
                        },
                        // 🔧 v11: 主卫 TileSpec — 主卫 与客餐厅 共用 tile 体系 (master bath 贴地砖). Value 后缀 -MBR-W 区分主卫 (避免与客餐厅 spec value 撞).
                        ["主卫"] = new List<TileSpecOption>
                        {
                            new TileSpecOption { Label = "正铺 300-800MM (人工32)",   Value = "sp300-800-MBR-W",  Match = new List<string> { "正铺", "300-800MM" }, IsDefault = true },
                            new TileSpecOption { Label = "正铺 600*1200MM (人工48)",  Value = "sp600-1200-MBR-W", Match = new List<string> { "正铺", "600*1200" }, MaterialPrice = 28.0, LaborPrice = 48.0 },
                            new TileSpecOption { Label = "正铺 750*1500MM (人工71)",  Value = "sp750-1500-MBR-W", Match = new List<string> { "正铺", "750*1500" }, MaterialPrice = 28.0, LaborPrice = 71.0 },
                            new TileSpecOption { Label = "正铺 800*1600MM (人工81)",  Value = "sp800-1600-MBR-W", Match = new List<string> { "正铺", "800*1600" }, MaterialPrice = 28.0, LaborPrice = 81.0 },
                            new TileSpecOption { Label = "正铺 900*1800MM (人工91)",  Value = "sp900-1800-MBR-W", Match = new List<string> { "正铺", "900*1800" }, MaterialPrice = 28.0, LaborPrice = 91.0 },
                            new TileSpecOption { Label = "菱铺 300-800MM (人工48)",   Value = "spDiamond-MBR-W",  Match = new List<string> { "菱铺" }, MaterialPrice = 28.0, LaborPrice = 48.0 }
                        },
                        // 🔧 v13: 阳台 TileSpec — 阳台 与 客餐厅 共用 tile 体系 (地砖). Value 后缀 -BAL 区分客餐厅.
                        ["阳台"] = new List<TileSpecOption>
                        {
                            new TileSpecOption { Label = "正铺 300-800MM (人工32)",   Value = "sp300-800-BAL",  Match = new List<string> { "正铺", "300-800MM" }, MaterialPrice = 28.0, LaborPrice = 32.0 },
                            new TileSpecOption { Label = "正铺 600*1200MM (人工48)",  Value = "sp600-1200-BAL", Match = new List<string> { "正铺", "600*1200" }, IsDefault = true, MaterialPrice = 28.0, LaborPrice = 48.0 },
                            new TileSpecOption { Label = "正铺 750*1500MM (人工71)",  Value = "sp750-1500-BAL", Match = new List<string> { "正铺", "750*1500" }, MaterialPrice = 28.0, LaborPrice = 71.0 },
                            new TileSpecOption { Label = "正铺 800*1600MM (人工81)",  Value = "sp800-1600-BAL", Match = new List<string> { "正铺", "800*1600" }, MaterialPrice = 28.0, LaborPrice = 81.0 },
                            new TileSpecOption { Label = "正铺 900*1800MM (人工91)",  Value = "sp900-1800-BAL", Match = new List<string> { "正铺", "900*1800" }, MaterialPrice = 28.0, LaborPrice = 91.0 },
                            new TileSpecOption { Label = "菱铺 300-800MM (人工48)",   Value = "spDiamond-BAL",  Match = new List<string> { "菱铺" }, MaterialPrice = 28.0, LaborPrice = 48.0 }
                        },
                        // 🔧 v13: 外花园 TileSpec — 外花园 与 客餐厅 共用 tile. Value 后缀 -OG.
                        ["外花园"] = new List<TileSpecOption>
                        {
                            new TileSpecOption { Label = "正铺 300-800MM (人工32)",   Value = "sp300-800-OG",  Match = new List<string> { "正铺", "300-800MM" }, MaterialPrice = 28.0, LaborPrice = 32.0 },
                            new TileSpecOption { Label = "正铺 600*1200MM (人工48)",  Value = "sp600-1200-OG", Match = new List<string> { "正铺", "600*1200" }, IsDefault = true, MaterialPrice = 28.0, LaborPrice = 48.0 },
                            new TileSpecOption { Label = "正铺 750*1500MM (人工71)",  Value = "sp750-1500-OG", Match = new List<string> { "正铺", "750*1500" }, MaterialPrice = 28.0, LaborPrice = 71.0 },
                            new TileSpecOption { Label = "正铺 800*1600MM (人工81)",  Value = "sp800-1600-OG", Match = new List<string> { "正铺", "800*1600" }, MaterialPrice = 28.0, LaborPrice = 81.0 },
                            new TileSpecOption { Label = "正铺 900*1800MM (人工91)",  Value = "sp900-1800-OG", Match = new List<string> { "正铺", "900*1800" }, MaterialPrice = 28.0, LaborPrice = 91.0 },
                            new TileSpecOption { Label = "菱铺 300-800MM (人工48)",   Value = "spDiamond-OG",  Match = new List<string> { "菱铺" }, MaterialPrice = 28.0, LaborPrice = 48.0 }
                        }
                    }
                }
            };
        }
    }

    public class RoomTypeMap
    {
        public List<string> Keywords { get; set; }
        public string RoomType { get; set; }
    }

    public class QuoteItemConfig
    {
        public string Name { get; set; }
        public string Unit { get; set; }
        public double MaterialPrice { get; set; }
        public double LaborPrice { get; set; }
        /// <summary>
        /// 计算规则：
        /// Floor - 地面面积
        /// CeilingAndWall - 地面面积 + 周长 × 高度
        /// </summary>
        public string CalcRule { get; set; }

        /// <summary>
        /// 是否为统计项（不显示单价/合价）
        /// </summary>
        public bool IsSummaryItem { get; set; }

        public string Description { get; set; }
    }

    /// <summary>
    /// 厨卫/特殊房间默认测算参数 (走 config.json 可调)
    /// </summary>
    public class BathroomKitchenDefaults
    {
        /// <summary>默认门洞扣减面积 (㎡)</summary>
        public double DefaultDoorDeduct { get; set; } = 1.4;
        /// <summary>默认窗洞扣减面积 (㎡)</summary>
        public double DefaultWindowDeduct { get; set; } = 0.4;
        /// <summary>贴瓷片 / 瓷砖背胶 高度 (m)</summary>
        public double TileHeight { get; set; } = 2.4;
        /// <summary>卫生间墙面防水保护层高度 (m)</summary>
        public double WaterproofHeight { get; set; } = 2.0;
        /// <summary>厨房墙面防水层高度 (m)</summary>
        public double KitchenWaterproofHeight { get; set; } = 0.6;
        /// <summary>阳台墙面防水层高度 (m)</summary>
        public double BalconyWaterproofHeight { get; set; } = 0.6;
        /// <summary>外花园卷材防水墙面高度 (m)</summary>
        public double OutdoorGardenRollHeight { get; set; } = 0.8;
        /// <summary>外花园非卷材墙面防水高度 (m)</summary>
        public double OutdoorGardenNonRollHeight { get; set; } = 0.6;
        /// <summary>预留: 沉箱架空高度 (m) — 后续阳台/卫浴 启用</summary>
        public double SumpPitHeight { get; set; } = 0.0;
        /// <summary>预留: 花池高度 (m) — 后续外花园 启用</summary>
        public double FlowerPoolHeight { get; set; } = 0.0;
    }

    /// <summary>
    /// 模板面板设置：模板目录、活动模板、关键词匹配规则、楼层别名
    /// </summary>
    public class TemplateSettingsConfig
    {
        /// <summary>模板文件夹路径（绝对路径）</summary>
        public string TemplateFolderPath { get; set; } = "";

        /// <summary>当前激活的模板别名（如 "dizhuan"），对应 Templates 字典的 Key</summary>
        public string ActiveTemplate { get; set; } = "dizhuan";

        /// <summary>模板别名 → 文件名 映射（如 "dizhuan" → "dizhuan.xlsx"）</summary>
        public Dictionary<string, string> Templates { get; set; } = new Dictionary<string, string>();

        /// <summary>楼层别名归一化（如 "1F" → "一楼"），用于复式多层匹配</summary>
        public Dictionary<string, string> FloorAliasMap { get; set; } = new Dictionary<string, string>();

        /// <summary>填房间地面面积的项目关键字</summary>
        public List<string> FloorItemKeywords { get; set; } = new List<string>();

        /// <summary>房间类型→模板组回退映射（配置阳台/外花园等映射到客餐厅等）</summary>
        public Dictionary<string, string> RoomTypeFallbackMap { get; set; } = new Dictionary<string, string>();

        /// <summary>填房间墙顶面面积的项目关键字</summary>
        public List<string> WallItemKeywords { get; set; } = new List<string>();

        /// <summary>
        /// 瓷砖规格选项 (按房间类型): 同时多个 tile variant 行 (e.g. 「客厅及餐厅正铺地砖 (300-800MM)」 / (600*1200) / (菱铺) 等).
        /// UI: QuotePanel 对 ≥2 个 variant 的 roomType 显示规格下拉. 用户选一个 → 其余行 D 列清零, 只留匹配行生效.
        /// 匹配: Match 是 *全部* 必须命中的子串列表, 用于精准区分同尺寸下「正铺」与「菱铺」之类歧义.
        /// </summary>
        public Dictionary<string, List<TileSpecOption>> TileSpecOptions { get; set; } = new Dictionary<string, List<TileSpecOption>>();
    }

    /// <summary>
    /// 瓷砖规格选项: 一组与一组 Match 子串 (全部命中才算匹配).
    /// </summary>
    public class TileSpecOption
    {
        /// <summary>面板下拉显示标签 (e.g. "正铺 300-800MM (人工32)")</summary>
        public string Label { get; set; }
        /// <summary>唯一值 (e.g. "sp300-800"), 写回 SelectedTileSpecs</summary>
        public string Value { get; set; }
        /// <summary>item.Name 必须 *全部* 包含这些子串 才算命中此规格</summary>
        public List<string> Match { get; set; } = new List<string>();
        /// <summary>面板启动默认 选中此项 (首个 isDefault=true 的优先; 都无 则 index=0).</summary>
        [JsonProperty("isDefault", NullValueHandling = NullValueHandling.Ignore, DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsDefault { get; set; } = false;

        /// <summary>单规格模板 override: 选中此规格后, 该 row 的 C5(材质) 单价 将被覆写为此值. null = 不覆写 (保留模板原值).</summary>
        [JsonProperty("materialPrice", NullValueHandling = NullValueHandling.Ignore)]
        public double? MaterialPrice { get; set; } = null;

        /// <summary>单规格模板 override: 选中此规格后, 该 row 的 C7(人工) 单价 将被覆写为此值. null = 不覆写 (保留模板原值).</summary>
        [JsonProperty("laborPrice", NullValueHandling = NullValueHandling.Ignore)]
        public double? LaborPrice { get; set; } = null;
    }
}
