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
                    new RoomTypeMap { Keywords = new List<string> { "卫", "厕所", "洗手间" }, RoomType = "卫生间" },
                    new RoomTypeMap { Keywords = new List<string> { "厨" }, RoomType = "厨房" },
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
                        { "外花园", "客餐厅" }
                    },
                    FloorItemKeywords = new List<string> { "地面保护", "铺地砖", "地砖", "地板", "正铺" },
                    WallItemKeywords = new List<string> { "墙顶面基层加固", "墙面基层处理", "鸟巢腻子", "芬琳芬华", "五合一", "内墙乳胶漆" }
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
    }
}
