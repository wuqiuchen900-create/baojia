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
        private Button _btnCancel;

        // 🔧 瓷砖规格动态下拉: 仅当 config.TemplateSettings.TileSpecOptions[roomType].Count ≥ 2 才生成.
        private readonly Dictionary<string, ComboBox> _tileSpecCombos = new Dictionary<string, ComboBox>();

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

            const int labelW = 80;
            const int comboW = 280;
            int rowH = 32;
            // 把 _btnStart/_btnCancel 向下推 pushUp px, 给瓷砖规格节腾位置 (插在门洞/窗洞 与按钮之间).
            // 同时窗体增高 pushUp + AutoScroll=true 让超桌高时滚动条出现 (CAD palette 桌高 ≈640px),
            // 按钮不会被剪 (reviewer HIGH 建议).
            //   - 2 entries → pushUp = 2*32+22 = 86, 窗体高 560+86 = 646.
            //   - 4 entries → pushUp = 4*32+22 = 150, 窗体高 560+150 = 710.
            int rowsNeeded = entries.Count;
            int pushUp = rowsNeeded * rowH + 22;
            int originalBtnTop = _btnStart.Top;
            int originalCancelTop = _btnCancel.Top;
            _btnStart.Top = originalBtnTop + pushUp;
            _btnCancel.Top = originalCancelTop + pushUp;
            this.Size = new Size(this.Width, this.Height + pushUp);
            this.AutoScroll = true;   // 防 CAD palette 高度限制 — 超过桌高时滚动条兜底

            int yStart = originalBtnTop;
            int xLabel = 20;
            int xCombo = 20 + labelW + 8;
            var sep = new Label
            {
                Text = "── 瓷砖规格 (按房间) ──",
                Left = 20, Top = yStart,
                Width = 380,
                ForeColor = SystemColors.GrayText,
                Font = new Font("Microsoft YaHei", 8F)
            };
            this.Controls.Add(sep);
            yStart += 22;

            var added = new List<Control>();
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
                // 🔧 优先用 isDefault=true 的 spec 作默认; 都无则 index=0.
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
                added.Add(lbl);
                added.Add(cb);
            }
            this.Controls.AddRange(added.ToArray());
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
            };
            y += rowH;

            // ── 复式层数 ──
            _lblFloorCount = new Label { Text = "复式层数：", Left = 40, Top = y, Width = labelW - 20, TextAlign = ContentAlignment.MiddleRight };
            _numFloorCount = new NumericUpDown
            {
                Left = 40 + labelW - 20 + 8, Top = y, Width = 80,
                Minimum = 2, Maximum = 5, Value = 2, Enabled = false
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
                _cmbTemplate.Items.AddRange(ts.Templates.Keys.Cast<object>().ToArray());
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
        /// 从面板里抽用户选的瓷砖规格 (每 roomType -> spec.Value) 返回; 由 Commands 写进 config.SelectedTileSpecs.
        /// 没出现的 roomType 不入 (代表用户没改该类别下拉, 走 fallback).
        /// </summary>
        public Dictionary<string, string> GetTileSpecSelections()
        {
            var map = new Dictionary<string, string>();
            foreach (var kv in _tileSpecCombos)
            {
                if (kv.Value.SelectedItem is TileSpecOption opt && !string.IsNullOrEmpty(opt.Value))
                    map[kv.Key] = opt.Value;
            }
            return map;
        }
    }
}
