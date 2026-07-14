# BaoJiaCAD 匹配 / 分类 / 查找 / 路由 逻辑 维护档案

> **审计 scope**: 项目内所有「字符串 Contains / Regex / TryGetValue / FirstOrDefault / 分支判断」类代码路径。
> **目的**: 后期维护、加新 RoomType / 新模板、改动 fallback 链路时一站式查到「我该改哪里」。
> **写档日期**: 2026-07-14，依据 v15 ship 后代码状态。
> **覆盖文件**: `BaoJiaCAD/*.cs` + `BaoJiaCAD/config.json`

---

## 0. 演化时间线 (版本 → Layer 改动)

| 版本 | Layer | 触发 |
|---|---|---|
| v5 | L11 (Floor/Wall 关键词) + L17 (PHASE A gate) | 单规格模板 price override |
| v6 | L5 (k1/k2/k3 三key 兑底) + L4 (per-floor template) | 多楼层选择性 |
| v7 | L7 (3D-tplDict key) + L4 (scratchpad) + L14 (multi-template) | 复式跨层模板混合 |
| v8 | L10 (ItemFormulas Bath/Kit/Outer) | 卫生间/厨房/外花园默认公式 |
| v9 | L8 (NONE 标记) + L4.5 (NONE 选项) | `<NONE>` 选项触发 mudiban 风格免动 |
| v11 | L1 Order (主卫→主卧→卫→厨→卧) + L3 (8 大类) | 防「主卧」误匹配 |
| v14 | L9 (ApplyLivingRoomNoneMudiban) + DirtSpecRegex | 「mudiban + 客 None 不贴砖」需求 |
| v14.2 | L9.a (dirtRow 通配 MM) + Commands.cs ActiveTemplate 写回 | 单层 fallback stale + 实木石砖线不命中 |
| v14.4 | L9.b (CS0841 fix + zero-fill fallback) | 双兜底 |
| v14.5 | L9.c (material-keyword gate) | 防「5MM 圆角倒角」 sub-spec 误 catch |
| v14.6 | L9.d (LastOrDefault) | 主规格末位, 修「错行被改」 |
| **v15** | L12.5 `_v14TriggerRoomTypes` HashSet | 触发 RoomType 由「客餐厅」→ `{客餐/阳台/外花园}` |

---

## 1. 数据流图 (CAD → xlsx)

```
CAD user 框选 「客厅」 文字
        │
        ▼
[Layer 1] CAD text → RoomType 分类  (MatchRoomType / ClassifyRoom)
[Layer 2] 楼层前缀 识别            (StripFloorPrefix / ExtractFloor)
        │ → Room {Name, RoomType, FloorLevel, FloorArea, Perimeter, WallHeight}
        ▼
[Layer 3] Room → 6/8 大类归纳     (MapToSixCategory)
        │ → 用于 QuotePanel 显示 + 排序
        ▼ (BJ 按钮触发)
[Layer 4] 面板每层模板 dropdown → config.SelectedFloorTemplates
[Layer 5] 面板每房间规格 dropdown → config.SelectedTileSpecs (k1/k2/k3)
        │
        ▼ (ExcelExporter)
[Layer 6] ParseTemplate                  (扫描模板 rooms groups 区)
        │ → List<TemplateGroup> { header, items, subtotal, SourceTemplate }
        ▼
[Layer 7] ResolveTemplates 8-tier lookup  (3D key + FallbackMap 兑底链)
        │ → 每个 Room 拿到 一组 group 克隆源
        ▼ (FillRoomData per cloned group)
[Layer 8] Spec marker 检测              (NONE / sp600-1200-LR 等)
        │
        ▼
[Layer 9] v14 special trigger set       (mudiban + NONE + 客餐/阳台/外花园)
        │ → ApplyLivingRoomNoneMudiban 或 走下面 PHASE A
        ▼
[Layer 10] PHASE A: 单规格 price override  (IdentifyTileSpecMatch + BuildSpecItemName)
[Layer 11] PHASE B: Outdoor/Indoor formulas  (FindFormulaKey)
[Layer 12] PHASE C: Floor/Wall item 分类    (IsFloorItem / IsWallItem)
        │
        ▼ per template item row
[Layer 13] C3 数量 Fill                 (FloorArea / WallArea / formula 数量)
        │
        ▼
[Layer 14] xlsx closed + cleanup        (InvalidDefinedNameRegex)
        ▼
output.xlsx 保存至 user 指定路径
```

---

## 2. Layer 详细 档案

---

### Layer 1 — CAD text → RoomType 分类

**入口**:
- `RoomDetector.MatchRoomType` (RoomDetector.cs:259-271) — DetectRooms 主入口
- `ExcelExporter.ClassifyRoom` (ExcelExporter.cs:647-654) — ParseTemplate 辅入口
- `CategoryPanel.MapToSixCategory` (CategoryPanel.cs:39-57) — 显示 / 排序 辅入口

**逻辑**: `_config.RoomTypeMaps` 是 `List<RoomTypeMap>`, 按顺序遍历, `text.Contains(k)` 命中则返回 `RoomType` (first-match-wins)。无匹配返 `null` / `"其他"`。

