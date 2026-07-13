# BaoJiaCAD - AutoCAD 一键报价插件

基于 AutoCAD .NET API 开发的装饰工程一键报价插件。

## 功能

- 框选 CAD 图中的房间文字和墙线
- 自动识别房间类型（客厅、厨房、卧室等）
- 追踪闭合边界，检测墙线是否闭合并提示
- 计算地面面积、墙面面积
- 按模板格式生成 Excel 报价单

## 技术栈

- C# / .NET Framework 4.8
- AutoCAD .NET API
- ClosedXML 0.97.0（Excel 生成，MIT 许可 — 已避开 SixLabors.Fonts 2.x 在 net48 上的 `TableLoader` 类型初始化异常）
- Newtonsoft.Json（配置读取）

### 模板三区模型（`ExcelExporter.cs`）

```
R1..R7          顶部 7 行          — logo / 标题 / 工程名称，原状保留
R8..八综合 前区  房间原型区         — 软件识别房间后 CloneGroupInPlace 写入
八综合..末尾     chrome 静态块     — 整段移动到房间后面，不解析、不写公式、不清列
```

如果改了模板 (加入/调整 chrome 子段)，不要重走旧的 `FixChromeFormulas` / 『其它』段 Clear 路径。详见 [`docs/ClosedXML踩坑记录.md`](docs/ClosedXML踩坑记录.md) 第 5.1 节。

## 项目结构

```
BaoJiaCAD/
├── BaoJiaCAD.csproj    # 项目文件
├── Commands.cs         # AutoCAD 命令入口
├── Room.cs             # 房间和报价项数据模型
├── QuoteConfig.cs      # 配置读取
├── BoundaryHelper.cs   # CAD 边界检测辅助
├── RoomDetector.cs     # 房间识别逻辑
├── ExcelExporter.cs    # Excel 导出
└── config.json         # 默认配置
```

## 编译说明

1. 安装 Visual Studio 2022
2. 修改 `BaoJiaCAD.csproj` 中的 `AutoCADDir`，指向 AutoCAD 安装目录，例如：

```xml
<PropertyGroup>
  <AutoCADDir>C:\Program Files\Autodesk\AutoCAD 2024</AutoCADDir>
</PropertyGroup>
```

或者在命令行编译时传入：

```bash
dotnet build -p:AutoCADDir="C:\Program Files\Autodesk\AutoCAD 2024"
```

## 使用方法

1. 编译生成 `BaoJiaCAD.dll`
2. 在 AutoCAD 中输入 `NETLOAD`，加载 `BaoJiaCAD.dll`
3. 输入命令 `BJ`
4. 按提示框选所有房间区域（包含房间名称文字和墙线）
5. 输入墙面高度（默认 2800mm）
6. 选择保存路径，生成报价单

## 配置说明

`config.json` 中可以配置：

- `DefaultWallHeight`：默认墙面高度（mm）
- `RoomTypeMaps`：房间类型关键字映射
- `QuoteItems`：报价项目、单价、计算规则

### 计算规则

- `Floor`：按地面面积计算
- `CeilingAndWall`：按地面面积 + 周长 × 高度计算

## 注意事项

- 图纸单位应为毫米（mm）
- 房间名称文字应放在对应空间内部
- 墙线应尽量闭合，否则插件会提示未闭合

## 后续计划

- [ ] 支持扣除门窗洞口面积
- [ ] 支持更多报价项目
- [ ] 支持读取用户自定义模板
- [ ] 支持批量导出多个工程
