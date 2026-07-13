# -*- coding: utf-8 -*-
import openpyxl, os, codecs

out_lines = []
tpl_dir = "muban_clean"
for tpl in ["dizhuan", "mudiban", "fushi", "zhubaojiao"]:
    path = os.path.join(tpl_dir, f"{tpl}.xlsx")
    if not os.path.exists(path):
        out_lines.append(f"SKIP: {path}")
        continue
    wb = openpyxl.load_workbook(path, data_only=True)
    ws = wb.active
    out_lines.append(f"\n=== {tpl}.xlsx === (rows 8-70)")
    for row in ws.iter_rows(min_row=8, max_row=70, max_col=4, values_only=True):
        c1 = str(row[0] or "").strip()
        c2 = str(row[1] or "").strip()
        c3 = str(row[2] or "").strip()[:20] if len(row) > 2 else ""
        c4 = str(row[3] or "").strip()[:10] if len(row) > 3 else ""
        if c1 or c2:
            out_lines.append(f"  [{c1:8s}] {c2:35s} | {c3:20s} | {c4}")
    wb.close()

# write to UTF-8 file
with codecs.open("tpl_items.txt", "w", "utf-8-sig") as f:
    f.write("\n".join(out_lines))
print("Done. Written to tpl_items.txt")
