"""Final cleanup of GetTemplatePath in BaoJiaCAD/Commands.cs.

Reviewer-flagged issue #1 addressed: restore the breadcrumb warning when
!userChangedTemplate + 复式 + fushi 文件找不到 (was lost in prior round).

DRY nit (helper method extraction) DEFERRED — 2 sites × ~6 lines is acceptable,
helper extraction introduces more refactor risk than ratio of savings.

All CJK as literal UTF-8 in Python source; no \\uXXXX escape sequences
in the script body to avoid \\ud83d\\udd27 surrogate-pair pitfalls.
"""
from pathlib import Path

TARGET = Path(r"E:\xiangmu\baojia\BaoJiaCAD\Commands.cs")
src = TARGET.read_text(encoding="utf-8")

SIG = "private string GetTemplatePath(QuoteConfig config, bool isMultiFloor, string overrideTemplate, Editor editor)\n        {"
start = src.find(SIG)
if start < 0:
    raise SystemExit("ERROR: SIG not found")

throw_start = src.find("throw new FileNotFoundException(", start)
if throw_start < 0:
    raise SystemExit("ERROR: throw not found after SIG")

after_throw = src.find("\");\n", throw_start)
if after_throw < 0:
    raise SystemExit("ERROR: throw-close not found")

method_close = src.find("        }\n", after_throw)
if method_close < 0:
    raise SystemExit("ERROR: method-close `        }` not found")

slice_end = method_close + len("        }")

NEW_BODY = (
    "private string GetTemplatePath(QuoteConfig config, bool isMultiFloor, string overrideTemplate, Editor editor)\n"
    "        {\n"
    "            string dllDir = Path.GetDirectoryName(typeof(Commands).Assembly.Location);\n"
    "            var ts = config?.TemplateSettings;\n"
    "            // 修复 #3: TemplateFolderPath 为空时使用 DLL 目录作为默认模板文件夹\n"
    "            string tplDir = (ts != null && !string.IsNullOrEmpty(ts.TemplateFolderPath))\n"
    "                ? ts.TemplateFolderPath\n"
    "                : dllDir;\n"
    "\n"
    "            // 0. 检测用户是否主动改了面板下拉 (overrideTemplate != ActiveTemplate 表示改过).\n"
    "            //    不区分会产生 1 个 regression: 复式 + 不动下拉 -> 旧行为默认 fushi, 新逻辑会无限选 dizhuan.\n"
    "            bool userChangedTemplate =\n"
    "                !string.IsNullOrEmpty(overrideTemplate)\n"
    "                && overrideTemplate != (ts?.ActiveTemplate ?? \"\");\n"
    "\n"
    "            // 1. 保留老行为: 用户未改下拉 + 复式 -> 默认 fushi\n"
    "            if (!userChangedTemplate && isMultiFloor\n"
    "                && ts != null\n"
    "                && ts.Templates != null\n"
    "                && ts.Templates.TryGetValue(\"fushi\", out var fushiDefaultFile))\n"
    "            {\n"
    "                string fpDefault = Path.Combine(tplDir, fushiDefaultFile);\n"
    "                if (File.Exists(fpDefault))\n"
    "                {\n"
    "                    editor.WriteMessage($\"\\n[模板] 复式默认 (面板未改) -> 使用 {fushiDefaultFile} (含楼层分组)\");\n"
    "                    return fpDefault;\n"
    "                }\n"
    "                // 文件缺失: 醒目 breadcrumb (旧行为保留), 不静默退化到 dizhuan\n"
    "                editor.WriteMessage(\n"
    "                    $\"\\n[模板] !! 复式期望 fushi 但路径 {tplDir} 不存在, \" +\n"
    "                    $\"fallback 到面板选择 ({ts.ActiveTemplate ?? \"未配置\"})\");\n"
    "            }\n"
    "\n"
    "            // 2. 优先级最高: 面板下拉选择 (overrideTemplate / ActiveTemplate) - 复式也允许 mudiban/zhubaojiao\n"
    "            //    用户口述: \"复式 ≠ 必然 fushi\" - 选什么走什么\n"
    "            string activeTemplate = !string.IsNullOrEmpty(overrideTemplate) ? overrideTemplate : ts?.ActiveTemplate;\n"
    "            if (ts != null\n"
    "                && ts.Templates != null\n"
    "                && !string.IsNullOrEmpty(activeTemplate)\n"
    "                && ts.Templates.TryGetValue(activeTemplate, out var fileName))\n"
    "            {\n"
    "                string p = Path.Combine(tplDir, fileName);\n"
    "                if (File.Exists(p))\n"
    "                {\n"
    "                    editor.WriteMessage($\"\\n[模板] 面板选择 {activeTemplate} -> 使用 {fileName}\");\n"
    "                    if (isMultiFloor && activeTemplate != \"fushi\")\n"
    "                    {\n"
    "                        editor.WriteMessage(\n"
    "                            $\"\\n[模板] 注: {activeTemplate} 原型区非复式结构. \" +\n"
    "                            $\"ProcessRooms 按 Room.FloorLevel 自检测多楼 + ResolveTemplates 回退到无楼层前缀的原型. \" +\n"
    "                            $\"如有异常请选 fushi.\");\n"
    "                    }\n"
    "                    return p;\n"
    "                }\n"
    "                editor.WriteMessage($\"\\n[模板] !! 面板选 {activeTemplate} 但文件 {fileName} 不存在, 走 fushi 兜底\");\n"
    "            }\n"
    "\n"
    "            // 3. 复式兜底 (面板选的文件丢失时, 用 fushi 救场 - 提供楼层适配性)\n"
    "            if (isMultiFloor && ts != null\n"
    "                && ts.Templates != null\n"
    "                && ts.Templates.TryGetValue(\"fushi\", out var fushiFile))\n"
    "            {\n"
    "                string fpFallback = Path.Combine(tplDir, fushiFile);\n"
    "                if (File.Exists(fpFallback))\n"
    "                {\n"
    "                    editor.WriteMessage($\"\\n[模板] 复式兜底 -> 使用 {fushiFile}\");\n"
    "                    return fpFallback;\n"
    "                }\n"
    "                editor.WriteMessage(\n"
    "                    $\"\\n[模板] !! 复式期望 {fushiFile} 但路径 {tplDir} 不存在, \" +\n"
    "                    $\"fallback 到 ActiveTemplate={ts?.ActiveTemplate}\");\n"
    "            }\n"
    "\n"
    "            // 4. 终极 fallback 老 dllDir/template.xlsx\n"
    "            string fallback = Path.Combine(dllDir, \"template.xlsx\");\n"
    "            if (File.Exists(fallback))\n"
    "            {\n"
    "                editor.WriteMessage($\"\\n[模板] fallback 到 {fallback}（兼容老 dllDir 模板）\");\n"
    "                return fallback;\n"
    "            }\n"
    "\n"
    "            throw new FileNotFoundException(\n"
    "                $\"模板文件未找到。请检查 config.json 的 TemplateSettings 是否有效，或将 template.xlsx 放在插件 DLL 同级目录:\\n{dllDir}\");\n"
    "        }"
)

