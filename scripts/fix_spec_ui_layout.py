"""修 QuotePanel.cs BuildTileSpecSection 两处问题:

1) `var (roomType, list) = entries[i]` 在 net48 上 KeyValuePair<,>.Deconstruct 重载推断失败 (8 errors CS0411/CS8129/CS8130)
   → 改成 `string roomType = entries[i].Key; List<TileSpecOption> list = entries[i].Value;`
2) reviewer 提的 layout bug: 按钮向上推 + yStart=originalBtnTop-pushUp (与按钮 top 同)
   → 改成按钮向下推 + yStart=originalBtnTop (按钮在新节下方) + this.Size.Height += pushUp (窗体变高避免下限被切)
"""
from pathlib import Path

p = Path("BaoJiaCAD/QuotePanel.cs")
src = p.read_text(encoding="utf-8")

# 修 1: tuple deconstruct → 显式 .Key / .Value
old1 = "            var (roomType, list) = entries[i];"
new1 = "            string roomType = entries[i].Key;\n            List<TileSpecOption> list = entries[i].Value;"
if old1 not in src:
    print("ERR: anchor #1 not found")
    raise SystemExit(1)
src = src.replace(old1, new1, 1)

# 修 2: 按钮改成向下推 + yStart = originalBtnTop + 窗体增高
old2 = """            // 将新段插入到按钮上方: 把 _btnStart 与 _btnCancel 向上推  rowsNeeded * rowH + 22 px.
            int rowsNeeded = (entries.Count + 1) / 2;
            int pushUp = rowsNeeded * rowH + 22;
            int originalBtnTop = _btnStart.Top;
            int originalCancelTop = _btnCancel.Top;
            _btnStart.Top = originalBtnTop - pushUp;
            _btnCancel.Top = originalCancelTop - pushUp;

            int yStart = originalBtnTop - pushUp;"""
new2 = """            // 把 _btnStart/_btnCancel 向下推 pushUp px, 给瓷砖规格节腾位置 (插在门洞/窗洞 与按钮之间).
            // 同时把窗体增高 pushUp, 避免底部按被切.
            int rowsNeeded = (entries.Count + 1) / 2;
            int pushUp = rowsNeeded * rowH + 22;
            int originalBtnTop = _btnStart.Top;
            int originalCancelTop = _btnCancel.Top;
            _btnStart.Top = originalBtnTop + pushUp;
            _btnCancel.Top = originalCancelTop + pushUp;
            this.Size = new Size(this.Width, this.Height + pushUp);

            int yStart = originalBtnTop;"""
if old2 not in src:
    print("ERR: anchor #2 not found")
    raise SystemExit(1)
src = src.replace(old2, new2, 1)

p.write_text(src, encoding="utf-8")
print("OK fixed QuotePanel.cs (deconstruct + layout)")