**Config 默认** (QuoteConfig.cs:113-124):

| Order | Keywords | RoomType |
|---|---|---|
| 1 | 花园/露台/庭院/天井/花池/花坛 | 外花园 |
| 2 | 客餐厅/客厅/餐厅/楼梯/起居/门厅/玄关/过道/走廊/入户/堂屋/阁楼/地下室/吧台/家庭厅/阳光房 | 客餐厅 |
| 3 | 阳台 | 阳台 |
| 4 | 主卫/主卧内卫/主卧卫生间/主人卫生间 | 主卫 |
| 5 | 卫/厕所/洗手间 | 卫生间 |
| 6 | 厨 | 厨房 |
| 7 | 主卧/主人房/主人卧室 | 主卧 |
| 8 | 卧/书房/父母/儿童/小孩/保姆/衣帽间/储物间/茶室/钢琴房/老人房/女儿房/儿子房/客房/电竞房/影音室/健身房/棋牌室/麻将室/酒窖/红酒/画室/工作室/佛堂/桑拿/KTV/多功能室/休闲厅/收藏室/台球室 | 卧室 |

**Edge cases**:
- ⚠️ **顺序敏感 (v11 fix)**: 主卫必须在卫生间之前; 主卧必须在卧室之前。否则「主卧卫生间」归公卫 bucket; 「主卧 + 衣帽间」归卧室。
- ⚠️ **外花园最前**: 因为「露台花园」中「花园」比其它 keyword 先 — 故排首位。
- ⭕ **空 / 标点文字**: 返 null/「其他」, caller skip。

**回归风险**: 加新 RoomType → 决定「*keyword 顺序*」别动现有; 必须时新 entry 加在「更具体/keyword 更长/最前」位置。改后跑 sim test 验证「*主卧卫生间*」归主卫 bucket 没破。

---

### Layer 2 — 楼层前缀 识别

**入口**:
- `CategoryPanel.StripFloorPrefix` + `FloorPrefixRegex` (CategoryPanel.cs:22-32) — 8 大类先 strip 再 keyword 匹配
- `RoomDetector.ExtractFloor` (RoomDetector.cs:275-281) — DetectRooms 期间给 `Room.FloorLevel` 赋值

**Regex pattern**:
```
🔧 CategoryPanel.cs:22-32
private static readonly Regex FloorPrefixRegex = new Regex(
    @"^\s*(1F|2F|3F|4F|5F|一楼|二楼|三楼|四楼|五楼|一层|二层|三层|首层)\s*",
    RegexOptions.Compiled);

🔧 RoomDetector.cs:275-281 (注意 lookahead 不同: 一-龥\d boundary)
var match = Regex.Match(text, @"(?:^\s*|[^一-龥\d])(1F|2F|...|首层)(?=\s|$|[^一-龥\d])");
```

**Config**: `QuoteConfig.TemplateSettings.FloorAliasMap` (QuoteConfig.cs:157-...) — 标准化映射:
```
{ "1F": "一楼", "2F": "二楼", ..., "一层": "一楼", "首层": "一楼" }
```

**Edge cases**:
- ⭕ **复式 CAD 文字没floor prefix** (写「客餐厅」不写「1F 客餐厅」) → DetectRooms 走「未指定」+ warning「未包含楼层前缀」 → fallback 一楼
- ⭕ **floorOverride 参数** (防呆流程): 强制覆盖 ground prefix; 矛盾 warning 但仍保 floorOverride (`RoomDetector.cs:120-130`)

**回归风险**: 加别名 (例「负一楼」「B1」) → **同时**改两处 pattern + FloorAliasMap, 别忘了 rev sim test。

---

### Layer 3 — Room → 6/8 大类归纳

**入口**: `CategoryPanel.MapToSixCategory` (CategoryPanel.cs:39-57)

**SixCats** (CategoryPanel.cs:19-20): `{ "客餐厅", "厨房", "卫生间", "主卧", "主卫", "卧室", "阳台", "外花园" }`

**逻辑**: 先 `StripFloorPrefix` 去前缀 → 遍历 RoomTypeMaps → 检查 core.Contains(keyword) → 命中后判断是否 SixCats 内 (含 → map.RoomType; 否 → "其他")。

**Edge cases**:
- ⭕ **Type 不在 6 类** (legacy config) → 返「其他」, 不入 bucket
- ⭕ **空名/纯数字** → 返「其他」, 不入 bucket
- ⭕ **v11 8 类升级**: 从 6 加 「主卫 / 阳台 / 外花园」 split, 防「主卧 / 卫生间」 误分

**回归风险**: 加新大类 (例「储物间」) → 加到 SixCats + RoomTypeMaps RoomType 配「卧室」之类; 否 sim 验 8 大类 bucket 排序无破。

---

### Layer 4 — 面板每层模板 dropdown → config

**入口**: Commands.cs:54-95 — BJ 流程读取 panel 选择写到 config

```
panel.GetTileSpecSelections()    → config.SelectedTileSpecs
panel.GetFloorTemplateSelections() → config.SelectedFloorTemplates
panel.SelectedTemplate           → config.TemplateSettings.ActiveTemplate (v14.2 add)
```