new_src = src[:start] + NEW_BODY + src[slice_end:]

# Sanity 1: braces balance
opens_before = src.count("{")
closes_before = src.count("}")
opens_after = new_src.count("{")
closes_after = new_src.count("}")
print(f"FILE braces before: open={opens_before} close={closes_before}")
print(f"FILE braces after:  open={opens_after}  close={closes_after}")
if (opens_before != closes_before) or (opens_after != closes_after):
    raise SystemExit("ERROR: file-level brace imbalance")

# Sanity 2: NEW_BODY itself
new_opens = NEW_BODY.count("{")
new_closes = NEW_BODY.count("}")
print(f"NEW_BODY braces: open={new_opens} close={new_closes}")
if new_opens != new_closes:
    raise SystemExit("ERROR: NEW_BODY brace imbalance")

# Sanity 3: critical markers
markers = [
    "userChangedTemplate",
    "overrideTemplate",
    "isMultiFloor",
    "fushiDefaultFile",
    "fushiFile",
    "activeTemplate",
    "ActiveTemplate",
    "fushi",
    "tplDir",
    "fallback",
]
for m in markers:
    if m not in new_src:
        raise SystemExit(f"ERROR: missing marker {m!r}")

# Sanity 4: forbidden — old comment block
forbidden = [
    "复式场景：优先 fushi",
]
for pat in forbidden:
    if pat in NEW_BODY:
        raise SystemExit(f"ERROR: NEW_BODY still has old marker {pat!r}")

# Sanity 5: byte delta
delta = len(new_src) - len(src)
print(f"OLD bytes: {len(src.encode('utf-8'))}")
print(f"NEW bytes: {len(new_src.encode('utf-8'))}")
print(f"Bytes delta: {delta:+d}")

# Sanity 6: utf-8 clean
try:
    encoded = NEW_BODY.encode("utf-8")
    encoded_full = new_src.encode("utf-8")
    print(f"NEW_BODY encodes cleanly: {len(encoded)} bytes")
    print(f"new_src   encodes cleanly: {len(encoded_full)} bytes")
except UnicodeEncodeError as ex:
    raise SystemExit(f"ERROR: surrogate issue: {ex}")

# Sanity 7: NEW_BODY should have BOTH inline fushi lookups (Step 1 + Step 3)
fushi_lookup_count = NEW_BODY.count('ts.Templates.TryGetValue("fushi"')
print(f"fushi lookup block count: {fushi_lookup_count} (expected 2)")
if fushi_lookup_count != 2:
    raise SystemExit(f"ERROR: expected 2 fushi lookup blocks, got {fushi_lookup_count}")

# Sanity 8: breadcrumb warning must include "复式期望 fushi"
if "复式期望 fushi" not in NEW_BODY:
    raise SystemExit("ERROR: missing restored breadcrumb warning message")

TARGET.write_text(new_src, encoding="utf-8")
print("OK — GetTemplatePath with restored breadcrumb.")
