using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

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
        // 🔧 specs cache — BuildTileSpecSection 填充, RebuildMultiFloorGrid 重建时读取
        private List<string> _specRoomTypesCache;
        private Dictionary<string, List<TileSpecOption>> _specsDictCache;
        // 🔧 v7 模板列表 cache — BuildTileSpecSection 填充, RebuildMultiFloorGrid 后用
        private List<string> _templatesCache;
        // 🔧 v12: UI 层 隐藏 模板 key 列表 — 不加 到 _cmbTemplate / 多层 grid 每层 下拉项. config.Templates 字典 仍 完整 (优秀 但用户用不到 的模板 隐藏在 UI). 客户用不到 → 隐藏之. 动 config.json Templates 词书 不动 = 仅 UI 隐藏.
        private static readonly HashSet<string> _uiHiddenTemplateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fushi", "zhubaojiao"
        };

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
            InitializeComponent();
            BuildTileSpecSection(config);   // 🔧 根据配置动态生成规格下拉 (在 InitializeComponent 之后, 在 ApplyConfig 之前)
            ApplyConfig(config, dwgName);
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
                foreach (var spec in list)
                    cb.Items.Add(spec);
                cb.DisplayMember = nameof(TileSpecOption.Label);
                int defaultIdx = -1;
                for (int s = 0; s < list.Count; s++)
                {
                    if (list[s] is TileSpecOption opt2 && opt2.IsDefault)
                    {
                        defaultIdx = s;
                        break;
                    }
                }
                cb.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;

                _tileSpecCombos[roomType] = cb;
                _pnlSingleSpec.Controls.Add(lbl);
                _pnlSingleSpec.Controls.Add(cb);
            }
            this.Controls.Add(_pnlSingleSpec);

            // ── 多楼层 TableLayoutPanel (默认隐藏) ──
            _tlpMultiSpec = new TableLayoutPanel
            {
                Location = new Point(20, originalBtnTop),
                Size = new Size(840, multiHeightNeeded),
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
            _tlpMultiSpec.SuspendLayout();
            _tlpMultiSpec.Controls.Clear();
            _multiFloorCombos.Clear();
            _multiFloorTemplateCombos.Clear();

            int cols = _specRoomTypesCache.Count + 2;     // v7: +1 模板 +1 别名
            int rows = floorCount + 1;                    // +1 列表头行
            _tlpMultiSpec.ColumnCount = cols;
            _tlpMultiSpec.RowCount = rows;

            // 列宽: col 0=130 模板, col 1=60 别名, 余 110 规格列
            _tlpMultiSpec.ColumnStyles.Clear();
            _tlpMultiSpec.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
            _tlpMultiSpec.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60F));
            for (int c = 2; c < cols; c++)
                _tlpMultiSpec.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
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
                    cb.SelectedIndex = defaultIdx;

                    cells[r, c] = cb;
                    _multiFloorCombos[(floorAlias, roomType)] = cb;
                    _tlpMultiSpec.Controls.Add(cb, c + 2, r + 1);
                }
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
                Minimum = 2000, Maximum = 5000, Value = 2800, Increment = 100
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
                this.Width = mf ? 880 : 440;
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
                this.DialogResult = DialogResult.OK;
                this.Close();
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
                _btnStart, _btnCancel,
            });

            this.AcceptButton = _btnStart;
            this.CancelButton = _btnCancel;
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
    }
}