**关键代码段**:
- Commands.cs:54 — read panel.SelectedTemplate as `overrideTemplate`
- Commands.cs:60-66 — **v14.2 关键**: write back ActiveTemplate
- Commands.cs:71-79 — fill SelectedFloorTemplates
- Commands.cs:179-205 — Per-floor template path computation

**v14.2 write-back 代码** (Commands.cs:60-67):
```
// 🔧 v14.2 修复: 面板 dropdown 选的模板需写回 config.TemplateSettings.ActiveTemplate.
if (config?.TemplateSettings != null)
    config.TemplateSettings.ActiveTemplate = overrideTemplate;
```

**Edge cases**:
- ⚠️ **v14.2 关键**: 单层 + 选 mudiban + 客 None — 不写回 ActiveTemplate, ExcelExporter v14 fallback 读到 stale dizhuan → bug。**必 写回**。
- ⭕ 复式 dropdown 没选 → SelectedFloorTemplates 不含该 floor; fallback ActiveTemplate
- ⭕ per-floor template 是 runtime-only, 不持久化

**回归风险**: 别无脑删 v14.2 ActiveTemplate write-back — 否则回归「单层 mudiban + 客 None 不触发 special layout」 bug。

---

### Layer 5 — 面板每房间规格 dropdown → config (3-key)

**入口**: `ExcelExporter.FillRoomData` (ExcelExporter.cs:1366-1381) — 运行时查 SelectedTileSpecs 凑 k1→k2→k3

```
string floorKey = (room.FloorLevel ?? "").Trim();
string roomKey  = (room.RoomType ?? "").Trim();
string k1 = floorKey + "|" + roomKey;       // e.g. "一楼|客餐厅"
string k2 = "|" + roomKey;                  // fallback global
string k3 = roomKey;                        // backward-compat
resolved = try k1 → try k2 → try k3
```

**NONE marker** (QuotePanel.cs:69-72):
```
public const string NoneSpecValue = "<NONE>";
public static readonly TileSpecOption NoneSpecOption = new TileSpecOption
{
    Value = "<NONE>",
    Label = "<无/木地板>",
    IsNoneMarker = true
};
```

**Edge cases**:
- ⭕ 3-key 兑底顺序: v6 floor-keyed 主路径 → v6 全局 flatten → v5 old single-key 向后兼容
- ⭕ resolved == "<NONE>" → selectedSpecCached = null (跳 PHASE A, 走 v14 Path if mudiban) — ExcelExporter.cs:1379-1381
- ⭕ panel NONE Index 0 prepend 适用于单层 + 复式 (v14 统一)

**回归风险**: 加新 spec (例「300*600MM」) → 加 QuoteConfig.cs TileSpecOptions 那 section 一条 + Verify SpecSizeTokenRegex (QuotePanel.cs:67) 是否能标出; 若 IsDefault=true 该变 Panel default。

---

### Layer 6 — ParseTemplate (Excel 模板 → TemplateGroup)

**入口**: `ExcelExporter.ParseTemplate` (ExcelExporter.cs:550-660) — 扫 R8..最后行

**功能**: 抽 Excel 区 2 房间原型到 in-memory `TemplateGroup` 列表 (含 Name/RoomType/FloorLevel/Items/SubtotalRow/SourceTemplate); chrome 静态区 (七/八/九 直接费/综合/其它) 不入 groups。

**关键代码段**:
- ExcelExporter.cs:550-660 — 主循环 (每 row 一判)
- ExcelExporter.cs:562-597 — `currentGroup.Items` 累加 (列1数字 + 列2名)
- ExcelExporter.cs:567-576 — 小计/合计行识别
- ExcelExporter.cs:609-616 — `HouseLevelMarkers` array
- ExcelExporter.cs:618-625 — `IsHouseLevelMarker` 检测
- ExcelExporter.cs:1654-1657 — `GroupHeaderRegex`

**Regex pattern**:
```
private static readonly Regex GroupHeaderRegex = new Regex(
    @"^[一二三四五六七八九十]+、?$",
    RegexOptions.Compiled);
```

**Edge cases**:
- ⭕ **chrome 起点**: 一旦扫到「七 直接费 / 八 综合 / 九 其它」标志 (HouseLevelMarkers), 立即 break, 不再入 groups
- ⭕ **小计/合计**: 用 Contains 而非 c1=="" + EndsWith 双锚点 — 防 chrome 子段被 col1=「」 漏识别
- ⭕ **IsGroupHeader**: 仅匹配「一、~十、」 形态, 不是「一、」（含顿号变体） 可能漏掉

**回归风险**: 加新 chrome block entry (例「十、额外费」) → HouseLevelMarkers 加 tuple `("十", "额外费")`。改 GroupHeaderRegex 顺序防漏 (例「十一」)。

---

### Layer 7 — ResolveTemplates 8-tier 兑底链

**入口**: `ExcelExporter.ResolveTemplates` (ExcelExporter.cs:1018-1115)

**8-tier fallback chain**:

