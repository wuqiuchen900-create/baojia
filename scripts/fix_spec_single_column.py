"""修 QuotePanel.cs BuildTileSpecSection — 把 2 列 layout 改成单列竖排.

原因: 之前 label_100w + combo_180w 在 440 宽 form 中 xCol2 起点 338, combo 终点 626, 跑出画外
       (用户报告: 厨房 dropdown 不见).

修法: 单列 — label_80w + combo_280w, xCombo=108 终点 388, 全部留 440 form 内 (520 left 边距).
       行数 = entries.Count (was ceil(count/2)): 2 entries → pushUp=2×32+22=86 (was 54);
       4 entries → pushUp=150 (was 86). 窗体高度自适应调高 (this.Size.Height += pushUp 已存在).
"""
from pathlib import Path

p = Path("BaoJiaCAD/QuotePanel.cs")
src = p.read_text(encoding="utf-8")

# 修 1: 把 const + 行数 + col 坐标 + for-loop 单列化
old1 = """            const int labelW = 100;
            const int colW = 180;
            int rowH = 32;
            // 把 _btnStart/_btnCancel 向下推 pushUp px, 给瓷砖规格节腾位置 (插在门洞/窗洞 与按钮之间).
            // 同时把窗体增高 pushUp, 避免底部按被切.
            int rowsNeeded = (entries.Count + 1) / 2;
            int pushUp = rowsNeeded * rowH + 22;
            int originalBtnTop = _btnStart.Top;
            int originalCancelTop = _btnCancel.Top;
            _btnStart.Top = originalBtnTop + pushUp;
            _btnCancel.Top = originalCancelTop + pushUp;
            this.Size = new Size(this.Width, this.Height + pushUp);

            int yStart = originalBtnTop;
            int xCol1 = 20;
            int xCol2 = 20 + labelW + 8 + colW + 30;
            var sep = new Label
            {
                Text = \"── 瓷砖规格 (按房间) ──\",
                Left = 20, Top = yStart,
                Width = 380,
                ForeColor = SystemColors.GrayText,
                Font = new Font(\"Microsoft YaHei\", 8F)
            };
            this.Controls.Add(sep);
            yStart += 22;"""
new1 = """            const int labelW = 80;
            const int comboW = 280;
            int rowH = 32;
            // 单列竖排 (label + combo 全宽 一行一 spec), 保证 440 form 宽下不跑出画外.
            //   - 2 entries → pushUp = 2*32+22 = 86, 窗体高 560+86 = 646.
            //   - 4 entries → pushUp = 4*32+22 = 150, 窗体高 560+150 = 710.
            int rowsNeeded = entries.Count;
            int pushUp = rowsNeeded * rowH + 22;
            int originalBtnTop = _btnStart.Top;
            int originalCancelTop = _btnCancel.Top;
            _btnStart.Top = originalBtnTop + pushUp;
            _btnCancel.Top = originalCancelTop + pushUp;
            this.Size = new Size(this.Width, this.Height + pushUp);

            int yStart = originalBtnTop;
            int xLabel = 20;
            int xCombo = 20 + labelW + 8;
            var sep = new Label
            {
                Text = \"── 瓷砖规格 (按房间) ──\",
                Left = 20, Top = yStart,
                Width = 380,
                ForeColor = SystemColors.GrayText,
                Font = new Font(\"Microsoft YaHei\", 8F)
            };
            this.Controls.Add(sep);
            yStart += 22;"""
if old1 not in src:
    print("ERR anchor #1 not found")
    raise SystemExit(1)
src = src.replace(old1, new1, 1)

# 修 2: for-loop 内去掉 leftCol/i/2 双列计算, 改单列 i 单行
old2 = """            var added = new List<Control>();
            for (int i = 0; i < entries.Count; i++)
            {
                string roomType = entries[i].Key;
                List<TileSpecOption> list = entries[i].Value;
                bool leftCol = (i % 2 == 0);
                int xLabel = leftCol ? xCol1 : xCol2;
                int xCombo = xLabel + labelW + 8;
                int y = yStart + (i / 2) * rowH;

                var lbl = new Label
                {
                    Text = $\"{roomType}:\",
                    Left = xLabel, Top = y, Width = labelW,
                    TextAlign = ContentAlignment.MiddleRight
                };
                var cb = new ComboBox
                {
                    Left = xCombo, Top = y, Width = colW,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };"""
new2 = """            var added = new List<Control>();
            for (int i = 0; i < entries.Count; i++)
            {
                string roomType = entries[i].Key;
                List<TileSpecOption> list = entries[i].Value;
                int y = yStart + i * rowH;

                var lbl = new Label
                {
                    Text = $\"{roomType}:\",
                    Left = xLabel, Top = y, Width = labelW,
                    TextAlign = ContentAlignment.MiddleRight
                };
                var cb = new ComboBox
                {
                    Left = xCombo, Top = y, Width = comboW,
                    DropDownStyle = ComboBoxStyle.DropDownList
                };"""
if old2 not in src:
    print("ERR anchor #2 not found")
    raise SystemExit(1)
src = src.replace(old2, new2, 1)

p.write_text(src, encoding="utf-8")
print("OK fixed QuotePanel.cs BuildTileSpecSection → single column layout")
