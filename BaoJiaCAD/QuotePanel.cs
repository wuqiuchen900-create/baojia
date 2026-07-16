using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace BaoJiaCAD
{
    /// <summary>
    /// BJ 命令的参数输入面板（替代命令行逐项询问）。
    /// 用户一次性填写：工程名称 / 墙面高度 / 复式户型 / 模板选择 / 防水参数。
    /// </summary>
    public class QuotePanel : Form
    {
        // ── 控件 ──
        private TextBox _txtProjectName;
        private NumericUpDown _numWallHeight;
        private CheckBox _chkMultiFloor;
        private NumericUpDown _numFloorCount;
        private Label _lblFloorCount;
        private ComboBox _cmbTemplate;
        private NumericUpDown _numBathWaterproof;
        private NumericUpDown _numTileHeight;
        private NumericUpDown _numDoorDeduct;
        private NumericUpDown _numWindowDeduct;
        private NumericUpDown _numKitchenWaterproof;
        private NumericUpDown _numBalconyWaterproof;
        private NumericUpDown _numGardenRoll;
        private NumericUpDown _numGardenNonRoll;
        private Button _btnStart;
        // 🔧 v9.2: BuildTileSpecSection pushUp 后的 初始 form 高, RebuildMultiFloorGrid 作为 下穿底, 不硬编码 760 漂移.
        //   防御默认 = 760 — 万一 BuildTileSpecSection 提前 return (空 TileSpecOptions) 字段 为 0 防 Math.Max 跌破 原 layout
        private int _initialFormHeight = 760;
        private Button _btnCancel;
        // 🔧 v22: 「恢复默认」按钮 — 删除 user-overrides.json + 重置 内存 state 到 config.json 默认值。
        private Button _btnReset;
        // 🔧 v22: 记住 上次 ctor 传入 的 config/dwgName — ResetToDefaults 需要 重 填 默认值 时 用。
        private readonly QuoteConfig _config;
        private readonly string _dwgName;

        // 🔧 瓷砖规格动态下拉: 仅当 config.TemplateSettings.TileSpecOptions[roomType].Count ≥ 2 才生成.
        private readonly Dictionary<string, ComboBox> _tileSpecCombos = new Dictionary<string, ComboBox>();

        // 🔧 v6 多楼层网格 (IsMultiFloor=true 时显示): 行=FloorLevel, 列=RoomType. 用于复式跨层模板/规格混合
        //   (eg 1F=dizhuan-sp750-1500 / 2F=mudiban-NONE / 3F=dizhuan-sp600-1200) 一站式配置.
        // 🔧 v7 在 col 0 加 per-floor 模板下拉 (复式跨层模板混合: 1F=dizhuan / 2F=mudiban / 3F=dizhuan).
        private Panel _pnlSingleSpec;                       // 单楼层 (legacy) UI 容器 — IsMultiFloor=false 时显示
        private TableLayoutPanel _tlpMultiSpec;             // 多楼层网格容器 — IsMultiFloor=true 时显示
        private readonly Dictionary<(string Floor, string Room), ComboBox> _multiFloorCombos
            = new Dictionary<(string Floor, string Room), ComboBox>();
        // 🔧 v7 每层 模板下拉 (key=楼层别名, value=ComboBox)
        private readonly Dictionary<string, ComboBox> _multiFloorTemplateCombos
            = new Dictionary<string, ComboBox>();
        // 🔧 v19: 每层 墙高 (mm) NumericUpDown — 复式不同层层高时, 用户最后一列手填; 默认取面板全局 _numWallHeight.
        private readonly Dictionary<string, NumericUpDown> _multiFloorWallHeightCombos
            = new Dictionary<string, NumericUpDown>();
        // 🔧 v19.1 fix: 跨 RebuildMultiFloorGrid (复式 off→on / _numFloorCount 变) 保留 用户手填值 — 避免 toggle on/off 跟 调层数 后 丢输。
        private readonly Dictionary<string, double> _lastPerFloorHeights = new Dictionary<string, double>();
        // 🔧 specs cache — BuildTileSpecSection 填充, RebuildMultiFloorGrid 重建时读取
        // 🔧 v19 init: 预初始化 为 空 list, 避免 BuildTileSpecSection 早退返 (无 ≥2 variant) 时下游 null scan + Allow downstream callers to use `.Count` 直接 而免 `?.`+`??`  fallback.
        private List<string> _specRoomTypesCache = new List<string>();
        private Dictionary<string, List<TileSpecOption>> _specsDictCache;
        // 🔧 v7 模板列表 cache — BuildTileSpecSection 填充, RebuildMultiFloorGrid 后用
        private List<string> _templatesCache;
        // 🔧 v12: UI 层 隐藏 模板 key 列表 — 不加 到 _cmbTemplate / 多层 grid 每层 下拉项. config.Templates 字典 仍 完整 (优秀 但用户用不到 的模板 隐藏在 UI). 客户用不到 → 隐藏之. 动 config.json Templates 词书 不动 = 仅 UI 隐藏.
        private static readonly HashSet<string> _uiHiddenTemplateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fushi", "zhubaojiao"
        };
        // 🔧 v13.1: master/follower spec 同步 — 客餐厅 = master, [主卧 / 卧室 / 厨房 / 阳台 / 外花园] = follower 默认 跟随 master 变更, 任 follower 被用户 手动改 后 标 manual_override 不再 跟随. 卫生间 / 主卫 独立 不 跟随.
        private const string _masterSpecRoomType = "客餐厅";
        private static readonly HashSet<string> _followerSpecRoomTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "主卧", "卧室", "厨房", "阳台", "外花园"
        };
        private static readonly Regex _specSizeTokenRegex = new Regex(@"\d+(?:[\-\*]\d+)?MM", RegexOptions.Compiled);

        /// <summary>用户面板 NONE marker. ExcelExporter 看到此值 → selectedSpecCached=null, 跳过 PHASE A (mudiban 风格).</summary>
        public const string NoneSpecValue = "<NONE>";
        /// <summary>面板 NONE 选项 stub (Label 「&lt;无/木地板&gt;」). 注入多楼层 ComboBox.Items[0].</summary>
        public static readonly TileSpecOption NoneSpecOption = new TileSpecOption
        {
            Label = "<无/木地板>",
            Value = NoneSpecValue
        };
        // 🔧 v7 楼层别名 (9 层 max). 多楼层网格行头. 与 Commands.FloorAliases 同步.
        private static readonly string[] _floorAliases = {
            "一楼", "二楼", "三楼", "四楼", "五楼", "六楼", "七楼", "八楼", "九楼"
        };

        // ── 输出属性（调用方读取）──
        public string ProjectName => _txtProjectName.Text.Trim();
        public double WallHeight => (double)_numWallHeight.Value;
        public bool IsMultiFloor => _chkMultiFloor.Checked;
        public int FloorCount => (int)_numFloorCount.Value;
        public string SelectedTemplate => _cmbTemplate.SelectedItem?.ToString() ?? "dizhuan";
        public double BathWaterproofHeight => (double)_numBathWaterproof.Value;
        public double TileHeight => (double)_numTileHeight.Value;
        public double DoorDeduct => (double)_numDoorDeduct.Value;
        public double WindowDeduct => (double)_numWindowDeduct.Value;
        public double KitchenWaterproofHeight => (double)_numKitchenWaterproof.Value;
        public double BalconyWaterproofHeight => (double)_numBalconyWaterproof.Value;
        public double OutdoorGardenRollHeight => (double)_numGardenRoll.Value;
        public double OutdoorGardenNonRollHeight => (double)_numGardenNonRoll.Value;

        /// <param name="config">已加载的配置（用于填充模板下拉、默认值）</param>
        /// <param name="dwgName">当前 DWG 文件名（预填工程名称）</param>
        public QuotePanel(QuoteConfig config, string dwgName)
        {
            _config = config;   // 🔧 v22 — Record for ResetToDefaults
            _dwgName = dwgName; // 🔧 v22 — Record for ResetToDefaults
            InitializeComponent();
            BuildTileSpecSection(config);   // 🔧 根据配置动态生成规格下拉 (在 InitializeComponent 之后, 在 ApplyConfig 之前)
            ApplyConfig(config, dwgName);
            ApplyUserOverrides();   // 🔧 v20 跨实例 UI 记忆 — 加载 user-overrides.json 后 覆盖 默认值
        }

        /// <summary>
        /// 动态生成瓷砖规格下拉 (仅 ≥2 variant 的 roomType 才显示).
        /// - 嵌入位置: 从 InitializeComponent 算出的「_btnStart.Top - rowH*2」往上传交, 避免把按钮挤下去.
        /// - 用 form.Controls.AddRange 一次性插入分隔符 + 若干 Label + ComboBox.
        /// </summary>
        private void BuildTileSpecSection(QuoteConfig config)
        {
            var specsDict = config?.TemplateSettings?.TileSpecOptions;
            if (specsDict == null || specsDict.Count == 0) return;

            // 只取 ≥2 variant 的类别
            var entries = specsDict
                .Where(kv => kv.Value != null && kv.Value.Count >= 2)
                .ToList();
            if (entries.Count == 0) return;

            // 🔧 v6: 缓存 specs — RebuildMultiFloorGrid 后用
            _specsDictCache = specsDict;
            _specRoomTypesCache = entries.Select(e => e.Key).ToList();
            // 🔧 v7: 缓存 templates — RebuildMultiFloorGrid 每层模板下拉 项用
            _templatesCache = config?.TemplateSettings?.Templates?.Keys
                .Where(k => !_uiHiddenTemplateKeys.Contains(k))
                .ToList() ?? new List<string>();

            const int labelW = 80;
            const int comboW = 280;
            int rowH = 32;
            int originalBtnTop = _btnStart.Top;
            int originalCancelTop = _btnCancel.Top;

            // 单楼层 section 高度
            int singleHeightNeeded = entries.Count * rowH + 22;
            // 多楼层 section 最大高度 (1 表头 + 5 层 rows × 32)
            int multiHeightNeeded = (5 + 1) * rowH + 8;
            // 取最大 heights 作 pushUp — 避免切换时按钮跳动
            int pushUp = Math.Max(singleHeightNeeded, multiHeightNeeded);

            _btnStart.Top = originalBtnTop + pushUp;
            _btnCancel.Top = originalCancelTop + pushUp;
            this.Size = new Size(this.Width, this.Height + pushUp);
            this.AutoScroll = true;   // 防 CAD palette 高度限制
            // 🔧 v9.2: 缓存初始 form 高 — RebuildMultiFloorGrid 不下穿 (低于此则保留)
            _initialFormHeight = this.Height;

            // ── 单楼层 UI: 装进 Panel 一并隐藏 ──
            _pnlSingleSpec = new Panel
            {
                Location = new Point(20, originalBtnTop),
                Size = new Size(this.Width - 40, singleHeightNeeded + 8),
                Visible = true,
                BackColor = Color.Transparent,
            };

            int yStart = 0;  // panel 内的局部坐标
            var sep = new Label
            {
                Text = "── 瓷砖规格 (按房间) ──",
                Left = 0, Top = yStart,
                Width = 380,
                ForeColor = SystemColors.GrayText,
                Font = new Font("Microsoft YaHei", 8F)
            };
            _pnlSingleSpec.Controls.Add(sep);
            yStart += 22;

            int xLabel = 0;
            int xCombo = labelW + 8;
            for (int i = 0; i < entries.Count; i++)
            {
                string roomType = entries[i].Key;
                List<TileSpecOption> list = entries[i].Value;
                int y = yStart + i * rowH;

                var lbl = new Label
                {
                    Text = $"{roomType}：",
                    Left = xLabel, Top = y, Width = labelW,
                    TextAlign = ContentAlignment.MiddleRight
                };
                var cb = new ComboBox
                {
                    Left = xCombo, Top = y, Width = comboW,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };
                // 🔧 v14: 单层 UI 也 prepend NoneSpecOption (idx 0) — 此前仅复式 UI prepend, 单层 UI 缺 "<无/木地板>" 选项.
                //   复式 行 这里也走同一 NoneSpecValue (NoneSpecOption), 单+复 行为统一. 选中 → ExcelExporter FillRoomData 检测 <NONE> 做特殊 layout (e.g. mudiban 客厅 ← 地砖改 找平 + 保护=0).
                cb.Items.Add(NoneSpecOption);
                foreach (var spec in list)
                    cb.Items.Add(spec);
                cb.DisplayMember = nameof(TileSpecOption.Label);
                // defaultIdx: list[0] 现在位于 cb.Items[1] (because NONE prepended) — 选定 IsDefault spec 时 +1.
                int defaultSpecIdx = -1;
                for (int s = 0; s < list.Count; s++)
                {
                    if (list[s] is TileSpecOption opt2 && opt2.IsDefault)
                    {
                        defaultSpecIdx = s;
                        break;
                    }
                }
                cb.SelectedIndex = defaultSpecIdx >= 0 ? (defaultSpecIdx + 1) : 1;

            _tileSpecCombos[roomType] = cb;
            _pnlSingleSpec.Controls.Add(lbl);
            _pnlSingleSpec.Controls.Add(cb);
        }
        this.Controls.Add(_pnlSingleSpec);
        // 🔧 v13: 单层 panel 的 客餐厅 ↔ [厨房/主卧/阳台/外花园] master/follower 同步
        WireSpecMasterSync(_tileSpecCombos);

            // ── 多楼层 TableLayoutPanel (默认隐藏) ──
            _tlpMultiSpec = new TableLayoutPanel
            {
                Location = new Point(20, originalBtnTop),
                Size = new Size(1660, multiHeightNeeded),  // 🔧 v13.3 expand: 840 → 1660 容纳 8 个 180F spec cols + 130 + 60.
                Visible = false,
                AutoSize = false,
                CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
                BackColor = Color.FromArgb(252, 252, 252),
            };
            this.Controls.Add(_tlpMultiSpec);
        }

        /// <summary>
        /// 🔧 v7 多楼层网格 重画 — 在 _chkMultiFloor.CheckedChanged / _numFloorCount.ValueChanged 触发.
        ///   - 第 1 行: 列头 = [模板 (空)] + roomType 列表
        ///   - 第 2 行起: 行头 = 模板 ComboBox + "一楼" / "二楼" / ... ; 每格 = ComboBox(NONE+规格列表)
        ///   - 默认值: 1F 模板 用 _cmbTemplate 单下拉值; 2F+ 模板 默认复制 1F. 规格 同 v6.
        /// </summary>
        private void RebuildMultiFloorGrid()
        {
            if (_tlpMultiSpec == null || _specRoomTypesCache == null || _specRoomTypesCache.Count == 0) return;
            int floorCount = (int)_numFloorCount.Value;
            if (floorCount < 2 || floorCount > 9) return;  // 与 NumericUpDown 上下限同步

            // 🔧 v9: 去掉 this.SuspendLayout / this.ResumeLayout — RebuildMultiFloorGrid 只动 TLP children (form 本身 不加 child), form-level 冻结 会造成 整体 layout 延迟
            // 🔧 v19.1: 重建前 先快照 现存 墙高 NumericUpDown values → cache — 复式 off→on / 层数变 后 能找回手填值。
            foreach (var kv in _multiFloorWallHeightCombos)
                _lastPerFloorHeights[kv.Key] = (double)kv.Value.Value;

            _tlpMultiSpec.SuspendLayout();
            _tlpMultiSpec.Controls.Clear();
            _multiFloorCombos.Clear();
            _multiFloorTemplateCombos.Clear();
            _multiFloorWallHeightCombos.Clear();

            // 🔧 v19: cols = specCount + 3 — +1 模板 +1 别名 +1 墙高(mm) 列. 总宽: 130 + 60 + 180*specCount + 100 + 8(边框).
            int cols = _specRoomTypesCache.Count + 3;
            int rows = floorCount + 1;                    // +1 列表头行
            _tlpMultiSpec.ColumnCount = cols;
            _tlpMultiSpec.RowCount = rows;

            // 列宽: col 0=130 模板, col 1=60 别名, cols 2..cols-2 = 180 规格, 末列 col cols-1=100 墙高
            _tlpMultiSpec.ColumnStyles.Clear();
            _tlpMultiSpec.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
            _tlpMultiSpec.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60F));
            for (int c = 2; c < cols - 1; c++)
                _tlpMultiSpec.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180F));  // 🔧 v13.3 expand: 110F 不能 装 "正铺 750*1500MM (人工71)" (~154px) — 截断 as "正铺 75C". 8 个 spec cols × 180F = 1440 + 130(template) + 60(alias) + 100(wallHeight) = 1730.
            _tlpMultiSpec.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100F));  // 🔧 v19 末列 墙高 (mm) NumericUpDown
            // 🔧 v19: 动态设 TLP 总宽 — 适应 specCount 变化 (3~9 spec room types 都需要 TLP 窝下)
            int tlpTotalWidth = 130 + 60 + 180 * _specRoomTypesCache.Count + 100 + 8;
            _tlpMultiSpec.Width = tlpTotalWidth;
            // 行高: 32px
            _tlpMultiSpec.RowStyles.Clear();
            for (int r = 0; r < rows; r++)
                _tlpMultiSpec.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            // 🔧 v9.1: 双向同步 form & 按钮 — 升高 (复式层↑) 和 降回 (复式层↓) 都要重排, 避免 9→2 后 按钮 仍顿在 822 出现空隔
            //   复式: tlpHeight ≥ 200 (=BuildTileSpecSection 的 multiHeightNeeded) 时 按钮 会被表 遮. 同步推下.
            // 🔧 v9.2: targetH 用 缓存的 _initialFormHeight 替代硬编码 760, 避免 与 BuildTileSpecSection pushUp 漂移.
            const int ROW_H = 32, GAP = 8;
            int tlpHeight = rows * ROW_H + GAP;
            _tlpMultiSpec.Height = tlpHeight;
            int tlpBottom = _tlpMultiSpec.Bottom;
            int needFormH = tlpBottom + _btnStart.Height + this.Padding.Bottom + GAP;
            int targetH = Math.Max(_initialFormHeight, needFormH);
            if (targetH != this.Height) this.Height = targetH;
            _btnStart.Top = tlpBottom + GAP;
            _btnCancel.Top = tlpBottom + GAP;

            // 第 1 行: 列头 (col 0=模板, col 1="层 \\ 类", col 2..N+1=roomType)
            _tlpMultiSpec.Controls.Add(new Label
            {
                Text = "层 \\ 模板",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
            }, 0, 0);
            _tlpMultiSpec.Controls.Add(new Label
            {
                Text = "层 \\ 类",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
            }, 1, 0);
            for (int c = 0; c < _specRoomTypesCache.Count; c++)
            {
                _tlpMultiSpec.Controls.Add(new Label
                {
                    Text = _specRoomTypesCache[c],
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                }, c + 2, 0);
            }
            // 🔧 v19: 末列表头 — 墙高 (mm)
            _tlpMultiSpec.Controls.Add(new Label
            {
                Text = "墙高 (mm)",
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleCenter,
                Dock = DockStyle.Fill,
            }, cols - 1, 0);

            // 第 2 行起: floor rows
            ComboBox[,] cells = new ComboBox[floorCount, _specRoomTypesCache.Count];
            ComboBox[] tplCells = new ComboBox[floorCount];
            // 1F 默认模板 = _cmbTemplate 单下拉的当前选择 (向前兼容)
            string defaultTemplate = _cmbTemplate?.SelectedItem?.ToString()
                ?? (_templatesCache != null && _templatesCache.Count > 0 ? _templatesCache[0] : "dizhuan");
            for (int r = 0; r < floorCount; r++)
            {
                string floorAlias = _floorAliases[r];

                // col 0: 模板下拉
                var tplCb = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Dock = DockStyle.Fill,
                };
                foreach (var t in _templatesCache ?? new List<string>())
                    tplCb.Items.Add(t);
                if (r == 0)
                {
                    int idx = tplCb.Items.IndexOf(defaultTemplate);
                    tplCb.SelectedIndex = idx >= 0 ? idx : 0;
                }
                else
                {
                    // 2F+ 默认复制 1F
                    ComboBox tpl1F = tplCells[0];
                    tplCb.SelectedIndex = (tpl1F != null && tpl1F.SelectedIndex >= 0) ? tpl1F.SelectedIndex : 0;
                }
                tplCells[r] = tplCb;
                _multiFloorTemplateCombos[floorAlias] = tplCb;
                _tlpMultiSpec.Controls.Add(tplCb, 0, r + 1);

                // col 1: 楼层别名 label
                _tlpMultiSpec.Controls.Add(new Label
                {
                    Text = floorAlias,
                    Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
                    TextAlign = ContentAlignment.MiddleCenter,
                    Dock = DockStyle.Fill,
                }, 1, r + 1);

                // col 2..N+1: spec ComboBox (与 v6 逻辑一致)
                for (int c = 0; c < _specRoomTypesCache.Count; c++)
                {
                    string roomType = _specRoomTypesCache[c];
                    List<TileSpecOption> list = _specsDictCache[roomType] ?? new List<TileSpecOption>();

                    var cb = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Dock = DockStyle.Fill,
                    };
                    cb.DisplayMember = nameof(TileSpecOption.Label);

                    // 🔧 NONE stub prepended — index=0 选中后 ExcelExporter 走 mudiban 风格
                    cb.Items.Add(NoneSpecOption);
                    foreach (var spec in list)
                        cb.Items.Add(spec);

                    // 默认: 1F 用 isDefault+index0 ; 2F+ 默认复制 1F 同列 (行存为变量引用)
                    int defaultIdx;
                    if (r == 0)
                    {
                        int specDefaultIdx = -1;
                        for (int s = 0; s < list.Count; s++)
                            if (list[s] != null && list[s].IsDefault) { specDefaultIdx = s; break; }
                        specDefaultIdx = specDefaultIdx >= 0 ? specDefaultIdx : 0;
                        defaultIdx = 1 + specDefaultIdx;   // 0=NONE, 1=list[0], ...
                    }
                    else
                    {
                        // 🔧 v2 fix: 2F+ 默认复制 1F 同列 — NONE (idx=0) 也保留 —
                        //   用户可全 层 NONE 走 mudiban 兜底, 不会被 强刷为首个 spec.
                        ComboBox c1F = cells[0, c];
                        defaultIdx = (c1F != null && c1F.SelectedIndex >= 0) ? c1F.SelectedIndex : 1;
                    }
                    cb.SelectedIndex = defaultIdx;                cells[r, c] = cb;
                _multiFloorCombos[(floorAlias, roomType)] = cb;
                _tlpMultiSpec.Controls.Add(cb, c + 2, r + 1);
                }
                // 🔧 v19: 末列 墙高 (mm) NumericUpDown — 默认 = 面板全局 _numWallHeight.
                //   🔧 v19.1: 优先从 _lastPerFloorHeights cache 拼 上次用户手填值 (off→on / 跨层数 变 后); miss 才取 全局。
                //   NumericUpDown.Minimum/Maximum 自身 限制 2000-5000, 不需手动 clamp.
                double whDefault = _lastPerFloorHeights.TryGetValue(floorAlias, out var cached) ? cached : (double)_numWallHeight.Value;
                var whCb = new NumericUpDown
                {
                    Minimum = 1000m,    // 🔧 v19.2: 2000→1000 (覆设备间/夹层 1m 以隔不可能装修)
                    Maximum = 9000m,    // 🔧 v19.2: 5000→9000 (覆挑空客厅/大堂)
                    Increment = 100m,
                    Value = (decimal)whDefault,
                    Dock = DockStyle.Fill,
                };
                _lastPerFloorHeights.Remove(floorAlias);   // 🔧 v19.1: 用了 cache entry 删 — 避免 楼层减少 时 残留。
                _multiFloorWallHeightCombos[floorAlias] = whCb;
                _tlpMultiSpec.Controls.Add(whCb, cols - 1, r + 1);
                // 🔧 v13: master/follower 同步 — 每行 独立 跟随, 跨层 不 串.
                var rowCombos = new Dictionary<string, ComboBox>();
                for (int c2 = 0; c2 < _specRoomTypesCache.Count; c2++)
                    rowCombos[_specRoomTypesCache[c2]] = cells[r, c2];
                WireSpecMasterSync(rowCombos);
            }

            _tlpMultiSpec.ResumeLayout(true);   // 🔧 v9: ResumeLayout(true) 强制立即 layout pass — false 会 推迟 到下个 idle, AutoCAD palette 不发 idle 信号 → 行不画
            // 🔧 v9: 不调 this.ResumeLayout(true) — 上面同步 form/按钮 后, WinForms 会自然 layout, 避免 全窗 布局 pass 造成闪烁
        }

        private void InitializeComponent()
        {
            // ── 窗体 ──
            this.Text = "报价参数设置";
            this.Size = new Size(440, 560);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Padding = new Padding(20, 16, 20, 16);
            this.Font = new Font("Microsoft YaHei", 9F);

            int y = 20;
            const int labelW = 100;
            const int fieldW = 260;
            const int rowH = 32;

            // ── 工程名称 ──
            var lblProject = new Label { Text = "工程名称：", Left = 20, Top = y, Width = labelW, TextAlign = ContentAlignment.MiddleRight };
            _txtProjectName = new TextBox { Left = 20 + labelW + 8, Top = y, Width = fieldW };
            y += rowH + 6;

            // ── 墙面高度 ──
            var lblWall = new Label { Text = "墙面高度：", Left = 20, Top = y, Width = labelW, TextAlign = ContentAlignment.MiddleRight };
            _numWallHeight = new NumericUpDown
            {
                Left = 20 + labelW + 8, Top = y, Width = 100,
                Minimum = 1000, Maximum = 9000, Value = 2800, Increment = 100   // 🔧 v19.2: 下限 2000→1000 (覆设设备间/夹层), 上限 5000→9000 (提挑空大厅/大堂)
            };
            var lblWallUnit = new Label { Text = "mm", Left = 20 + labelW + 8 + 108, Top = y, Width = 30, TextAlign = ContentAlignment.MiddleLeft };
            y += rowH + 6;

            // ── 分隔线 ──
            y += 4;

            // ── 复式楼 ──
            _chkMultiFloor = new CheckBox
            {
                Text = "复式楼（多楼层）", Left = 20 + labelW + 8, Top = y,
                Width = 180, Checked = false
            };
            _chkMultiFloor.CheckedChanged += (s, e) =>
            {
                _numFloorCount.Enabled = _chkMultiFloor.Checked;
                _lblFloorCount.ForeColor = _chkMultiFloor.Checked ? SystemColors.ControlText : SystemColors.GrayText;
                // 🔧 v6: 单/复式 UI 切换, 同步表单宽 — _pnlSingleSpec 单楼层, _tlpMultiSpec 多楼层网格
                bool mf = _chkMultiFloor.Checked;
                if (_pnlSingleSpec != null) _pnlSingleSpec.Visible = !mf;
                if (_tlpMultiSpec != null)
                {
                    _tlpMultiSpec.Visible = mf;
                    if (mf) RebuildMultiFloorGrid();
                }
                // 🔧 v13.3 expand: 复式表单宽按 specCount 动态 (v19 加 墙高 列 +100px). 顺便边界安全 多 8px.
                this.Width = mf ? (130 + 60 + 180 * (_specRoomTypesCache?.Count ?? 5) + 100 + 48) : 440;
            };
            y += rowH;

            // ── 复式层数 ──
            _lblFloorCount = new Label { Text = "复式层数：", Left = 40, Top = y, Width = labelW - 20, TextAlign = ContentAlignment.MiddleRight };
            _numFloorCount = new NumericUpDown
            {
                Left = 40 + labelW - 20 + 8, Top = y, Width = 80,
                Minimum = 2, Maximum = 9, Value = 2, Enabled = false  // 🔧 v7 5 → 9 (面板 可 选 9 层)
            };
            // 🔧 v9 BUILD-ERROR-FIX: 必须在 _numFloorCount = new NumericUpDown 之后 再 += ValueChanged —
            //   原代码 顺序 是 _numFloorCount.ValueChanged += ... 在 字段 还是 null 时 访问 .ValueChanged 事件,
            //   v6 隐 bug, v7 由多层 grid RebuildMultiFloorGrid 加强 调用 而 被 F2 trace 抓出 → NullReferenceException.
            _numFloorCount.ValueChanged += (s, e) =>
            {
                if (_chkMultiFloor.Checked) RebuildMultiFloorGrid();
            };
            var lblFloorUnit = new Label { Text = "层", Left = 40 + labelW - 20 + 8 + 88, Top = y, Width = 30, TextAlign = ContentAlignment.MiddleLeft };
            _lblFloorCount.ForeColor = SystemColors.GrayText;
            y += rowH + 6;

            // ── 分隔线 ──
            y += 4;

            // ── 模板选择 ──
            var lblTpl = new Label { Text = "模板选择：", Left = 20, Top = y, Width = labelW, TextAlign = ContentAlignment.MiddleRight };
            _cmbTemplate = new ComboBox
            {
                Left = 20 + labelW + 8, Top = y, Width = 180,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            y += rowH + 6;

            // ── 分隔线 ──
            var sep2 = new Label
            {
                Text = "── 防水 & 贴砖参数（可后续在 config.json 调整）──",
                Left = 20, Top = y, Width = 380,
                ForeColor = SystemColors.GrayText, Font = new Font("Microsoft YaHei", 8F)
            };
            this.Controls.Add(sep2);
            y += 22;

            // 辅助: 快捷 NumericUpDown 生成器
            NumericUpDown MakeNum(decimal val, int x, int yPos)
            {
                return new NumericUpDown { Left = x, Top = yPos, Width = 70, Minimum = 0, Maximum = 5.0M, Value = val, Increment = 0.1M, DecimalPlaces = 1 };
            }
            Label MakeDimLabel(string text, int x, int yPos) => new Label { Text = text, Left = x, Top = yPos, Width = labelW, TextAlign = ContentAlignment.MiddleRight };
            Label MakeUnit(int x, int yPos) => new Label { Text = "m", Left = x, Top = yPos, Width = 24, TextAlign = ContentAlignment.MiddleLeft };

            int col1x = 20, col2x = 20 + labelW + 8 + 70 + 30 + 30;

            // 行1: 卫生间防水高 | 贴砖高度
            var lblBath = MakeDimLabel("卫生间防水高：", col1x, y);
            _numBathWaterproof = MakeNum(2.0M, col1x + labelW + 8, y);
            var lblBathUnit = MakeUnit(col1x + labelW + 8 + 78, y);
            var lblTile = MakeDimLabel("贴砖高度：", col2x, y);
            _numTileHeight = MakeNum(2.4M, col2x + labelW + 8, y);
            var lblTileUnit = new Label { Text = "m", Left = col2x + labelW + 8 + 78, Top = y, Width = 24, TextAlign = ContentAlignment.MiddleLeft };
            y += rowH;

            // 行2: 厨房防水高 | 阳台防水高
            var lblKitchen = MakeDimLabel("厨房防水高：", col1x, y);
            _numKitchenWaterproof = MakeNum(0.6M, col1x + labelW + 8, y);
            var lblKitchenUnit = MakeUnit(col1x + labelW + 8 + 78, y);
            var lblBalcony = MakeDimLabel("阳台防水高：", col2x, y);
            _numBalconyWaterproof = MakeNum(0.6M, col2x + labelW + 8, y);
            var lblBalconyUnit = new Label { Text = "m", Left = col2x + labelW + 8 + 78, Top = y, Width = 24, TextAlign = ContentAlignment.MiddleLeft };
            y += rowH;

            // 行3: 外花园卷材高 | 外花园非卷材高
            var lblGardenRoll = MakeDimLabel("花园卷材高：", col1x, y);
            _numGardenRoll = MakeNum(0.8M, col1x + labelW + 8, y);
            var lblGardenRollUnit = MakeUnit(col1x + labelW + 8 + 78, y);
            var lblGardenNonRoll = MakeDimLabel("花园非卷材高：", col2x, y);
            _numGardenNonRoll = MakeNum(0.6M, col2x + labelW + 8, y);
            var lblGardenNonRollUnit = new Label { Text = "m", Left = col2x + labelW + 8 + 78, Top = y, Width = 24, TextAlign = ContentAlignment.MiddleLeft };
            y += rowH;

            // 行4: 门洞扣减 | 窗洞扣减
            var lblDoor = MakeDimLabel("门洞扣减：", col1x, y);
            _numDoorDeduct = new NumericUpDown { Left = col1x + labelW + 8, Top = y, Width = 70, Minimum = 0, Maximum = 10.0M, Value = 1.4M, Increment = 0.1M, DecimalPlaces = 1 };
            var lblDoorUnit = new Label { Text = "㎡", Left = col1x + labelW + 8 + 78, Top = y, Width = 30, TextAlign = ContentAlignment.MiddleLeft };
            var lblWindow = MakeDimLabel("窗洞扣减：", col2x, y);
            _numWindowDeduct = new NumericUpDown { Left = col2x + labelW + 8, Top = y, Width = 70, Minimum = 0, Maximum = 10.0M, Value = 0.4M, Increment = 0.1M, DecimalPlaces = 1 };
            var lblWindowUnit = new Label { Text = "㎡", Left = col2x + labelW + 8 + 78, Top = y, Width = 30, TextAlign = ContentAlignment.MiddleLeft };
            y += rowH + 12;

            // ── 按钮 ──
            _btnStart = new Button
            {
                Text = "开始识别", Left = 100, Top = y, Width = 100, Height = 34,
                BackColor = Color.FromArgb(0, 122, 204), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold)
            };
            _btnStart.FlatAppearance.BorderSize = 0;
            _btnStart.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_txtProjectName.Text))
                {
                    MessageBox.Show("请输入工程名称。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                SaveUserOverrides();   // 🔧 v20 跨实例 UI 记忆 — OK 点击 后 序列化到 user-overrides.json
                this.DialogResult = DialogResult.OK;
                this.Close();
            };

            // 🔧 v22.2 「恢复默认」按钮 — 位置 顶部 row 「主操作」下方居中
            //   与 btnStart/btnCancel 8px 间距下隔, 属于 secondary action; Resize handler 重算 保 始终居中
            //   (multi toggle / 手动 resize / OS dpi zoom 都会 殃)。
            _btnReset = new Button
            {
                Text = "恢复默认",
                Left = 0,                                  // Resize handler 重算 (form width / 2 - btn.width / 2)
                Top = y + 42,                              // btnStart.Bottom + 8
                Width = 80, Height = 34,
                FlatStyle = FlatStyle.Flat,
                ForeColor = SystemColors.ControlText,
            };
            _btnReset.Click += (s, e) =>
            {
                var r = MessageBox.Show(
                    "恢复默认 会 从 user-overrides.json 清除 您 个人 UI 状态 并 把面板 重置 到 config.json 默认值。\r\n\r\n继续？",
                    "恢复默认", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r == DialogResult.Yes) ResetToDefaults();
            };

            _btnCancel = new Button
            {
                Text = "取消", Left = 220, Top = y, Width = 100, Height = 34,
                FlatStyle = FlatStyle.Flat
            };
            _btnCancel.Click += (s, e) =>
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            };

            // ── 添加控件 ──
            this.Controls.AddRange(new Control[]
            {
                lblProject, _txtProjectName,
                lblWall, _numWallHeight, lblWallUnit,
                _chkMultiFloor,
                _lblFloorCount, _numFloorCount, lblFloorUnit,
                lblTpl, _cmbTemplate,
                lblBath, _numBathWaterproof, lblBathUnit,
                lblTile, _numTileHeight, lblTileUnit,
                lblKitchen, _numKitchenWaterproof, lblKitchenUnit,
                lblBalcony, _numBalconyWaterproof, lblBalconyUnit,
                lblGardenRoll, _numGardenRoll, lblGardenRollUnit,
                lblGardenNonRoll, _numGardenNonRoll, lblGardenNonRollUnit,
                lblDoor, _numDoorDeduct, lblDoorUnit,
                lblWindow, _numWindowDeduct, lblWindowUnit,
                _btnReset, _btnStart, _btnCancel,   // 🔧 v22 恢复默认 按钮 加于 最前
            });

            this.AcceptButton = _btnStart;
            this.CancelButton = _btnCancel;

            // 🔧 v22.2 实时 居中 处理 — form 宽 变化 (multi toggle / 手动 resize / DPI zoom) 时, _btnReset 自动 重 算 Left/Top。
            //   跟 _btnStart 走 0px 间隔 的 8px gap; _btnStart.Top 被 RebuildMultiFloorGrid 主动 重 设 时也 能跟上 (他 Bottom 变 了 ⇒ _btnReset.Top 走)。
            this.Resize += (s, e) =>
            {
                if (_btnReset == null || _btnReset.IsDisposed) return;
                if (_btnReset.Handle == IntPtr.Zero) return;
                _btnReset.Left = Math.Max(0, (this.ClientSize.Width - _btnReset.Width) / 2);
                if (_btnStart != null && !_btnStart.IsDisposed)
                    _btnReset.Top = _btnStart.Bottom + 8;
            };
            // 初始 加载 一次性 居中 — 防止 btnReset 计算前 X=0 闪在 左边角
            if (!this.IsDisposed && _btnReset != null && !_btnReset.IsDisposed && _btnStart != null)
            {
                _btnReset.Left = Math.Max(0, (this.ClientSize.Width - _btnReset.Width) / 2);
                _btnReset.Top = _btnStart.Bottom + 8;
            }
        }

        private void ApplyConfig(QuoteConfig config, string dwgName)
        {
            // 工程名称
            string proj = string.IsNullOrWhiteSpace(dwgName) ? "未命名工程" : dwgName;
            _txtProjectName.Text = proj;

            // 墙面高度
            if (config != null && config.DefaultWallHeight > 0)
                _numWallHeight.Value = (decimal)config.DefaultWallHeight;

            // 模板下拉
            var ts = config?.TemplateSettings;
            if (ts?.Templates != null && ts.Templates.Count > 0)
            {
                _cmbTemplate.Items.AddRange(ts.Templates.Keys
                    .Where(k => !_uiHiddenTemplateKeys.Contains(k))
                    .Cast<object>().ToArray());
                // 选中当前激活模板
                string active = ts.ActiveTemplate ?? "dizhuan";
                int idx = _cmbTemplate.Items.IndexOf(active);
                _cmbTemplate.SelectedIndex = idx >= 0 ? idx : 0;
            }
            else
            {
                _cmbTemplate.Items.Add("dizhuan");
                _cmbTemplate.SelectedIndex = 0;
            }

            // 防水参数
            var defs = config?.BathroomKitchenDefaults;
            if (defs != null)
            {
                _numBathWaterproof.Value = (decimal)defs.WaterproofHeight;
                _numTileHeight.Value = (decimal)defs.TileHeight;
                _numDoorDeduct.Value = (decimal)defs.DefaultDoorDeduct;
                _numWindowDeduct.Value = (decimal)defs.DefaultWindowDeduct;
                _numKitchenWaterproof.Value = (decimal)defs.KitchenWaterproofHeight;
                _numBalconyWaterproof.Value = (decimal)defs.BalconyWaterproofHeight;
                _numGardenRoll.Value = (decimal)defs.OutdoorGardenRollHeight;
                _numGardenNonRoll.Value = (decimal)defs.OutdoorGardenNonRollHeight;
            }
        }

        /// <summary>
        /// 将面板中的防水参数回写到 config（覆盖 config.json 的默认值），
        /// 使本次报价使用面板中填入的高度。
        /// </summary>
        public void ApplyWaterproofToConfig(QuoteConfig config)
        {
            if (config?.BathroomKitchenDefaults == null) return;
            var d = config.BathroomKitchenDefaults;
            d.WaterproofHeight = (double)_numBathWaterproof.Value;
            d.TileHeight = (double)_numTileHeight.Value;
            d.DefaultDoorDeduct = (double)_numDoorDeduct.Value;
            d.DefaultWindowDeduct = (double)_numWindowDeduct.Value;
            d.KitchenWaterproofHeight = (double)_numKitchenWaterproof.Value;
            d.BalconyWaterproofHeight = (double)_numBalconyWaterproof.Value;
            d.OutdoorGardenRollHeight = (double)_numGardenRoll.Value;
            d.OutdoorGardenNonRollHeight = (double)_numGardenNonRoll.Value;
        }

        // 🔧 v13: master/follower spec 同步 — 客餐厅 改, [厨房/主卧/阳台/外花园] 默认 跟着, 任 follower 被 用户 手动 改 后 标 "override" 不再 跟随.
        //   同步规则: master SelectedItem.Label 抽 SIZE_TOKEN (按正则 "\d+([-*]\d+)?MM" , 例 "750*1500MM") → 找 follower.Items 中 Label 也 含同 token 的项 设 它.
        //   这样 跨 正铺/正贴/菱 family 仍 同步 (e.g. 客餐厅 选 "正铺 750*1500MM" 厨房 跳 到 "正贴 750*1500MM"; 阳/外 同 family 同步 同标签).
        //   独立 RoomType (卧室/卫生间/主卫) 不进 followers — 不 跟随 master.
        private void WireSpecMasterSync(IEnumerable<KeyValuePair<string, ComboBox>> comboDict)
        {
            ComboBox master = null;
            var followers = new List<ComboBox>();
            foreach (var kv in comboDict)
            {
                if (kv.Key == _masterSpecRoomType) { master = kv.Value; continue; }
                if (_followerSpecRoomTypes.Contains(kv.Key)) followers.Add(kv.Value);
            }
            if (master == null || followers.Count == 0) return;

            foreach (var fb in followers) fb.Tag = false;

            EventHandler followerMarker = null;
            followerMarker = (s, e) => { ((ComboBox)s).Tag = true; };
            foreach (var fb in followers) fb.SelectedIndexChanged += followerMarker;

            // 🔧 v13.2 bug fix: 抽 local function ApplySyncSafely — 临时 detach followerMarker, 程序设的 SelectedIndex 不 误标 override.
            //   v13.1 bug: 初始 sync 调 SyncFromMasterToFollowers 时 未 detach, 每个 follower SelectedIndex 程序设 跳 触发 followerMarker, 全体 被 误 标 override, 后 续 master 改 全部 被 跳过.  v13.2 补 让 master event + initial sync 共用 同一个 detach/reattach 流程.
            void ApplySyncSafely()
            {
                foreach (var fb in followers) fb.SelectedIndexChanged -= followerMarker;
                try { SyncFromMasterToFollowers(master, followers); }
                finally
                {
                    foreach (var fb in followers) fb.SelectedIndexChanged += followerMarker;
                }
            }

            master.SelectedIndexChanged += (s, e) => ApplySyncSafely();
            // 初次 panel 打开 时 master 已有 SelectedIndex (IsDefault / index 0), 触 一次同步. 走 ApplySyncSafely 避免 初始 follower 被 误标 override.
            if (master.SelectedIndex >= 0) ApplySyncSafely();
        }

        private static void SyncFromMasterToFollowers(ComboBox master, List<ComboBox> followers)
        {
            var sel = master.SelectedItem as TileSpecOption;
            if (sel?.Label == null) return;
            var match = _specSizeTokenRegex.Match(sel.Label);
            if (!match.Success) return;
            string masterSizeToken = match.Value;
            foreach (var fb in followers)
            {
                if (fb.Tag is bool overridden && overridden) continue;
                var matchOpt = fb.Items.OfType<TileSpecOption>()
                    .FirstOrDefault(x => x?.Label != null && x.Label.Contains(masterSizeToken));
                if (matchOpt != null)
                {
                    int idx = fb.Items.IndexOf(matchOpt);
                    if (idx >= 0) fb.SelectedIndex = idx;
                }
            }
        }

        /// <summary>
        /// 从面板里抽用户选的瓷砖规格返回; 由 Commands 写进 config.SelectedTileSpecs (运行时, 不持久化).
        ///   - 单楼层 (IsMultiFloor=false): key=团 RoomType (e.g. "客餐厅"), 老版单key 行为 — ExcelExporter 走 k3 兑底.
        ///   - 复式 (IsMultiFloor=true): key="{Floor}|{RoomType}" (e.g. "一楼|客餐厅") — ExcelExporter 走 k1 主路径,
        ///     NONE 选项照样输出 "<NONE>" 表达「该 (楼层, 房间) 不走 PHASE A、走 mudiban 风格、全身免动」.
        ///   - 未选择的 roomType 不入 — 走 配置退路 或 原始模板价格保留.
        /// </summary>
        public Dictionary<string, string> GetTileSpecSelections()
        {
            var map = new Dictionary<string, string>();
            if (_chkMultiFloor != null && _chkMultiFloor.Checked)
            {
                // 🔧 v6 多楼层网格: 输出 "{Floor}|{Room}" 编码 (NONE 跳出)
                foreach (var kv in _multiFloorCombos)
                {
                    if (kv.Value.SelectedItem is TileSpecOption opt && !string.IsNullOrEmpty(opt.Value))
                        map[$"{kv.Key.Floor}|{kv.Key.Room}"] = opt.Value;
                }
            }
            else
            {
                // 老版单楼层: 仅 RoomType 编码 — 向后兼容
                foreach (var kv in _tileSpecCombos)
                {
                    if (kv.Value.SelectedItem is TileSpecOption opt && !string.IsNullOrEmpty(opt.Value))
                        map[kv.Key] = opt.Value;
                }
            }
            return map;
        }

        /// <summary>
        /// 🔧 v7 从面板里抽用户选的每层 模板 (复式跨层模板混合: 1F=dizhuan / 2F=mudiban / 3F=dizhuan / ...).
        ///   - 单楼层 (IsMultiFloor=false): 返回空 dict (走原 _cmbTemplate 全局单模板).
        ///   - 复式 (IsMultiFloor=true): key=楼层别名 (e.g. "一楼"), value=config.TemplateSettings.Templates 的 key
        ///     (e.g. "dizhuan", "mudiban", "fushi", "zhubaojiao"). Commands 进一步转成 file path.
        ///   - 未选择的楼层不入 (走 fallback: ActiveTemplate).
        /// </summary>
        public Dictionary<string, string> GetFloorTemplateSelections()
        {
            var map = new Dictionary<string, string>();
            if (_chkMultiFloor != null && _chkMultiFloor.Checked)
            {
                foreach (var kv in _multiFloorTemplateCombos)
                {
                    string t = kv.Value.SelectedItem?.ToString();
                    if (!string.IsNullOrEmpty(t))
                        map[kv.Key] = t;
                }
            }
            // 单楼层模式下 返空 dict — ExcelExporter 走 原单 templatePath 路径
            return map;
        }

        /// <summary>
        /// 🔧 v19 从面板里抽用户选的每层 墙高 (mm) — 复式不同层层高.
        ///   - 单楼层 (IsMultiFloor=false): 返回空 dict (RoomDetector 走 panel.WallHeight 全局单值).
        ///   - 复式 (IsMultiFloor=true): key=楼层别名 (e.g. "一楼", "二楼"), value=墙高 mm (NumericUpDown Value).
        ///     Commands 进一步传给 RoomDetector, RoomDetector 创建 Room 时按 finalFloor 查找, miss 走全局 fallback.
        /// </summary>
        public Dictionary<string, double> GetFloorWallHeights()
        {
            var map = new Dictionary<string, double>();
            if (_chkMultiFloor != null && _chkMultiFloor.Checked)
            {
                foreach (var kv in _multiFloorWallHeightCombos)
                    map[kv.Key] = (double)kv.Value.Value;
            }
            // 单楼层模式下 返空 dict — RoomDetector 走 全局 panel.WallHeight
            return map;
        }

        // ============== 🔧 v20 跨实例 UI 记忆 ==============
        // 同 DLL 同存 user-overrides.json (e.g. BaoJiaCAD/bin/Debug/net48/user-overrides.json).
        // Save: btnStart.Click 时 序列 当前 UI. Load: ctor 末尾 读 + 静默 默认 覆盖.
        //   - 文件 缺失 / 损坏 / schema 不 合 → 静默 默认 (不 报 错).
        //   - 项目名 (ProjectName) 不 存 — 仍 按 dwgName 重 填.

        private static string _userOverridesPathCache;
        private static string GetUserOverridesPath()
        {
            if (_userOverridesPathCache != null) return _userOverridesPathCache;
            string dllDir;
            try { dllDir = Path.GetDirectoryName(typeof(QuotePanel).Assembly.Location) ?? ""; }
            catch { dllDir = ""; }
            _userOverridesPathCache = Path.Combine(dllDir, "user-overrides.json");
            return _userOverridesPathCache;
        }

        /// <summary>
        /// 🔧 v22 「恢复默认」实现 — 删 user-overrides.json + in-memory UI state 重 写 为 config.json 默认值。
        ///   - 不动 InitializeComponent (不重建 控件), 避免 重复 添加 Items 双 拷贝。
        ///   - 仅 设 值 — NumericUpDown.Value / CheckBox.Checked / ComboBox.SelectedIndex。
        ///   - 复式 开关 一 律 关, _lastPerFloorHeights 缓存 清, 让 用户 重新 选 复式 后 rebuild 拿 默认。
        /// </summary>
        private void ResetToDefaults()
        {
            // 1. 删 user-overrides.json — 静默 (文件 不 存 或 只 读 盘 都不 阻 挡)
            try
            {
                string path = GetUserOverridesPath();
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* silent */ }

            // 2. 工程名称 (重 设 回 dwgName; 空 时 用 默 "未命名工程" 占位)
            _txtProjectName.Text = string.IsNullOrWhiteSpace(_dwgName) ? "未命名工程" : _dwgName;

            // 3. 总 墙高 → config.DefaultWallHeight
            try { _numWallHeight.Value = (decimal)(_config?.DefaultWallHeight ?? 2800.0); }
            catch { }

            // 4. 复式 开关 (默认 关) + 层数 = 2 — 不 调 CheckedChanged handler 主动 设 false 也 可, handler 会 自己 跑 隐藏 _tlpMultiSpec
            if (_chkMultiFloor.Checked)
            {
                _chkMultiFloor.Checked = false;
                // CheckedChanged handler 会 隐藏 _tlpMultiSpec / 重 算 form 宽. 不 需 手动.
            }
            try { _numFloorCount.Value = 2; } catch { }

            // 5. 单层 模板 下拉 重 选 config.TemplateSettings.ActiveTemplate (index 0 fallback)
            string activeTpl = _config?.TemplateSettings?.ActiveTemplate ?? "dizhuan";
            int tplIdx = _cmbTemplate.Items.IndexOf(activeTpl);
            if (tplIdx >= 0) _cmbTemplate.SelectedIndex = tplIdx;

            // 6. 防水参数 — 8 个 NumericUpDown 重 写 config.BathroomKitchenDefaults 对 应 值
            var defs = _config?.BathroomKitchenDefaults;
            if (defs != null)
            {
                TryResetNumeric(_numBathWaterproof, defs.WaterproofHeight);
                TryResetNumeric(_numTileHeight, defs.TileHeight);
                TryResetNumeric(_numDoorDeduct, defs.DefaultDoorDeduct);
                TryResetNumeric(_numWindowDeduct, defs.DefaultWindowDeduct);
                TryResetNumeric(_numKitchenWaterproof, defs.KitchenWaterproofHeight);
                TryResetNumeric(_numBalconyWaterproof, defs.BalconyWaterproofHeight);
                TryResetNumeric(_numGardenRoll, defs.OutdoorGardenRollHeight);
                TryResetNumeric(_numGardenNonRoll, defs.OutdoorGardenNonRollHeight);
            }

            // 7. 单层 spec ComboBox 重 选 默认 (复用 BuildTileSpecSection 中 cb 设置 默认 逻辑:
            //      - 1F 用 isDefault+0 (NONE prepended → +1); 失配 → 走 NONE / index 1)
            foreach (var kv in _tileSpecCombos)
            {
                string roomType = kv.Key;
                List<TileSpecOption> list = _specsDictCache?[roomType];
                int defaultSpecIdx = -1;
                if (list != null)
                {
                    for (int s = 0; s < list.Count; s++)
                        if (list[s] != null && list[s].IsDefault) { defaultSpecIdx = s; break; }
                }
                defaultSpecIdx = defaultSpecIdx >= 0 ? defaultSpecIdx : 0;
                int idx = 1 + defaultSpecIdx;   // 0=NONE, 1=list[0]
                if (idx >= 0 && idx < kv.Value.Items.Count) kv.Value.SelectedIndex = idx;
            }

            // 8. 清 v19.1 _lastPerFloorHeights 跨-rebuild 缓存 — 避免 dirty 数据 残留, 下 次 re-toggle multi-floor rebuild 用 默认。
            _lastPerFloorHeights.Clear();
        }

        /// <summary>v22 辅助 — 重 置 NumericUpDown 为 给定 double 值, 越界 静默 跳过。</summary>
        private static void TryResetNumeric(NumericUpDown nud, double v)
        {
            try
            {
                if (v < (double)nud.Minimum || v > (double)nud.Maximum) return;
                nud.Value = (decimal)v;
            }
            catch { }
        }

        /// <summary>读 上次 OK 点 保存 的 UI 状态 并 覆盖 当前 默认。</summary>
        private void ApplyUserOverrides()
        {
            UserUIOverrides ov = null;
            try
            {
                string path = GetUserOverridesPath();
                if (!File.Exists(path)) return;
                ov = JsonConvert.DeserializeObject<UserUIOverrides>(File.ReadAllText(path));
                if (ov == null || ov.SchemaVersion != UserUIOverrides.CurrentSchemaVersion) return;
            }
            catch { return; }   // 静默 — 损坏 / 类型 错 / DesignException

            // Phase 1: 简单 字段 — NumericUpDown / 单层 模板 ComboBox
            //   ⚠ _chkMultiFloor.Checked 不 调 → 不 trigger CheckedChanged → RebuildMultiFloorGrid 不 跑
            //   所以 保 global _numWallHeight 默认 (用 上 调 _numWallHeight) 为 未来 multi 默认 起点
            ApplyNumericIfInRange(_numWallHeight, ov.WallHeight);
            if (ov.FloorCount >= (int)_numFloorCount.Minimum && ov.FloorCount <= (int)_numFloorCount.Maximum)
                ApplyNumericIfInRange(_numFloorCount, ov.FloorCount);
            ApplyComboByItemText(_cmbTemplate, ov.SelectedTemplate);
            ApplyNumericIfInRange(_numBathWaterproof, ov.BathWaterproofHeight);
            ApplyNumericIfInRange(_numTileHeight, ov.TileHeight);
            ApplyNumericIfInRange(_numDoorDeduct, ov.DoorDeduct);
            ApplyNumericIfInRange(_numWindowDeduct, ov.WindowDeduct);
            ApplyNumericIfInRange(_numKitchenWaterproof, ov.KitchenWaterproofHeight);
            ApplyNumericIfInRange(_numBalconyWaterproof, ov.BalconyWaterproofHeight);
            ApplyNumericIfInRange(_numGardenRoll, ov.GardenRollHeight);
            ApplyNumericIfInRange(_numGardenNonRoll, ov.GardenNonRollHeight);

            // 单层 spec Pull: ov.SpecSelections 中 key 为 ""|RoomType (Phase 1 完成)
            foreach (var kv in ov.SpecSelections ?? new Dictionary<string, string>())
            {
                string[] parts = (kv.Key ?? "").Split('|');
                if (parts.Length != 2) continue;
                if (string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
                {
                    if (_tileSpecCombos.TryGetValue(parts[1], out var cb))
                        ApplyComboByValueOrLabel(cb, kv.Value);
                }
            }

            // Phase 2: 复式 — 调 CheckedChanged → RebuildMultiFloorGrid 同步 跑; 完成后 找回 上次 每列 值
            if (ov.IsMultiFloor && !_chkMultiFloor.Checked)
                _chkMultiFloor.Checked = true;   // 触发 CheckedChanged sync rebuild (同步)
            // 🔧 v20.1 修复: 独立 Patch 区 — 无论 是否 只 才 才 toggle, 只要 ov.IsMultiFloor 就 走 patch。
            //   此前 patch 藏 在 trigger 内, 启动时 _chkMultiFloor.Checked 已 true (隐含 ctor) 会 静默 跳过。
            if (ov.IsMultiFloor)
            {
                // 现在 _multiFloorCombos / _multiFloorTemplateCombos / _multiFloorWallHeightCombos 都 装 上 了
                foreach (var kv in ov.SpecSelections ?? new Dictionary<string, string>())
                {
                    string[] parts = (kv.Key ?? "").Split('|');
                    if (parts.Length != 2) continue;
                    if (!string.IsNullOrEmpty(parts[0]) && !string.IsNullOrEmpty(parts[1]))
                    {
                        if (_multiFloorCombos.TryGetValue((parts[0], parts[1]), out var cb))
                            ApplyComboByValueOrLabel(cb, kv.Value);
                    }
                }
                foreach (var kv in ov.MultiFloorTemplates ?? new Dictionary<string, string>())
                    if (_multiFloorTemplateCombos.TryGetValue(kv.Key, out var cb))
                        ApplyComboByItemText(cb, kv.Value);
                foreach (var kv in ov.MultiFloorWallHeights ?? new Dictionary<string, double>())
                    if (_multiFloorWallHeightCombos.TryGetValue(kv.Key, out var nud))
                        ApplyNumericIfInRange(nud, kv.Value);
            }
        }

        /// <summary>OK 点 后 抓 当前面板 UI 状态 序列 化 为 user-overrides.json。</summary>
        private void SaveUserOverrides()
        {
            try
            {
                bool isMulti = _chkMultiFloor != null && _chkMultiFloor.Checked;
                var ov = new UserUIOverrides
                {
                    SchemaVersion = UserUIOverrides.CurrentSchemaVersion,
                    WallHeight = WallHeight,
                    IsMultiFloor = isMulti,
                    FloorCount = FloorCount,
                    SelectedTemplate = SelectedTemplate,
                    BathWaterproofHeight = BathWaterproofHeight,
                    TileHeight = TileHeight,
                    DoorDeduct = DoorDeduct,
                    WindowDeduct = WindowDeduct,
                    KitchenWaterproofHeight = KitchenWaterproofHeight,
                    BalconyWaterproofHeight = BalconyWaterproofHeight,
                    GardenRollHeight = OutdoorGardenRollHeight,
                    GardenNonRollHeight = OutdoorGardenNonRollHeight,
                };
                // spec selections — 统一编码 「Floor|Room」(单层 空 floor 前缀)，
                //   与 Commands 运行时SelectedTileSpecs 完全 兼容 (Commands 那里 “|{Room}” 兑底走 k2 也 看到)
                var specs = GetTileSpecSelections(); // 🔧 v20.1: 删 死 三 元 — 两 arts 同 返。
                if (!isMulti)
                {
                    var renamed = new Dictionary<string, string>();
                    foreach (var kv in specs) renamed[$"|{kv.Key}"] = kv.Value;
                    ov.SpecSelections = renamed;
                }
                else
                {
                    ov.SpecSelections = specs;   // 复式 已是 「一楼|客餐厅」 等
                }
                ov.MultiFloorTemplates = GetFloorTemplateSelections();
                ov.MultiFloorWallHeights = GetFloorWallHeights();

                File.WriteAllText(GetUserOverridesPath(), JsonConvert.SerializeObject(ov, Formatting.Indented));
            }
            catch { /* silent — 写 盘 失败 不应 阻 BJ 主流程 */ }
        }

        private static void ApplyNumericIfInRange(NumericUpDown nud, double v)
        {
            try
            {
                if (v < (double)nud.Minimum || v > (double)nud.Maximum) return;
                nud.Value = (decimal)v;
            }
            catch { }
        }

        private static void ApplyComboByItemText(ComboBox cb, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            int idx = cb.Items.IndexOf(text);
            if (idx >= 0) cb.SelectedIndex = idx;
        }

        /// <summary>匹配 spec ComboBox 中 Value 等 于 targetValue 的 item; 失配 回 退 匹配 text (NONE 项 使用 Value "&lt;NONE&gt;" 检测)。</summary>
        private static void ApplyComboByValueOrLabel(ComboBox cb, string targetValue)
        {
            if (string.IsNullOrEmpty(targetValue)) return;
            foreach (object item in cb.Items)
            {
                if (item is TileSpecOption opt && opt.Value == targetValue)
                {
                    int idx = cb.Items.IndexOf(opt);
                    if (idx >= 0) { cb.SelectedIndex = idx; return; }
                }
            }
            int tidx = cb.Items.IndexOf(targetValue);
            if (tidx >= 0) cb.SelectedIndex = tidx;
        }
    }

    /// <summary>🔧 v20 用户 UI 上次输入状态 — user-overrides.json DTO。与 config.json 并行, user-specific, 跨项目 一致。</summary>
    public class UserUIOverrides
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public double WallHeight { get; set; }
        public bool IsMultiFloor { get; set; }
        public int FloorCount { get; set; }
        public string SelectedTemplate { get; set; }

        /// <summary>spec selections — 统一 「Floor|Room」 编码 (单层 空 floor 前缀)。</summary>
        public Dictionary<string, string> SpecSelections { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, string> MultiFloorTemplates { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, double> MultiFloorWallHeights { get; set; } = new Dictionary<string, double>();

        public double BathWaterproofHeight { get; set; }
        public double TileHeight { get; set; }
        public double DoorDeduct { get; set; }
        public double WindowDeduct { get; set; }
        public double KitchenWaterproofHeight { get; set; }
        public double BalconyWaterproofHeight { get; set; }
        public double GardenRollHeight { get; set; }
        public double GardenNonRollHeight { get; set; }
    }
}