| Tier | Key | Type |
|---|---|---|
| 1 | (sourceTpl, cadFloor, cadType) | 严格 - v7 多模板主例 |
| 2 | (sourceTpl, "", cadType) | 源模板内通用 (无 floor 前缀) |
| 3 | (sourceTpl, cadFloor, fallbackType) | 源模板 + 类型 fallback (阳台→客餐厅) |
| 4 | (sourceTpl, "", fallbackType) | 源模板 + 通用 fallback |
| 5 | ("", cadFloor, cadType) | v6 兑底 - floor+type 查 |
| 6 | ("", "", cadType) | v6 兑底 - 完全通用 |
| 7 | allGroups that SourceTpl==sourceTpl + RoomType==cadType | 同模板同类型兜底 |
| 8 | allGroups (filter FloorLevel empty / "一楼" / first) | 最兜底 (CAD 无楼层时) |

**fallbackType 解析** (ExcelExporter.cs:1043-1049):
```
var fbMap = config?.TemplateSettings?.RoomTypeFallbackMap;
string fallbackType = null;
if (fbMap != null && !string.IsNullOrEmpty(cadType) && fbMap.TryGetValue(cadType, out var fb))
    fallbackType = fb;
// 防御: 旧 config 没 RoomTypeFallbackMap 时这两个强 fallback
if (fallbackType == null && (cadType == "阳台" || cadType == "外花园"))
    fallbackType = "客餐厅";
```

**Config `RoomTypeFallbackMap`** (QuoteConfig.cs:165-167):
```
{ "阳台": "客餐厅", "外花园": "客餐厅", "主卧": "客餐厅", "主卫": "卫生间" }
```

**Edge cases**:
- ⭕ **阳台/外花园** → fallback 「客餐厅」
- ⭕ **主卧** → fallback 「客餐厅」
- ⭕ **主卫** → fallback 「卫生间」
- ⭕ cadType null/空 → Dictionary.TryGetValue 抛 ArgumentNullException — 防御
- ⭕ **CAD 没 floor** vs 模板 selectedFloorTemplates 空 → tier 7/8 兜底

**回归风险**: 加 RoomType fallback → QuoteConfig.cs:165 加 k-v pair。改 fallbackType 链顺序影响所有 RoomType fallback 行为, 极慎重。

---

### Layer 8 — Spec NONE marker / Floor-Wall item 分类前置

**入口**: FillRoomData 入口处 `resolved == "<NONE>"` 检查 (ExcelExporter.cs:1379-1381)

```
if (resolved != null && resolved != "<NONE>")
    selectedSpecCached = resolved;     // 走 PHASE A (price override)
// else: selectedSpecCached = null     // 跳 PHASE A
```

**NONE 选项 prepend 入口**:
- QuotePanel.cs:190-194 (单层 UI)
- QuotePanel.cs:357 (复式 UI)

**Edge cases**:
- ⭕ 单层 + 复式统一 prepend (`NoneSpecOption` 添加位置 idx 0) — v14 确保
- ⭕ NONE marker 不挡 template 原价 — 走 v14 helper OR 走默认 PHASE B/C 路径

**回归风险**: 别删 NONE marker prepend — 客 None + 单层 fallback 路径 break。

---

### Layer 9 — v14 special trigger + ApplyLivingRoomNoneMudiban

**入口代码段** (ExcelExporter.cs:1383-1399):
```
string sourceTplV14 = "";
if (config?.SelectedFloorTemplates != null
    && !string.IsNullOrWhiteSpace(room?.FloorLevel)
    && config.SelectedFloorTemplates.TryGetValue(room.FloorLevel ?? "", out var _t14))
    sourceTplV14 = _t14;
else if (!string.IsNullOrEmpty(config?.TemplateSettings?.ActiveTemplate))
    sourceTplV14 = config.TemplateSettings.ActiveTemplate;       // 单层 fallback

bool isLivingRoomNoneMudiban = (resolved == "<NONE>")
    && _v14TriggerRoomTypes.Contains((room?.RoomType ?? "").Trim())   // v15 扩展
    && string.Equals(sourceTplV14, "mudiban", StringComparison.OrdinalIgnoreCase);
if (isLivingRoomNoneMudiban) {
    int n14 = ApplyLivingRoomNoneMudiban(ws, group, room, config);
    return n14;
}
```

**v14 helper 流程** (ExcelExporter.cs:1131-1228):
1. 找 `existingFloorLevelingRow` (含「地面找平」 first-or-default)
2. 找 `dirtRow` (`LastOrDefault` + 排除 7 wall keyword + 命中方法见下)
3. hashSet `handledRows` 去重
4. **3 case 分支**:
   - Case A/C: existing 找平 + dirtRow 同存 → 找平 fill ㎡ + dirtRow 0-out
   - Case B: 仅 dirtRow → rename「→地面找平」 + fill ㎡
   - Case C (no-cost fallback): 都 null → 找任何 IsFloorItem+DirtSpec+有 MM/砖/瓷/玻/石/木 keyword, 全 0-out
5. `protectRow` (含「地面保护」) C3=0
6. IsWallItem loop (用 handledRows skip 已处理) → wallArea fill

