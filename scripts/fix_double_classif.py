"""One-line fix for Bug #1 in FixTotalFormula (ExcelExporter.cs).

Change:
  if (c2.Contains("\u5c0f\u8ba1")) subtotalRows.Add(row);
  if (c2.Contains("\u5408\u8ba1")) totalRows.Add(row);
To:
  if (c2.Contains("\u5c0f\u8ba1")) subtotalRows.Add(row);
  else if (c2.Contains("\u5408\u8ba1")) totalRows.Add(row);

This ensures a row labeled e.g. "\u5206\u9879\u5c0f\u8ba1\u5408\u8ba1" (or similar
mixed labels) is classified into ONE bucket \u2014 not both, avoiding the
double-counting bug where the same row contributes its F-value AND its
sub-component subtotals to a downstream grand total.
"""
from pathlib import Path

TARGET = Path(r"E:\xiangmu\baojia\BaoJiaCAD\ExcelExporter.cs")
src = TARGET.read_text(encoding="utf-8")

# Use Unicode escapes to avoid CJK transmission issues in the script itself.
OLD = (
    'if (c2.Contains("\u5c0f\u8ba1")) subtotalRows.Add(row);\n'
    '                if (c2.Contains("\u5408\u8ba1")) totalRows.Add(row);'
)
NEW = (
    'if (c2.Contains("\u5c0f\u8ba1")) subtotalRows.Add(row);\n'
    '                else if (c2.Contains("\u5408\u8ba1")) totalRows.Add(row);'
)

if OLD not in src:
    raise SystemExit("ERROR: OLD pattern not found")

count = src.count(OLD)
if count != 1:
    print(f"WARN: pattern matches {count} times (expected 1)")

new_src = src.replace(OLD, NEW)

# Sanity: braces still balanced.
opens_before = src.count("{")
closes_before = src.count("}")
opens_after = new_src.count("{")
closes_after = new_src.count("}")
print(f"FILE braces before: open={opens_before} close={closes_before}")
print(f"FILE braces after:  open={opens_after}  close={closes_after}")
if (opens_before != closes_before) or (opens_after != closes_after):
    raise SystemExit("ERROR: file-level brace imbalance introduced")

# Sanity: NEW count = OLD count (no leftover).
if new_src.count(OLD) != 0:
    raise SystemExit("ERROR: OLD pattern still in file after replace")
if new_src.count(NEW) != count:
    raise SystemExit("ERROR: NEW pattern count mismatch")

# Sanity: bytes delta reasonable (~+5 chars for the 'else ' keyword + space).
delta = len(new_src) - len(src)
print(f"Bytes delta: {delta:+d}")
if delta != 5:
    raise SystemExit(f"ERROR: byte delta {delta} != expected +5")

TARGET.write_text(new_src, encoding="utf-8")
print(f"OK \u2014 replaced {count} occurrence. Bug #1 fixed.")
