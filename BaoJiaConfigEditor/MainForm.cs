using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace BaoJiaCAD.ConfigEditor
{
    /// <summary>
    /// BaoJiaCAD config.json 独立编辑器. Designer-free WinForms (单文件).
    /// - 启动: 自动定位 BaoJiaCAD/config.json (项目根) + BaoJiaCAD/bin/Debug/net48/config.json (运行时实读).
    /// - 保存: 双写两份 + 时间戳备份 + round-trip 验证.
    /// - 9 个 Tab 覆盖 config 的全部 ~15 个子 section.
    /// </summary>
    public class MainForm : Form
    {
        // ── config 双份路径 ──
        private string _projectRoot;       // BaoJiaCAD.sln 所在目录
        private string _srcPath;           // BaoJiaCAD/config.json (项目根, git-tracked)
        private string _binPath;           // BaoJiaCAD/bin/Debug/net48/config.json (运行时实读)

        // ── 内存中的配置 ──
        private QuoteConfig _cfg;

        // ── TabControl + 9 个 TabPage ──
        private TabControl _tabs;
        private TabPage _tabCompany;
        private TabPage _tabDefaults;
        private TabPage _tabRoomTypeMap;
        private TabPage _tabKeywords;
        private TabPage _tabFallbackMap;
        private TabPage _tabTemplates;
        private TabPage _tabFloorAlias;
        private TabPage _tabTileSpec;
        private TabPage _tabQuoteItems;

        // ── Tab 1 公司抬头 ──
        private TextBox _txtCompanyName;
        private TextBox _txtCompanyAddress;

        // ── Tab 2 默认墙高 + 厨卫参数 (10 个数值) ──
        private NumericUpDown _numWallHeight;
        private NumericUpDown _numTileHeight;
        private NumericUpDown _numWaterproofHeight;
        private NumericUpDown _numKitchenWaterproof;
        private NumericUpDown _numBalconyWaterproof;
        private NumericUpDown _numGardenRoll;
        private NumericUpDown _numGardenNonRoll;
        private NumericUpDown _numDoorDeduct;
        private NumericUpDown _numWindowDeduct;
        private NumericUpDown _numSumpPit;
        private NumericUpDown _numFlowerPool;

        // ── Tab 3 房型识别关键词 ──
        private DataGridView _dgvRoomTypeMap;

        // ── Tab 4 自动填数量关键词 (2 个 ListBox) ──
        private TextBox _txtFloorKwInput;
        private Button _btnFloorKwAdd;
        private Button _btnFloorKwDel;
        private ListBox _lstFloorKws;
        private TextBox _txtWallKwInput;
        private Button _btnWallKwAdd;
        private Button _btnWallKwDel;
        private ListBox _lstWallKws;

        // ── Tab 5 房型回退映射 ──
        private DataGridView _dgvFallbackMap;

        // ── Tab 6 模板设置 ──
        private TextBox _txtTplFolder;
        private ComboBox _cmbActiveTpl;
        private DataGridView _dgvTplFiles;

        // ── Tab 7 楼层别名 ──
        private DataGridView _dgvFloorAlias;

        // ── Tab 8 瓷砖规格 ──
        private ComboBox _cmbSpecRoomType;
        private Button _btnSpecAdd;
        private Button _btnSpecDel;
        private DataGridView _dgvTileSpec;

        // ── Tab 9 报价项目 ──
        private Button _btnQuoteItemAdd;
        private Button _btnQuoteItemDel;
        private DataGridView _dgvQuoteItems;

        // ── 底部按钮 ──
        private Label _lblPath;
        private Button _btnReload;
        private Button _btnReset;
        private Button _btnSave;
        private Button _btnQuit;

        // ── 6 大类 (用于房型相关 dropdown) ──
        private static readonly string[] SixCats = { "客餐厅", "厨房", "卫生间", "主卧", "主卫", "卧室", "阳台", "外花园" };
        // ── CalcRule dropdown 选项 (允许自由输入) ──
        private static readonly string[] CalcRules = { "Floor", "CeilingAndWall" };

        // 房间类型 panel 始终在重置后保持 RoomTypeMaps.Default 顺序?
        // 不需要 — 我们就是直接读 + 写 config, 不排序 (保留用户在 config.json 里的顺序).

        public MainForm()
        {
            Text = "BaoJiaCAD 配置编辑";
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(1180, 760);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(960, 640);
            Font = new Font("Microsoft YaHei", 9F);

            BuildTabs();
            BuildFooter();

            LocateConfigFiles();
            LoadConfig();
        }

        // ══════════════════════════════════════════════
        //  TabControl + Tab 页面构建
        // ══════════════════════════════════════════════

        private void BuildTabs()
        {
            _tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
            };

            _tabCompany = new TabPage("🏢 公司抬头");
            _tabDefaults = new TabPage("📏 默认参数 & 厨卫防水");
            _tabRoomTypeMap = new TabPage("🔍 房型识别关键词");
            _tabKeywords = new TabPage("🧱 自动填数量关键词");
            _tabFallbackMap = new TabPage("🚪 房型回退映射");
            _tabTemplates = new TabPage("🏛️ 模板设置");
            _tabFloorAlias = new TabPage("🔢 楼层别名");
            _tabTileSpec = new TabPage("🏷️ 瓷砖规格");
            _tabQuoteItems = new TabPage("💰 报价项目");

            BuildCompanyTab(_tabCompany);
            BuildDefaultsTab(_tabDefaults);
            BuildRoomTypeMapTab(_tabRoomTypeMap);
            BuildKeywordsTab(_tabKeywords);
            BuildFallbackMapTab(_tabFallbackMap);
            BuildTemplatesTab(_tabTemplates);
            BuildFloorAliasTab(_tabFloorAlias);
            BuildTileSpecTab(_tabTileSpec);
            BuildQuoteItemsTab(_tabQuoteItems);

            _tabs.TabPages.AddRange(new TabPage[] {
                _tabCompany, _tabDefaults, _tabRoomTypeMap, _tabKeywords,
                _tabFallbackMap, _tabTemplates, _tabFloorAlias, _tabTileSpec,
                _tabQuoteItems,
            });
            Controls.Add(_tabs);
        }

        private void BuildFooter()
        {
            var pnlFooter = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 60,
                Padding = new Padding(8),
            };

            _lblPath = new Label
            {
                Dock = DockStyle.Top,
                Height = 24,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
            };

            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 32,
                FlowDirection = FlowDirection.RightToLeft,
            };

            _btnQuit = new Button { Text = "退出", Width = 80, Height = 28 };
            _btnQuit.Click += (s, e) => Close();
            _btnSave = new Button
            {
                Text = "💾 保存 (双写)", Width = 130, Height = 28,
                BackColor = Color.FromArgb(0, 122, 204),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold),
            };
            _btnSave.FlatAppearance.BorderSize = 0;
            _btnSave.Click += BtnSave_Click;
            _btnReset = new Button { Text = "重置为默认", Width = 100, Height = 28 };
            _btnReset.Click += BtnReset_Click;
            _btnReload = new Button { Text = "🔄 重新加载", Width = 100, Height = 28 };
            _btnReload.Click += BtnReload_Click;

            row.Controls.AddRange(new Control[] { _btnQuit, _btnSave, _btnReset, _btnReload });
            pnlFooter.Controls.Add(row);
            pnlFooter.Controls.Add(_lblPath);
            Controls.Add(pnlFooter);
        }

        // ────────────────────────────────────────────
        //  Tab 1 公司抬头
        // ────────────────────────────────────────────
        private void BuildCompanyTab(TabPage tab)
        {
            var pnl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(20, 20, 20, 20),
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));

            pnl.Controls.Add(new Label
            {
                Text = "公司抬头：",
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Fill,
            }, 0, 0);
            _txtCompanyName = new TextBox { Dock = DockStyle.Fill, MaxLength = 80 };
            pnl.Controls.Add(_txtCompanyName, 1, 0);

            pnl.Controls.Add(new Label
            {
                Text = "公司地址 + 电话：",
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Fill,
            }, 0, 1);
            _txtCompanyAddress = new TextBox { Dock = DockStyle.Fill, MaxLength = 200 };
            pnl.Controls.Add(_txtCompanyAddress, 1, 1);

            var hint = new Label
            {
                Text = "提示：这 2 个字段会出现在每个报价单顶部。",
                ForeColor = SystemColors.GrayText,
                AutoSize = true,
                Padding = new Padding(4),
            };
            pnl.Controls.Add(hint, 1, 2);

            tab.Controls.Add(pnl);
        }

        // ────────────────────────────────────────────
        //  Tab 2 默认墙高 + 厨卫/防水参数 (10 个数值, 2 列布局)
        // ────────────────────────────────────────────
        private void BuildDefaultsTab(TabPage tab)
        {
            var pnl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                Padding = new Padding(20),
                AutoScroll = true,
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25F));

            // 行 0: 默认墙高  (mm)
            _numWallHeight = MakeNumeric(2800M, 2000M, 5000M, 50M, 0, "mm");
            AddRow(pnl, 0, "默认墙高", _numWallHeight, "(mm)");

            // 行 1: DefaultDoorDeduct + DefaultWindowDeduct
            _numDoorDeduct = MakeNumeric(1.4M, 0M, 10M, 0.1M, 1, "㎡");
            _numWindowDeduct = MakeNumeric(0.4M, 0M, 10M, 0.1M, 1, "㎡");
            AddRow(pnl, 1, "门洞扣减", _numDoorDeduct, "窗洞扣减", _numWindowDeduct, "㎡");

            // 行 2: TileHeight + WaterproofHeight
            _numTileHeight = MakeNumeric(2.4M, 0M, 5M, 0.1M, 1, "m");
            _numWaterproofHeight = MakeNumeric(2.0M, 0M, 5M, 0.1M, 1, "m");
            AddRow(pnl, 2, "贴砖高度", _numTileHeight, "卫生间防水高", _numWaterproofHeight, "m");

            // 行 3: Kitchen + Balcony 防水高
            _numKitchenWaterproof = MakeNumeric(0.6M, 0M, 5M, 0.1M, 1, "m");
            _numBalconyWaterproof = MakeNumeric(0.6M, 0M, 5M, 0.1M, 1, "m");
            AddRow(pnl, 3, "厨房防水高", _numKitchenWaterproof, "阳台防水高", _numBalconyWaterproof, "m");

            // 行 4: Garden Roll + Non-Roll
            _numGardenRoll = MakeNumeric(0.8M, 0M, 5M, 0.1M, 1, "m");
            _numGardenNonRoll = MakeNumeric(0.6M, 0M, 5M, 0.1M, 1, "m");
            AddRow(pnl, 4, "外花园卷材高", _numGardenRoll, "外花园非卷材高", _numGardenNonRoll, "m");

            // 行 5: SumpPit + FlowerPool
            _numSumpPit = MakeNumeric(0.0M, 0M, 5M, 0.1M, 1, "m");
            _numFlowerPool = MakeNumeric(0.0M, 0M, 5M, 0.1M, 1, "m");
            AddRow(pnl, 5, "沉箱架空高", _numSumpPit, "花池高", _numFlowerPool, "m");

            tab.Controls.Add(pnl);
        }

        private static NumericUpDown MakeNumeric(decimal val, decimal min, decimal max, decimal inc, int decimals = 1, string unit = "")
        {
            var n = new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                Increment = inc,
                DecimalPlaces = decimals,
                Dock = DockStyle.Fill,
            };
            if (!string.IsNullOrEmpty(unit))
                n.Tag = unit;  // 单位在 Row 添加函数里单独 Lbl 显示
            return n;
        }

        // Layout helper: 2-cell row with 文案+数值, 右侧跟随单位提示
        private static void AddRow(TableLayoutPanel pnl, int rowIdx, string lbl1, NumericUpDown num1, string unit)
        {
            pnl.Controls.Add(new Label
            {
                Text = lbl1 + "：",
                TextAlign = ContentAlignment.MiddleRight,
                Dock = DockStyle.Fill,
            }, 0, rowIdx);
            pnl.Controls.Add(num1, 1, rowIdx);
            pnl.Controls.Add(new Label
            {
                Text = unit,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                ForeColor = SystemColors.GrayText,
            }, 2, rowIdx);
            pnl.Controls.Add(new Label(), 3, rowIdx);
        }
        private static void AddRow(TableLayoutPanel pnl, int rowIdx, string lbl1, NumericUpDown num1, string lbl2, NumericUpDown num2, string unit)
        {
            pnl.Controls.Add(new Label { Text = lbl1 + "：", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, 0, rowIdx);
            pnl.Controls.Add(num1, 1, rowIdx);
            pnl.Controls.Add(new Label { Text = lbl2 + "：", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, 2, rowIdx);
            pnl.Controls.Add(new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = false }
                ._Add(num2, new Label { Text = unit, AutoSize = false, ForeColor = SystemColors.GrayText, Margin = new Padding(4, 6, 0, 0) }), 3, rowIdx);
        }

        // ────────────────────────────────────────────
        //  Tab 3 房型识别关键词 RoomTypeMaps (DataGridView)
        // ────────────────────────────────────────────
        private void BuildRoomTypeMapTab(TabPage tab)
        {
            var toolbar = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                Padding = new Padding(8, 4, 8, 4),
            };
            var btnAdd = new Button { Text = "+ 新增房型", Width = 100, Height = 26 };
            var btnDel = new Button { Text = "- 删除选中", Width = 100, Height = 26 };
            btnAdd.Click += (s, e) =>
            {
                _dgvRoomTypeMap.Rows.Add("房间1, 房间2", SixCats[0]);
            };
            btnDel.Click += (s, e) =>
            {
                foreach (DataGridViewRow r in _dgvRoomTypeMap.SelectedRows)
                    if (!r.IsNewRow) _dgvRoomTypeMap.Rows.Remove(r);
            };
            toolbar.Controls.AddRange(new Control[] { btnAdd, btnDel, new Label { Text = "（房型命中: CAD 文字 → RoomType 映射; 关键词用 | 或英文逗号分隔, 留空即为「无关键词」）", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 8, 0, 0) } });

            _dgvRoomTypeMap = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                RowHeadersVisible = false,
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(248, 250, 252) },
            };
            var colKw = new DataGridViewTextBoxColumn
            {
                HeaderText = "关键词 (| 或 英文逗号 分隔)",
                Name = "Keywords",
                FillWeight = 70,
            };
            var colRt = new DataGridViewComboBoxColumn
            {
                HeaderText = "RoomType",
                Name = "RoomType",
                FillWeight = 30,
                FlatStyle = FlatStyle.Flat,
            };
            colRt.Items.AddRange(SixCats);
            _dgvRoomTypeMap.Columns.AddRange(new DataGridViewColumn[] { colKw, colRt });
            _dgvRoomTypeMap.DataError += (s, e) => { /* 静默 — 用户清空单元格的异常不打扰 */ e.ThrowException = false; };

            tab.Controls.Add(_dgvRoomTypeMap);
            tab.Controls.Add(toolbar);
        }

        // ────────────────────────────────────────────
        //  Tab 4 自动填数量关键词 (2 个 ListBox, 各 + Add/Del 按钮)
        // ────────────────────────────────────────────
        private void BuildKeywordsTab(TabPage tab)
        {
            var pnl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                Padding = new Padding(12),
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));

            pnl.Controls.Add(BuildSingleKeywordPanel("地面类自动填 (项目名含这些词 → 数量 = 地面面积)", out _txtFloorKwInput, out _btnFloorKwAdd, out _btnFloorKwDel, out _lstFloorKws), 0, 0);
            pnl.Controls.Add(BuildSingleKeywordPanel("墙面类自动填 (项目名含这些词 → 数量 = 墙顶面积)", out _txtWallKwInput, out _btnWallKwAdd, out _btnWallKwDel, out _lstWallKws), 1, 0);

            tab.Controls.Add(pnl);
        }

        private Panel BuildSingleKeywordPanel(string title, out TextBox txtInput, out Button btnAdd, out Button btnDel, out ListBox lst)
        {
            var p = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var lbl = new Label { Text = title, Dock = DockStyle.Top, Height = 28, Font = new Font("Microsoft YaHei", 9F, FontStyle.Bold), ForeColor = Color.FromArgb(0, 90, 158), TextAlign = ContentAlignment.MiddleLeft };
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 4, 0, 0) };

            // 🔧 用 locals 再 add handlers — 不能 lambda 捕获 out 参数 (CS1628), 故先 copy 到 local var
            var txt = new TextBox { Width = 220, Height = 22 };
            var addBtn = new Button { Text = "添加", Width = 60, Height = 24 };
            var delBtn = new Button { Text = "删除", Width = 60, Height = 24 };
            var list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };

            addBtn.Click += (s, e) =>
            {
                string kw = txt.Text?.Trim();
                if (!string.IsNullOrEmpty(kw) && !list.Items.Contains(kw))
                {
                    list.Items.Add(kw);
                    txt.Clear();
                }
            };
            delBtn.Click += (s, e) =>
            {
                while (list.SelectedIndex >= 0)
                {
                    list.Items.RemoveAt(list.SelectedIndex);
                }
            };

            row.Controls.AddRange(new Control[] { txt, addBtn, delBtn });
            p.Controls.Add(list);
            p.Controls.Add(row);
            p.Controls.Add(lbl);

            // out 参数最后赋值 (lambda 已绑定到 local, 不受影响)
            txtInput = txt;
            btnAdd = addBtn;
            btnDel = delBtn;
            lst = list;
            return p;
        }

        // ────────────────────────────────────────────
        //  Tab 5 房型回退映射 (Key=RoomType, Value=RoomType)
        // ────────────────────────────────────────────
        private void BuildFallbackMapTab(TabPage tab)
        {
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 4, 8, 4) };
            var btnAdd = new Button { Text = "+ 新增映射", Width = 100, Height = 26 };
            var btnDel = new Button { Text = "- 删除选中", Width = 100, Height = 26 };
            btnAdd.Click += (s, e) => _dgvFallbackMap.Rows.Add(SixCats[0], SixCats[1]);
            btnDel.Click += (s, e) =>
            {
                foreach (DataGridViewRow r in _dgvFallbackMap.SelectedRows)
                    if (!r.IsNewRow) _dgvFallbackMap.Rows.Remove(r);
            };
            toolbar.Controls.AddRange(new Control[] { btnAdd, btnDel, new Label { Text = "（房型不存在时, 回退到 fallback 房型找模板. 比如 阳台→客餐厅）", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 8, 0, 0) } });

            _dgvFallbackMap = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                RowHeadersVisible = false,
            };
            var colF1 = new DataGridViewComboBoxColumn { HeaderText = "源房型", Name = "Key", FillWeight = 50, FlatStyle = FlatStyle.Flat };
            var colF2 = new DataGridViewComboBoxColumn { HeaderText = "回退房型", Name = "Value", FillWeight = 50, FlatStyle = FlatStyle.Flat };
            colF1.Items.AddRange(SixCats);
            colF2.Items.AddRange(SixCats);
            _dgvFallbackMap.Columns.AddRange(new DataGridViewColumn[] { colF1, colF2 });
            _dgvFallbackMap.DataError += (s, e) => { e.ThrowException = false; };

            tab.Controls.Add(_dgvFallbackMap);
            tab.Controls.Add(toolbar);
        }

        // ────────────────────────────────────────────
        //  Tab 6 模板设置 Templates
        // ────────────────────────────────────────────
        private void BuildTemplatesTab(TabPage tab)
        {
            var pnl = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                Padding = new Padding(20),
            };
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            pnl.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

            // 行 0: 模板文件夹
            pnl.Controls.Add(new Label { Text = "模板文件夹：", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, 0, 0);
            _txtTplFolder = new TextBox { Dock = DockStyle.Fill, Multiline = false };
            pnl.Controls.Add(_txtTplFolder, 1, 0);
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            pnl.Controls.Add(new Label(), 2, 0);

            // 行 1: 激活模板默认
            pnl.Controls.Add(new Label { Text = "默认激活模板：", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, 0, 1);
            _cmbActiveTpl = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            pnl.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            pnl.Controls.Add(_cmbActiveTpl, 1, 1);
            pnl.Controls.Add(new Label(), 2, 1);

            // 行 2: 模板 key=文件映射
            pnl.Controls.Add(new Label { Text = "模板字典 (key=文件名)：", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Top }, 0, 2);
            pnl.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            _dgvTplFiles = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                RowHeadersVisible = false,
            };
            _dgvTplFiles.Columns.AddRange(new DataGridViewColumn[] {
                new DataGridViewTextBoxColumn { HeaderText = "key (如 dizhuan)", Name = "Key", FillWeight = 30 },
                new DataGridViewTextBoxColumn { HeaderText = "filename (如 dizhuan.xlsx)", Name = "File", FillWeight = 70 },
            });
            _dgvTplFiles.DataError += (s, e) => { e.ThrowException = false; };
            pnl.Controls.Add(_dgvTplFiles, 1, 2);
            pnl.SetColumnSpan(_dgvTplFiles, 2);

            tab.Controls.Add(pnl);
        }

        // ────────────────────────────────────────────
        //  Tab 7 楼层别名 FloorAliasMap
        // ────────────────────────────────────────────
        private void BuildFloorAliasTab(TabPage tab)
        {
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 4, 8, 4) };
            var btnAdd = new Button { Text = "+ 新增", Width = 80, Height = 26 };
            var btnDel = new Button { Text = "- 删除", Width = 80, Height = 26 };
            btnAdd.Click += (s, e) => _dgvFloorAlias.Rows.Add("", "");
            btnDel.Click += (s, e) =>
            {
                foreach (DataGridViewRow r in _dgvFloorAlias.SelectedRows)
                    if (!r.IsNewRow) _dgvFloorAlias.Rows.Remove(r);
            };
            toolbar.Controls.AddRange(new Control[] { btnAdd, btnDel, new Label { Text = "（楼层原文 → 归一化别名. eg 1F→一楼, 一层→一楼, 首层→一楼）", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 8, 0, 0) } });

            _dgvFloorAlias = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                RowHeadersVisible = false,
            };
            _dgvFloorAlias.Columns.AddRange(new DataGridViewColumn[] {
                new DataGridViewTextBoxColumn { HeaderText = "原文 (1F / 一层 / 首层)", Name = "Raw", FillWeight = 50 },
                new DataGridViewTextBoxColumn { HeaderText = "归一化 (一楼)", Name = "Norm", FillWeight = 50 },
            });
            _dgvFloorAlias.DataError += (s, e) => { e.ThrowException = false; };

            tab.Controls.Add(_dgvFloorAlias);
            tab.Controls.Add(toolbar);
        }

        // ────────────────────────────────────────────
        //  Tab 8 瓷砖规格 TileSpecOptions (master-detail)
        //  上: ComboBox (roomType) + 增/删按钮
        //  下: DataGridView 列 (Label / Value / Match / IsDefault / MatPrice / LabPrice)
        // ────────────────────────────────────────────
        private void BuildTileSpecTab(TabPage tab)
        {
            var top = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 60,
                ColumnCount = 4,
                Padding = new Padding(8, 8, 8, 8),
            };
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));

            top.Controls.Add(new Label { Text = "房间类型：", TextAlign = ContentAlignment.MiddleRight, Dock = DockStyle.Fill }, 0, 0);
            _cmbSpecRoomType = new ComboBox
            {
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
            };
            top.Controls.Add(_cmbSpecRoomType, 1, 0);
            _btnSpecAdd = new Button { Text = "+ 新规格", Width = 80, Height = 26 };
            _btnSpecDel = new Button { Text = "- 删除", Width = 80, Height = 26 };
            top.Controls.Add(_btnSpecAdd, 2, 0);
            top.Controls.Add(_btnSpecDel, 3, 0);

            top.Controls.Add(new Label
            {
                Text = "提示：选中房间类型后, 下方是该房型的规格列表. Match 用 | 或 英文逗号 分隔子串, 全部命中才算匹配.",
                ForeColor = SystemColors.GrayText, AutoSize = true,
            }, 0, 1);
            top.SetColumnSpan((Control)top.Controls[top.Controls.Count - 1], 4);

            _dgvTileSpec = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                EditMode = DataGridViewEditMode.EditOnEnter,
                RowHeadersVisible = false,
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(248, 250, 252) },
            };
            _dgvTileSpec.Columns.AddRange(new DataGridViewColumn[] {
                new DataGridViewTextBoxColumn { HeaderText = "Label (面板显示)",  Name = "Label",  FillWeight = 30 },
                new DataGridViewTextBoxColumn { HeaderText = "Value (唯一键)",  Name = "Value",  FillWeight = 15 },
                new DataGridViewTextBoxColumn { HeaderText = "Match (| 或, 分隔)", Name = "Match",  FillWeight = 25 },
                new DataGridViewCheckBoxColumn { HeaderText = "默认",            Name = "IsDefault", FillWeight = 8 },
                new DataGridViewTextBoxColumn { HeaderText = "MaterialPrice",   Name = "MaterialPrice", FillWeight = 11,
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.##" } },
                new DataGridViewTextBoxColumn { HeaderText = "LaborPrice",      Name = "LaborPrice",    FillWeight = 11,
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.##" } },
            });
            _dgvTileSpec.DataError += (s, e) => { e.ThrowException = false; };

            _cmbSpecRoomType.SelectedIndexChanged += (s, e) => RefreshTileSpecGrid();
            _btnSpecAdd.Click += BtnSpecAdd_Click;
            _btnSpecDel.Click += BtnSpecDel_Click;

            tab.Controls.Add(_dgvTileSpec);
            tab.Controls.Add(top);
        }

        // ────────────────────────────────────────────
        //  Tab 9 报价项目 QuoteItems
        // ────────────────────────────────────────────
        private void BuildQuoteItemsTab(TabPage tab)
        {
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(8, 4, 8, 4) };
            _btnQuoteItemAdd = new Button { Text = "+ 新增项目", Width = 100, Height = 26 };
            _btnQuoteItemDel = new Button { Text = "- 删除选中", Width = 100, Height = 26 };
            _btnQuoteItemAdd.Click += (s, e) =>
            {
                _dgvQuoteItems.Rows.Add("新项目", "m²", 0, 0, "Floor", false, "");
            };
            _btnQuoteItemDel.Click += (s, e) =>
            {
                foreach (DataGridViewRow r in _dgvQuoteItems.SelectedRows)
                    if (!r.IsNewRow) _dgvQuoteItems.Rows.Remove(r);
            };
            toolbar.Controls.AddRange(new Control[] { _btnQuoteItemAdd, _btnQuoteItemDel, new Label { Text = "（CalcRule: Floor = 地面面积, CeilingAndWall = 地面+周长×墙高. 可手输入其他）", AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(20, 8, 0, 0) } });

            _dgvQuoteItems = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = true,
                AllowUserToDeleteRows = true,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = true,
                EditMode = DataGridViewEditMode.EditOnEnter,
                RowHeadersVisible = false,
                AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(248, 250, 252) },
            };
            var colCalcRule = new DataGridViewComboBoxColumn
            {
                HeaderText = "CalcRule",
                Name = "CalcRule",
                FillWeight = 18,
                FlatStyle = FlatStyle.Flat,
            };
            colCalcRule.Items.AddRange(CalcRules);
            _dgvQuoteItems.Columns.AddRange(new DataGridViewColumn[] {
                new DataGridViewTextBoxColumn { HeaderText = "Name",           Name = "Name",           FillWeight = 18 },
                new DataGridViewTextBoxColumn { HeaderText = "Unit",           Name = "Unit",           FillWeight = 6 },
                new DataGridViewTextBoxColumn { HeaderText = "MaterialPrice",  Name = "MaterialPrice",  FillWeight = 11,
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.##" } },
                new DataGridViewTextBoxColumn { HeaderText = "LaborPrice",     Name = "LaborPrice",     FillWeight = 11,
                    DefaultCellStyle = new DataGridViewCellStyle { Format = "0.##" } },
                colCalcRule,
                new DataGridViewCheckBoxColumn { HeaderText = "统计项", Name = "IsSummaryItem", FillWeight = 8 },
                new DataGridViewTextBoxColumn { HeaderText = "Description", Name = "Description", FillWeight = 28 },
            });
            _dgvQuoteItems.DataError += (s, e) => { e.ThrowException = false; };

            tab.Controls.Add(_dgvQuoteItems);
            tab.Controls.Add(toolbar);
        }

        // ══════════════════════════════════════════════
        //  IO: 定位 / 加载 / 保存 / 备份
        // ══════════════════════════════════════════════

        private void LocateConfigFiles()
        {
            // 从 exe 所在目录往上找, 直到发现 BaoJiaCAD.sln (标识这是 project root)
            DirectoryInfo dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\', '/'));
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "BaoJiaCAD.sln")))
                dir = dir.Parent;
            if (dir == null)
            {
                MessageBox.Show(
                    "找不到 BaoJiaCAD.sln, 请把 BaoJiaConfigEditor.exe 放到 BaoJiaCAD 项目内运行.\n" +
                    "推荐放置: E:\\xiangmu\\baojia\\BaoJiaConfigEditor\\bin\\Debug\\net48\\",
                    "找不到项目根", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _projectRoot = null;
                return;
            }
            _projectRoot = dir.FullName;
            _srcPath = Path.Combine(_projectRoot, "BaoJiaCAD", "config.json");
            _binPath = Path.Combine(_projectRoot, "BaoJiaCAD", "bin", "Debug", "net48", "config.json");
            _lblPath.Text = $"项目根: {_projectRoot}    ·    src: {Path.GetFileName(_srcPath)}    ·    bin: {Path.GetFileName(_binPath)}";
        }

        private void LoadConfig()
        {
            if (_projectRoot == null) return;
            string loadPath = File.Exists(_srcPath) ? _srcPath
                            : File.Exists(_binPath) ? _binPath
                            : null;
            if (loadPath == null)
            {
                MessageBox.Show(
                    $"未找到 config.json, 将用默认值初始化.\n期望路径:\n  {_srcPath}\n  {_binPath}",
                    "初次启动", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _cfg = QuoteConfig.CreateDefault();
            }
            else
            {
                try
                {
                    _cfg = QuoteConfig.Load(loadPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"加载失败: {ex.Message}\n\n来源文件: {loadPath}", "加载失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            ApplyConfigToUI();
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            // 先把 UI 值拷回 _cfg
            try { ApplyUIToConfig(); }
            catch (Exception ex)
            {
                MessageBox.Show($"读取面板值失败: {ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            try
            {
                // 备份现有两份 (如果存在)
                BackupFile(_srcPath);
                BackupFile(_binPath);

                // 写两份
                Directory.CreateDirectory(Path.GetDirectoryName(_srcPath));
                _cfg.Save(_srcPath);
                Directory.CreateDirectory(Path.GetDirectoryName(_binPath));
                _cfg.Save(_binPath);

                // Round-trip 验证: 重读两份, 检查关键计数
                var reload1 = QuoteConfig.Load(_srcPath);
                var reload2 = QuoteConfig.Load(_binPath);
                int c1 = reload1.RoomTypeMaps?.Count ?? 0;
                int c2 = reload2.RoomTypeMaps?.Count ?? 0;
                if (c1 != c2)
                    throw new InvalidDataException($"两份配置内容不一致: src={c1} 房型, bin={c2} 房型");

                MessageBox.Show(
                    $"✅ 已保存到 2 份文件:\n  • {_srcPath}\n  • {_binPath}\n\n" +
                    $"原始文件已备份为 config_bak_yyyyMMdd_HHmmss.json (同目录).\n\n" +
                    $"Round-trip 自检:\n  房型关键词: {reload1.RoomTypeMaps?.Count ?? 0} 条\n  默认墙高: {reload1.DefaultWallHeight} mm",
                    "保存成功", MessageBoxButtons.OK, MessageBoxIcon.Information);

                ApplyUIToConfig();  // 把刚保存的内容回灌 UI, 触发 transient consistency (eg 新增空 row 自动消失)
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存失败: {ex.Message}", "保存失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnReload_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("重新加载会丢弃当前面板上的所有修改, 确认?",
                "重新加载", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            LoadConfig();
        }

        private void BtnReset_Click(object sender, EventArgs e)
        {
            if (MessageBox.Show("确认要把所有 section 重置为代码默认?\n这会丢失当前面板的所有修改!",
                "重置为默认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            _cfg = QuoteConfig.CreateDefault();
            ApplyConfigToUI();
            MessageBox.Show("已重置为默认 (还没保存, 点 Save 才会写入磁盘).", "重置完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static void BackupFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string bak = Path.Combine(dir, $"{name}_bak_{ts}{ext}");
            File.Copy(path, bak, true);
        }

        // ══════════════════════════════════════════════
        //  Config ↔ UI 双向绑定
        // ══════════════════════════════════════════════

        private void ApplyConfigToUI()
        {
            // Tab 1
            _txtCompanyName.Text = _cfg.CompanyName ?? "";
            _txtCompanyAddress.Text = _cfg.CompanyAddress ?? "";

            // Tab 2
            _numWallHeight.Value = Clamp((decimal)_cfg.DefaultWallHeight, _numWallHeight);
            var defs = _cfg.BathroomKitchenDefaults ?? new BathroomKitchenDefaults();
            _numTileHeight.Value = Clamp((decimal)defs.TileHeight, _numTileHeight);
            _numWaterproofHeight.Value = Clamp((decimal)defs.WaterproofHeight, _numWaterproofHeight);
            _numKitchenWaterproof.Value = Clamp((decimal)defs.KitchenWaterproofHeight, _numKitchenWaterproof);
            _numBalconyWaterproof.Value = Clamp((decimal)defs.BalconyWaterproofHeight, _numBalconyWaterproof);
            _numGardenRoll.Value = Clamp((decimal)defs.OutdoorGardenRollHeight, _numGardenRoll);
            _numGardenNonRoll.Value = Clamp((decimal)defs.OutdoorGardenNonRollHeight, _numGardenNonRoll);
            _numDoorDeduct.Value = Clamp((decimal)defs.DefaultDoorDeduct, _numDoorDeduct);
            _numWindowDeduct.Value = Clamp((decimal)defs.DefaultWindowDeduct, _numWindowDeduct);
            _numSumpPit.Value = Clamp((decimal)defs.SumpPitHeight, _numSumpPit);
            _numFlowerPool.Value = Clamp((decimal)defs.FlowerPoolHeight, _numFlowerPool);

            // Tab 3 RoomTypeMaps
            _dgvRoomTypeMap.Rows.Clear();
            foreach (var m in _cfg.RoomTypeMaps ?? new List<RoomTypeMap>())
            {
                _dgvRoomTypeMap.Rows.Add(string.Join(", ", m.Keywords ?? new List<string>()), m.RoomType ?? "");
            }

            // Tab 4 Keywords
            _lstFloorKws.Items.Clear();
            foreach (var s in (_cfg.TemplateSettings?.FloorItemKeywords ?? new List<string>())) _lstFloorKws.Items.Add(s);
            _lstWallKws.Items.Clear();
            foreach (var s in (_cfg.TemplateSettings?.WallItemKeywords ?? new List<string>())) _lstWallKws.Items.Add(s);

            // Tab 5 FallbackMap
            var fb = _cfg.TemplateSettings?.RoomTypeFallbackMap ?? new Dictionary<string, string>();
            _dgvFallbackMap.Rows.Clear();
            foreach (var kv in fb) _dgvFallbackMap.Rows.Add(kv.Key, kv.Value);

            // Tab 6 Templates
            _txtTplFolder.Text = _cfg.TemplateSettings?.TemplateFolderPath ?? "";
            _cmbActiveTpl.Items.Clear();
            _dgvTplFiles.Rows.Clear();
            var tpls = _cfg.TemplateSettings?.Templates ?? new Dictionary<string, string>();
            foreach (var kv in tpls)
            {
                _cmbActiveTpl.Items.Add(kv.Key);
                _dgvTplFiles.Rows.Add(kv.Key, kv.Value);
            }
            string active = _cfg.TemplateSettings?.ActiveTemplate ?? "dizhuan";
            int ai = _cmbActiveTpl.Items.IndexOf(active);
            _cmbActiveTpl.SelectedIndex = ai >= 0 ? ai : (_cmbActiveTpl.Items.Count > 0 ? 0 : -1);

            // Tab 7 FloorAliasMap
            var alias = _cfg.TemplateSettings?.FloorAliasMap ?? new Dictionary<string, string>();
            _dgvFloorAlias.Rows.Clear();
            foreach (var kv in alias) _dgvFloorAlias.Rows.Add(kv.Key, kv.Value);

            // Tab 8 TileSpec
            RebuildSpecRoomTypeList();

            // 🔧 review-fix #1+#2: ComboBox 单元格需要值在 Items 内才显示 — 用户保存了自定义 CalcRule (例
            //   以后加的 "Perimeter") 时动态加进 col.Items, 这才不在 Load 后变 blank、才不丢值.
            //   dedupe via HashSet, 不丢 列表顺序. 去掉了原来的 空 if-body死代码.
            //   该 col 在 BuildQuoteItemsTab 是 local var, 不在函数 scope — 按 Name 从 _dgvQuoteItems.Columns 找回.
            var seenCalcRules = new HashSet<string>(CalcRules, StringComparer.Ordinal);
            var colCalcRuleDGV = _dgvQuoteItems.Columns["CalcRule"] as DataGridViewComboBoxColumn;
            // Tab 9 QuoteItems
            _dgvQuoteItems.Rows.Clear();
            foreach (var qi in _cfg.QuoteItems ?? new List<QuoteItemConfig>())
            {
                if (colCalcRuleDGV != null
                    && !string.IsNullOrWhiteSpace(qi.CalcRule)
                    && seenCalcRules.Add(qi.CalcRule))
                {
                    colCalcRuleDGV.Items.Add(qi.CalcRule);
                }
                _dgvQuoteItems.Rows.Add(qi.Name, qi.Unit, qi.MaterialPrice, qi.LaborPrice, qi.CalcRule, qi.IsSummaryItem, qi.Description);
            }
        }

        private decimal Clamp(decimal val, NumericUpDown ctrl) =>
            Math.Max(ctrl.Minimum, Math.Min(ctrl.Maximum, val));

        private void ApplyUIToConfig()
        {
            // Tab 1
            _cfg.CompanyName = _txtCompanyName.Text?.Trim() ?? "";
            _cfg.CompanyAddress = _txtCompanyAddress.Text?.Trim() ?? "";

            // Tab 2
            _cfg.DefaultWallHeight = (double)_numWallHeight.Value;
            if (_cfg.BathroomKitchenDefaults == null) _cfg.BathroomKitchenDefaults = new BathroomKitchenDefaults();
            var d = _cfg.BathroomKitchenDefaults;
            d.TileHeight = (double)_numTileHeight.Value;
            d.WaterproofHeight = (double)_numWaterproofHeight.Value;
            d.KitchenWaterproofHeight = (double)_numKitchenWaterproof.Value;
            d.BalconyWaterproofHeight = (double)_numBalconyWaterproof.Value;
            d.OutdoorGardenRollHeight = (double)_numGardenRoll.Value;
            d.OutdoorGardenNonRollHeight = (double)_numGardenNonRoll.Value;
            d.DefaultDoorDeduct = (double)_numDoorDeduct.Value;
            d.DefaultWindowDeduct = (double)_numWindowDeduct.Value;
            d.SumpPitHeight = (double)_numSumpPit.Value;
            d.FlowerPoolHeight = (double)_numFlowerPool.Value;

            // Tab 3 → RoomTypeMaps
            _cfg.RoomTypeMaps = new List<RoomTypeMap>();
            foreach (DataGridViewRow r in _dgvRoomTypeMap.Rows)
            {
                if (r.IsNewRow) continue;
                string kwText = Convert.ToString(r.Cells["Keywords"].Value) ?? "";
                string rt = Convert.ToString(r.Cells["RoomType"].Value) ?? "";
                if (string.IsNullOrWhiteSpace(rt)) continue;
                var kws = kwText.Split(new[] { ',', '|', '，', '、' }, StringSplitOptions.RemoveEmptyEntries)
                                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                _cfg.RoomTypeMaps.Add(new RoomTypeMap { Keywords = kws, RoomType = rt });
            }

            // Tab 4 → FloorItemKeywords / WallItemKeywords
            if (_cfg.TemplateSettings == null) _cfg.TemplateSettings = new TemplateSettingsConfig();
            _cfg.TemplateSettings.FloorItemKeywords = _lstFloorKws.Items.Cast<object>().Select(o => o.ToString()).ToList();
            _cfg.TemplateSettings.WallItemKeywords = _lstWallKws.Items.Cast<object>().Select(o => o.ToString()).ToList();

            // 先 commit 当前的 TileSpec / FloorAlias / Templates / FallbackMap 到 _cfg (因为会重复引用同一 _cfg.TemplateSettings)
            ApplyUIToCfg_RoomSpecTemplatesPart();

            // Tab 5 → RoomTypeFallbackMap
            _cfg.TemplateSettings.RoomTypeFallbackMap = new Dictionary<string, string>();
            foreach (DataGridViewRow r in _dgvFallbackMap.Rows)
            {
                if (r.IsNewRow) continue;
                string k = Convert.ToString(r.Cells["Key"].Value) ?? "";
                string v = Convert.ToString(r.Cells["Value"].Value) ?? "";
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    _cfg.TemplateSettings.RoomTypeFallbackMap[k] = v;
            }

            // 🔧 review-fix #3: CMB 之前仅 LoadConfig 时同步, 用户加了 DGV 新 key 后 CMB stale →
            //   save 时 SelectedItem 可能指向不存在的 key. 重建 CMB (从 DGV) + 保留用户当前选中。
            //   注: 拿"用户当前 选中"而不是 _cfg.ActiveTemplate — 后者是 Load 时的陈值, 不能反映用户中途手动改。
            string userPickedActive = _cmbActiveTpl.SelectedItem?.ToString() ?? "";
            _cmbActiveTpl.Items.Clear();
            foreach (DataGridViewRow r in _dgvTplFiles.Rows)
            {
                if (r.IsNewRow) continue;
                string k = Convert.ToString(r.Cells["Key"].Value) ?? "";
                if (!string.IsNullOrEmpty(k)) _cmbActiveTpl.Items.Add(k);
            }
            int ai = _cmbActiveTpl.Items.IndexOf(userPickedActive);
            // 🔧 reviewer nit: 不要 silent auto-promote 首项 — 用户删了 picked template 后 为 -1 (ActiveTemplate 留空) 让人可见.
            _cmbActiveTpl.SelectedIndex = ai >= 0 ? ai : -1;

            // Tab 6 → Templates
            _cfg.TemplateSettings.TemplateFolderPath = _txtTplFolder.Text?.Trim() ?? "";
            _cfg.TemplateSettings.Templates = new Dictionary<string, string>();
            foreach (DataGridViewRow r in _dgvTplFiles.Rows)
            {
                if (r.IsNewRow) continue;
                string k = Convert.ToString(r.Cells["Key"].Value) ?? "";
                string v = Convert.ToString(r.Cells["File"].Value) ?? "";
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    _cfg.TemplateSettings.Templates[k] = v;
            }
            _cfg.TemplateSettings.ActiveTemplate = _cmbActiveTpl.SelectedItem?.ToString() ?? "";

            // Tab 7 → FloorAliasMap
            _cfg.TemplateSettings.FloorAliasMap = new Dictionary<string, string>();
            foreach (DataGridViewRow r in _dgvFloorAlias.Rows)
            {
                if (r.IsNewRow) continue;
                string k = Convert.ToString(r.Cells["Raw"].Value) ?? "";
                string v = Convert.ToString(r.Cells["Norm"].Value) ?? "";
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                    _cfg.TemplateSettings.FloorAliasMap[k] = v;
            }

            // Tab 9 → QuoteItems
            _cfg.QuoteItems = new List<QuoteItemConfig>();
            foreach (DataGridViewRow r in _dgvQuoteItems.Rows)
            {
                if (r.IsNewRow) continue;
                string name = Convert.ToString(r.Cells["Name"].Value) ?? "";
                if (string.IsNullOrWhiteSpace(name)) continue;
                _cfg.QuoteItems.Add(new QuoteItemConfig
                {
                    Name = name,
                    Unit = Convert.ToString(r.Cells["Unit"].Value) ?? "",
                    MaterialPrice = ParseDouble(r.Cells["MaterialPrice"].Value),
                    LaborPrice = ParseDouble(r.Cells["LaborPrice"].Value),
                    CalcRule = Convert.ToString(r.Cells["CalcRule"].Value) ?? "Floor",
                    IsSummaryItem = Convert.ToBoolean(r.Cells["IsSummaryItem"].Value),
                    Description = Convert.ToString(r.Cells["Description"].Value) ?? "",
                });
            }
        }

        // Tab 8 TileSpec 单独处理 — master-detail, 需要先 commit 当前选中的房间类型的 DGV
        private void CommitCurrentSpecToCfg()
        {
            if (_cfg.TemplateSettings == null) _cfg.TemplateSettings = new TemplateSettingsConfig();
            if (_cfg.TemplateSettings.TileSpecOptions == null)
                _cfg.TemplateSettings.TileSpecOptions = new Dictionary<string, List<TileSpecOption>>();
            string curRt = _cmbSpecRoomType.SelectedItem?.ToString();
            if (string.IsNullOrEmpty(curRt)) return;
            var list = new List<TileSpecOption>();
            foreach (DataGridViewRow r in _dgvTileSpec.Rows)
            {
                if (r.IsNewRow) continue;
                string label = Convert.ToString(r.Cells["Label"].Value) ?? "";
                string value = Convert.ToString(r.Cells["Value"].Value) ?? "";
                string matchStr = Convert.ToString(r.Cells["Match"].Value) ?? "";
                if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(value)) continue;
                var matches = matchStr.Split(new[] { ',', '|', '，', '、' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
                list.Add(new TileSpecOption
                {
                    Label = label,
                    Value = value,
                    Match = matches,
                    IsDefault = Convert.ToBoolean(r.Cells["IsDefault"].Value),
                    MaterialPrice = ParseNullableDouble(r.Cells["MaterialPrice"].Value),
                    LaborPrice = ParseNullableDouble(r.Cells["LaborPrice"].Value),
                });
            }
            _cfg.TemplateSettings.TileSpecOptions[curRt] = list;
        }

        // ApplyUIToConfig 时调一次 (把 master-detail 当前正在编辑的 房间 类型 commit 完, 再继续读其他 1D 表)
        private void ApplyUIToCfg_RoomSpecTemplatesPart()
        {
            // 1. 当前列表的 TileSpec
            CommitCurrentSpecToCfg();
            // 2. 同时把列表里其他 room types 还没 commit 的也 commit (实际 _cfg 上都有 entry, 但 DGV 只显示当前选中的)
            //    → 已隐式处理, 因为 _cfg 已经 initialized (LoadConfig + ApplyConfigToUI 调用过)
        }

        private static double ParseDouble(object val)
        {
            if (val == null || val == DBNull.Value) return 0.0;
            double d;
            double.TryParse(Convert.ToString(val), out d);
            return d;
        }

        private static double? ParseNullableDouble(object val)
        {
            if (val == null || val == DBNull.Value) return null;
            string s = Convert.ToString(val)?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            if (double.TryParse(s, out var d)) return d;
            return null;
        }

        // ══════════════════════════════════════════════
        //  Tab 8 TileSpec master-detail 逻辑
        // ══════════════════════════════════════════════

        private void RebuildSpecRoomTypeList()
        {
            _cmbSpecRoomType.Items.Clear();
            // 用 _cfg 里实际存在的 key 顺序 (用户首次编辑时可手动加 SixCats 之外的, 如 "客厅")
            var keys = _cfg?.TemplateSettings?.TileSpecOptions?.Keys?.ToList() ?? new List<string>();
            // 加 SixCats 作为推荐未配置项
            foreach (var c in SixCats)
                if (!keys.Contains(c)) keys.Add(c);
            foreach (var k in keys) _cmbSpecRoomType.Items.Add(k);
            // 默认选中第一个
            _cmbSpecRoomType.SelectedIndex = (_cmbSpecRoomType.Items.Count > 0) ? 0 : -1;
            RefreshTileSpecGrid();
        }

        private void RefreshTileSpecGrid()
        {
            // commit 当前 room type (避免 DGV 值在切换时丢失)
            CommitCurrentSpecToCfg();
            string curRt = _cmbSpecRoomType.SelectedItem?.ToString();
            _dgvTileSpec.Rows.Clear();
            if (string.IsNullOrEmpty(curRt)) return;
            var list = _cfg?.TemplateSettings?.TileSpecOptions?.TryGetValue(curRt, out var l) == true ? l : null;
            if (list == null) return;
            foreach (var spec in list)
            {
                _dgvTileSpec.Rows.Add(
                    spec.Label,
                    spec.Value,
                    string.Join(", ", spec.Match ?? new List<string>()),
                    spec.IsDefault,
                    spec.MaterialPrice.HasValue ? (object)spec.MaterialPrice.Value : DBNull.Value,
                    spec.LaborPrice.HasValue ? (object)spec.LaborPrice.Value : DBNull.Value);
            }
        }

        private void BtnSpecAdd_Click(object sender, EventArgs e)
        {
            _dgvTileSpec.Rows.Add("新规格 (改 Label)", "spNew-" + Guid.NewGuid().ToString("N").Substring(0, 4), "正铺", false, DBNull.Value, DBNull.Value);
        }

        private void BtnSpecDel_Click(object sender, EventArgs e)
        {
            foreach (DataGridViewRow r in _dgvTileSpec.SelectedRows)
                if (!r.IsNewRow) _dgvTileSpec.Rows.Remove(r);
        }

        /// <summary>
        /// EXE 入口点. WinExe 项目必须显式包含 static Main().
        /// </summary>
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"BaoJiaCAD 配置编辑启动失败:\n{ex.Message}\n\n{ex.StackTrace}",
                    "致命错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    // 辅助扩展: 简洁地把多个控件塞进 FlowLayoutPanel
    internal static class FlowPanelExt
    {
        public static FlowLayoutPanel _Add(this FlowLayoutPanel p, params Control[] controls)
        {
            p.Controls.AddRange(controls);
            return p;
        }
    }
}