**dirtRow 命中规则** (ExcelExporter.cs:1166-1177):
```
排除 (!i.Name.Contains 各 1): 找平 / 保护 / 防水 / 墙面 / 顶面 / 乳胶漆 / 腻子 / 基层 / 勾缝
+ 命中 (任 1):
  A. canonical: 正铺地砖 / 铺地砖 / 抹地 / 客厅及餐厅地砖 / 客厅及餐厅正铺
  B. 通用 MM: DirtSpecRegex 命中 + 名 含「砖/瓷/玻/石/木」 任 1 材质 keyword
```

**static regex** (ExcelExporter.cs:209-211):
```
private static readonly Regex DirtSpecRegex = new Regex(
    @"\d+(?:[-*]\d+)?\s*MM[\)\uff09\s]*",
    RegexOptions.Compiled);
```

**v15 HashSet** (ExcelExporter.cs:213-223):
```
private static readonly HashSet<string> _v14TriggerRoomTypes = new HashSet<string>(StringComparer.Ordinal)
{
    "客餐厅", "阳台", "外花园"
};
```

**Edge cases**:
- ⚠️ 命中需排除「处理」 (v14.5 dropped — 误中「倒角处理」tile 相关)
- ⚠️ 双匹配歧义 → LastOrDefault 选末位 (v14.6)
- ⭕ Marker regex strip: `【v14 mudiban 客None:.*?】` 允许多次运行同 row 不累加

**回归风险**: 改 v15 HashSet → 改 QuoteConfig.cs RoomTypeFallbackMap 同 步 (eg 加「茶室」走 mudiban, 同时改 3 处 list)。

---

### Layer 10 — ItemFormulas 公式字典 (Bath/Kit/OuterGarden)

**入口**:
- `ExcelExporter.FindFormulaKey` (ExcelExporter.cs:1529-1541) — match ItemFormulas / OutdoorGardenFormulas 字典 key
- `CategoryPanel.AskBathroomKitchenFormulas` (CategoryPanel.cs:206-256) — 默认填 ItemFormulas
- `CategoryPanel.WriteOutdoorGardenFormulas` (CategoryPanel.cs:286-310) — 外花园 卷/非卷 公式

**Formulas 写入规则**:

| 类型 | IsWaterproofedRoll | ItemFormulas key | 数量算法 |
|---|---|---|---|
| 卫生间/主卫 | (不问) | 地面防水处理 | FloorArea |
| 卫生间/主卫 | (不问) | 地面防水保护层 | FloorArea |
| 卫生间/主卫 | (不问) | 墙面防水层 | Perimeter × defs.WaterproofHeight (2.0m) |
| 卫生间/主卫 | (不问) | 墙面贴瓷片 | Perimeter × defs.TileHeight - 门窗洞 |
| 卫生间/主卫 | (不问) | 墙面瓷砖刷背胶 | 同上 |
| 厨房 | (不问) | 地面防水处理 | FloorArea |
| 厨房 | (不问) | 墙面防水层 | Perimeter × defs.KitchenWaterproofHeight (0.6m) |
| 厨房 | (不问) | 墙面贴瓷片 | Perimeter × defs.TileHeight |
| 阳台 | (不问) | 地面防水处理 | FloorArea |
| 阳台 | (不问) | 墙面防水层 | Perimeter × defs.BalconyWaterproofHeight (0.6m) |
| 外花园 (Y 卷材) | true | 地面自粘防水卷材 | FloorArea + Perimeter × 0.8 |
| 外花园 (Y 卷材) | true | 卷材防水保护层 | 同上 |
| 外花园 (N 非卷材) | false | 地面防水处理 | FloorArea |
| 外花园 (N 非卷材) | false | 地面防水保护层 | FloorArea |
| 外花园 (N 非卷材) | false | 墙面防水层 | Perimeter × 0.6 |

**Key 匹配逻辑** (ExcelExporter.cs:1529-1541):
```
private static string FindFormulaKey(Dictionary<string, double> dict, string itemName)
{
    if (dict == null || string.IsNullOrEmpty(itemName)) return null;
    if (dict.TryGetValue(itemName, out _)) return itemName;
    var match = dict.Keys
        .Where(k => !string.IsNullOrEmpty(k) && (itemName.Contains(k) || k.Contains(itemName)))
        .OrderByDescending(k => k.Length)
        .FirstOrDefault();
    return match;
}
```

**Edge cases**:
- ⭕ **dict 找不到精确 key** → fallback substring match, 选 *最长 key* 优先
- ⭕ **ItemFormulas 为空 + IsWaterproofedRoll 有值** → Debug warning「OutdoorGardenFormulas 为空」
- ⚠️ **Math.Max** 极小房间贴瓷片面积 - 防负数

**回归风险**: 加新防水公式 → 同时改 CategoryPanel.WriteOutdoorGardenFormulas + config 默认值 + sim test 验 key 命中。

---

### Layer 11 — Floor / Wall item 关键词分类

