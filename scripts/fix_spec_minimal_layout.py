"""✅ 重做 single-column fix:
   上轮 ERR anchor #2 (List<TileSpecOption> list 那行实际只 12 空格, 不是 16). 本脚本按 *当前* 文件精确匹配.

✅ 同时加 reviewer HIGH (CAD palette 在 1080p 上 ≈640px, 2-cat 已 646, 4-cat 直接 710): this.AutoScroll = true
   让 form 超过桌高时自带滚动条, 按钮永远不会消失. 不限制 pushUp 计算, 走 AutoScroll 兜底.
"""
from pathlib import Path

p = Path("BaoJiaCAD/QuotePanel.cs")
src = p.read_text(encoding="utf-8")

# 修 1: 上半部 layout 改单列 (labelW 100->80, comboW 280, counts 单列)
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
            int xCol2 = 20 + labelW + 8 + colW + 30;"""
new1 = """            const int labelW = 80;
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

            int yStart = originalBtnTop;
            int xLabel = 20;
            int xCombo = 20 + labelW + 8;"""
if old1 not in src:
    print("ERR anchor #1 not found")
    raise SystemExit(1)
src = src.replace(old1, new1, 1)

# 修 2: for-loop 单列化 — 注意 「List<TileSpecOption> list」 行实际只 12 空格
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
                    Text = $\"{roomType}：\",
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
                    Text = $\"{roomType}：\",
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

# 修 3: AutoScroll=true 让超桌高时自动滚动条出现 (reviewer HIGH 建议补救)
old3 = """            this.Size = new Size(this.Width, this.Height + pushUp);

            int yStart = originalBtnTop;"""
new3 = """            this.Size = new Size(this.Width, this.Height + pushUp);
            this.AutoScroll = true;   // 防 CAD palette 高度限制 — 超过桌高时滚动条兜底

            int yStart = originalBtnTop;"""
if old3 not in src:
    print("ERR anchor #3 not found")
    raise SystemExit(1)
src = src.replace(old3, new3, 1)

p.write_text(src, encoding="utf-8")
print("OK applied: single-column + AutoScroll protection (build with this)")