**入口**:
- `ExcelExporter.GetFloorKeywords` (ExcelExporter.cs:1543-1548)
- `ExcelExporter.GetWallKeywords` (ExcelExporter.cs:1550-1554)
- `ExcelExporter.IsFloorItem` (ExcelExporter.cs:1557-1561)
- `ExcelExporter.IsWallItem` (ExcelExporter.cs:1613-1617)
- `ExcelExporter.IsTileishName` (ExcelExporter.cs:1563-1571) — anti-keyword set, 仅 floorItemCount==0 fallback

**Floor keywords (默认)**:
```
[ "铺地砖", "地砖", "地板", "正铺", "地面保护" ]
```

**Wall keywords (默认)**:
```
[ "墙顶面基层加固", "墙面基层处理", "鸟巢腻子", "芬琳芬华", "五合一", "内墙乳胶漆" ]
```

**Tileish anti-keywords** (ExcelExporter.cs:197-205):
```
[ "地砖", "正铺", "正贴", "菱铺", "菱贴" ]
// 故意不含「地板」「强化地板」「木地板」— 防mudiban 床屋被v4 误改为地砖+套价
```

**Edge cases**:
- ⚠️ 「地板」 在 Floor keywords 但不在 Tileish → v4 保护 — 主卧 mudiban 模板 不会 被「木地板」 误判
- ⚠️ IsFloorItem 命中+IsWallItem 同命中 → 走 IsFloorItem 分支 (priority: floor > wall)

**回归风险**: 改 Floor/Wall keywords → 加 sim 房间涵盖「地板」「墙顶面」「强化地板」 验证 IsFloorItem / IsWallItem 互斥。

---

### Layer 12 — Spec 匹配 + PHASE A (single-tpl price override)

**入口**:
- `ExcelExporter.IdentifyTileSpecMatch` (ExcelExporter.cs:1623-1648) — item.Name 命中 ALL spec.Match 子串 → spec.Value
- `ExcelExporter.BuildSpecItemName` (ExcelExporter.cs:1585-1611) — 构造「正铺地砖（750*1500MM）」 改名

**逻辑 (IdentifyTileSpecMatch)**:
```
var ordered = specs
    .Where(s => s?.Match != null && s.Match.Count > 0)
    .OrderByDescending(s => s.Match.Sum(m => (m?.Length ?? 0)))
    .ThenByDescending(s => s.Match.Count);

foreach (var spec in ordered)
    foreach (var m in spec.Match)
        allHit = itemName.Contains(m);    // ALL-hit, 任一 miss 整 spec 不 match
```

**PHASE A 触发条件** (ExcelExporter.cs:1417-1454):
```
bool isExactSpecRow = IdentifyTileSpecMatch(item.Name, config, room.RoomType) != null;
if (floorItemCount <= 1 && IsFloorItem(item.Name, config) && selectedSpecCached != null
    && config.TemplateSettings.TileSpecOptions.TryGetValue(room.RoomType ?? "", out var specList)
    && (isExactSpecRow || (floorItemCount == 0 && IsTileishName(item.Name))))
```

**BuildSpecItemName**:
```
prefix = 菱铺 → "菱铺地砖";  正铺 → "正铺地砖";  else "铺地砖"
size = spec.Match 首个非 keyword 数字串
return prefix + "（" + size + "MM）"
```

**Edge cases**:
- ⭕ **同 item 多 spec match** → 最长 keyword 总长 + 最多子串数 取胜 (防 blanket spec 误 win)
- ⭕ **菱铺 spec** → prefix only, size 从 opt.Label regex 抓 (`_specSizeTokenRegex` QuotePanel.cs:67)
- ⭕ `MaterialPrice.HasValue` OR `LaborPrice.HasValue` 任一存在 → 触发, 否则 skip
- ⚠️ PHASE A ordered 后 才 跑 PHASE B (ItemFormulas) 同 row 不会 双 覆 写 (PHASE A 在 前)

**回归风险**: 改 BuildSpecItemName prefix 顺序 (例 「菱铺」→「菱贴」) — 验证所有 spec 并 row 名重写一致性。

---

### Layer 13 — C3 数量 Fill (FloorArea / WallArea / formula)

**入口**: ExcelExporter.cs:1483-1520 per item row

**逻辑分 3 段**:
1. **PHASE A**: spec override (C5/C7/C9 marker) — Layer 12
2. **PHASE B**: Outdoor/Indoor formula (findFormulaKey) → 用 ItemFormulas 或 OutdoorGardenFormulas 的 dict 量
3. **PHASE C**: Floor/Wall item classification → FloorArea (floor) 或 WallArea (wall)

**Edge cases**:
- ⭕ floorItemCount >= 2 + spec_mismatch → C3=0 (multi-spec blank-on-mismatch)
- ⭕ floorItemCount <= 1 + spec_match + spec_selected → C3=FloorArea (dizhuan 单行模板)
- ⭕ ItemFormulas 命中 → C3=formulaQty, skip C3=FloorArea
- ⚠️ 顺序: PHASE A 在 前 (改 C5/C7/C9), 后 PHASE B/C 改 C3; 三 段 都 命中 时 B/A/C 不 互 覆

**回归风险**: 加 item.formula key → 同 步 加 CategoryPanel.WriteBathroomKitchenFormulas 默认 + QuoteConfig 不 需 (走 runtime)。

---

### Layer 14 — xlsx save + cleanup (InvalidDefinedNameRegex)

**入口**: `ExcelExporter.PreprocessInvalidDefinedNames` (ExcelExporter.cs:184-204)

**Regex pattern**:
```
🔧 ExcelExporter.cs:189-198
private static readonly Regex InvalidDefinedNameRegex = new Regex(
    @"<definedName[^>]*?name=""[^""]*[\\/?*\[\]:][^""]*""[^>]*?(?:/>|>.*?</definedName>)",
    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
```

**Logic**: 扫 wb workbook.xml; 命中 invalid (name 含 `\`/`?`/`*`/`[`/`]`/`:`) 则 strip 整个 definedName element; 若 definedNames 块变空, EmptyDefinedNamesRegex 再 strip。

**Edge cases**:
- ⭕ 模板源 wb 含 `\P` defined name → strip (ClosedXML 加载报错)
- ⭕ 处理失败 → fallback 原文件 (不 crash)

**回归风险**: 加新 invalid name 字符集合 → 改 regex char class。改后跑 build test 模拟「\P definedName」情况。

---

### Layer 15 — Floor ordering (写 入 OrderKey)

**入口**: `ExcelExporter.FloorOrderKey` (ExcelExporter.cs:1660-1675)

**规则**:
```
一 → 1000
二 → 2000
...
十 → 10000
未指定 → int.MaxValue - 1
```

**Edge cases**:
- ⭕ `未指定` (CAD 无 floor prefix) 排末位
- ⭕ 数字 (~decimal) 不 match → int 20000 fallback

**回归风险**: 加新 floor 别名 (例「阁楼层」=8500) → FloorOrderKey 加 if branch。

---

### Layer 16 — Master / Follower Spec sync (panel syncing)

**入口**: `QuotePanel.WireSpecMasterSync` + `_masterSpecRoomType` + `_followerSpecRoomTypes` (QuotePanel.cs:62-66, 657-707)

**Master**: `"客餐厅"` — 改 master = 同步改 followers
**Followers**: `{ "主卧", "卧室", "厨房", "阳台", "外花园" }` — 跟 master 联动

**Edge cases**:
- ⭕ **独立 RoomType** (卫生间/主卫) 不入 follower → 不联动
- ⭕ master 没选 (selectedSpec == null) → 不联动

**回归风险**: 加新 「follower」 RoomType → 加 进 `_followerSpecRoomTypes` 同 步 进 `_v14TriggerRoomTypes` (若 mudiban 兼容)。

---

### Layer 17 — 重复 detection (本层/跨层)

**入口**: `RoomDetector.IsDuplicate` (RoomDetector.cs:245-279)

**Logic**:
```
const double distanceTolerance = 500.0;   // mm (复式上下层间距)
const double areaTolerance = 0.1;         // ㎡
foreach (var g in currentLayerHits)        // 本层累积
    if (d < 500 && |area diff| < 0.1) → warn "本层重复"
foreach (var g in _previousBoundaries)     // 跨层已识别
    if 同样 → warn "跨层重复"
```

**Edge cases**:
- ⚠️ **distanceTolerance 100→500** (v9 修复 #9 放宽) — 防 复式 1F/2F 同位置房间 误 跨 层 重复
- ⭕ 退化多段线 (NumberOfVertices==0) → 用 GeometricExtents fallback

**回归风险**: 改 tolerance → 防 复式 上下 层 同位置房 间 被合并 跳。

---

## 3. 跨 Layer 依赖网络

```
[Layer 1 → Layer 3]  RoomDetector.MatchRoomType → CategoryPanel.MapToSixCategory
                          ↘ ExcelExporter.ClassifyRoom (parse-time)

[Layer 2 → Layer 6]  CategoryPanel.StripFloorPrefix + RoomDetector.ExtractFloor → ParseTemplate (FloorLevel)

[Layer 4 → Layer 7]  Commands.cs panel → SelectedFloorTemplates → ResolveTemplates (sourceTpl per floor)
                          ↘ Commands.cs v14.2 write-back ActiveTemplate → Layer 9 (single-layer fallback)

[Layer 5 → Layer 10/12]  panel SelectedTileSpecs (k1/k2/k3) → FillRoomData (NONE marker dispatch)

[Layer 7 → Layer 9]   ResolveTemplates → tplDict lookup mudiban group → ApplyLivingRoomNoneMudiban

[Layer 9 → Layer 8]   v14 helper 命中 NONE → 跳 PHASE A → 走 mudiban style

[Layer 10 → Layer 11]  ItemFormulas grep dict → fill row C3 (PHASE B)

[Layer 11 → Layer 12]  IsFloorItem/IsWallItem → IdentifyTileSpecMatch → PHASE A

[Layer 12 → Layer 13]  PHASE A override (C5/C7/C9) + PHASE C fill (C3)

[Layer 13 → Layer 14]  ws row 写入 → wb.Save() → PreprocessInvalidDefinedNames

[Layer 15]           FloorOrderKey → ProcessRooms 排序 (六层顺序一→十写入)

[Layer 16]           _masterSpecRoomType/_followerSpecRoomTypes → QuotePanel dropdown 联动

[Layer 17]           IsDuplicate (距 500mm) → 跳过重复房间
```

---

## 4. 维护 check-list (加新逻辑时 必 同步)

| 改动 场 景 | 必 同步文件/层 | 可能破 sim 测点 |
|---|---|---|
| 加新 RoomType (例「茶室」) | Layer 1 (RoomTypeMaps), Layer 3 (SixCats), Layer 7 (RoomTypeFallbackMap) | 主卧/卫生间 ordering; 主 bedroom 归类 |
| 加新 别名 (例「B1」) | Layer 2 (FloorPrefixRegex + FloorAliasMap) | 1F/2F/3F extract |
| 加新 Spec (例「300*600MM」) | Layer 5 (QuoteConfig TileSpecOptions) + Layer 12 (BuildSpecItemName path) | IsDefault=true 改 panel default |
| 加新模板 (例「环氧地坪」) | Layer 6 (ParseTemplate) + Layer 7 (key) + v14 mudiban gate | ResolveTemplates 兑底链熔融 |
| 加新 default formula (例「窗台板」) | Layer 10 (CategoryPanel.Write*) + config default | ItemFormulas key 命中 |
| 加新防水公式 (例「阳台 自粘 防水卷材」) | Layer 10 + BathroomKitchenDefaults + sim test | 卷/非卷公式互斥 |
| 加新 v14 trigger RoomType | Layer 9 (_v14TriggerRoomTypes) + Layer 7 (FallBackMap like 客餐厅) + Layer 16 (master/follower if shared) | marker regex strip consistency |
| 改 v14 helper (rename 「地面找平」 等) | Layer 9 marker regex strip 串 + 3 处 Debug log wording | 多次运行 row marker 累 加 |
| 改 master/follower 联动 | Layer 16 (_masterSpecRoomType/_followerSpecRoomTypes) | panel sync 跨层回退 |
| 改 invalid name char set | Layer 14 (InvalidDefinedNameRegex) | 加载 defence |

---

## 5. 测试覆盖矩 阵

| 测 | 覆 盖 layer | 现 status |
|---|---|---|
| `scripts/verify_dizhuan_override_v3.py` | L1/L4/L7/L11/L12 (dizhuan path) | ✅ 38/0 |
| Manual: 单层 + mudiban + 客 None | L4 (ActiveTemplate write-back) + L9 (v14 trigger) | ✅ user confirmed |
| Manual: 复式 + mudiban + 客 None | L4 (SelectedFloorTemplates) + L9 | ✅ |
| **GAP: 单层 + mudiban + 阳台 None** (v15 extend) | L9 (_v14TriggerRoomTypes new set) | ❌ 缺 sim |
| **GAP: 单层 + mudiban + 外花园 None** (v15 extend) | L9 | ❌ 缺 sim |
| **GAP: 复式 + mudiban + 阳台 None** (v15 extend) | L4 + L9 | ❌ 缺 sim |

---

## 6. 已知 边界 + TODO

| 编号 | 描 述 | 提案 解 |
|---|---|---|
| TODO 1 | sim test 缺 mudiban + 阳台/外花园 None runtime 验证 | 加 Python sim `verify_mudiban_None.py` (阳台/外花园 case) |
| TODO 2 | Marker 文案 偏 客 (`【v14 mudiban 客None: ...】`) | 抽常量 + 改 marker regex strip 为「mudiban 主项None」 |
| TODO 3 | `_v14TriggerRoomTypes` hard-coded | auto-derive from `RoomTypeFallbackMap (kv.Value == "客餐厅")` |
| TODO 4 | Helper 名 `ApplyLivingRoomNoneMudiban` stale | rename 为 `ApplyNoneMudibanTilegRoundRoom` |
| TODO 5 | 「石材铺贴」 等 exotic 行 (无 MM+砖keyword) 漏 | zero-fill fallback 已加 keyword gate, 仍是 95% cover |
| TODO 6 | config-driven rename 「→地面找平」 | 加 QuoteConfig.json `MudibanNoneNewName` entry |

---

## 7. 维护 手册

**最快 查询**:

1. "CAD 文字 错 配 RoomType" → Layer 1
2. "面 板 没 有 dropdown 选 项" → Layer 5, 8
3. "xlsx 出 现 「错行 被 改」" → Layer 9 (dirtRow LastOrDefault + material gate)
4. "复式 跨 层 template 不 走" → Layer 4, 7
5. "防 水 公式 没 填" → Layer 10
6. "Chrome 静 态 块 被 解 析 入 groups" → Layer 6 (HouseLevelMarkers)
7. "PHASE A 没 改 价" → Layer 12 (PHASE A gate)
8. "xlsx 加 载 报 definedName 错" → Layer 14

**新 加 功 能 时**:
- 先 想 「属 哪 层」 → 查本 文档 at 维 护 check-list
- 改 完 跑 sim test → 验 没 regression
- 严格 记 录 该 处 「minimatch/keyword regex」 → 加 测 例 覆盖

---

**最后更 新**: 2026-07-14 · v15 ship · 需 v16+ 改后 更 新本 doc
