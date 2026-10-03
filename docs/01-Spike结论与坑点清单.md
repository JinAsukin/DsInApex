# DIA · Spike 结论与坑点清单

> 执行日期：2026-10-03
> 执行环境：Windows 11 (10.0.26100) / .NET SDK 10.0.203 / WindowsAppSDK 2.5.1
> Spike 工程：`E:\DsInApex\spike\DsInApex.Spike\`
> 结论：**技术路线可行，方案 A（WinUI 3 全量重构）确认执行**，但 P1 需增加若干未预估的工作项。

---

## 0. 结论先行

| 判断 | 结果 |
|---|---|
| WinUI 3 unpackaged + self-contained 能否跑通 | ✅ 可以 |
| WinUI 3 语言运行时热切换能否实现 | ✅ 可以，**自建附加属性机制**，刷新 0.4ms |
| 上游 `{DynamicResource}` 依赖是否必须重建 | ✅ **必须重建**（编译器级证实 WinUI 3 无此标记） |
| 上游 XAML 能否低改动迁移 | ❌ **不能**，`DockPanel` 等容器在 WinUI 3 中不存在 |
| 工期影响 | P1/P2/P3 需上调，主要来自 **`Trigger` 体系重写(36 处)** + 容器替换(21 处) + 121 条文案中文化 |

---

## 1. 验证结果总表

| # | 验证项 | 优先级 | 结果 | 证据 |
|---|---|---|---|---|
| S1 | WinUI 3 项目可构建运行 | 必过 | ✅ **通过** | 窗口句柄 462194，标题 `Ds in Apex · Spike`，Responding=True，无 COMException |
| S2 | **语言运行时热切换** | 必过 | ✅ **通过** | 13–16 个控件实际属性值随语言切换同步变更，耗时 **0.402 / 0.373 ms**，无需重启 |
| S3 | 托盘图标可用 | 必过 | ✅ **通过** | `IsCreated=True`；注册表 `HKCU\Control Panel\NotifyIconSettings` 出现本程序记录 |
| S4 | 拉起 C++ 引擎并解析输出 | 必过 | ✅ **通过** | 手柄在位后成功解析出设备（`Flydigi VADER3` / `04B4:2412` / `usage FFA0:0001` / `reports 32:32`），退出码 0，耗时 54ms。期间抓出并修复了 `0x` 前缀解析 bug —— 详见 `docs/02-P1开工障碍清除报告.md` |
| S5 | 单实例 + 最小化到托盘 | 期望 | ✅ **通过** | 双实例并发启动，进程计数恒为 **1**；`AppWindow.Closing` 拦截关闭并隐藏 |
| S6 | FilePicker 可用 | 期望 | ⏳ **待人工验证** | `InitializeWithWindow` 已在代码中就位，需人工点击触发 |

### S2 实测数据（核心攻关闭环）

```
16:05:33  init             lang=zh-CN   refreshCount=1   lastRefreshMs=2.024   registered=13
16:05:36  language-changed lang=en      refreshCount=2   lastRefreshMs=0.402   ← 13 个控件全部变英文
16:05:38  language-changed lang=zh-CN   refreshCount=3   lastRefreshMs=0.373   ← 全部切回中文
```

覆盖的目标属性：

| 控件类型 | 目标属性 | 结果 |
|---|---|---|
| TextBlock | `Text` | ✅ |
| Button | `Content` | ✅ |
| CheckBox | `Content` | ✅ |
| RadioButton | `Content` | ✅ |
| TextBox | `PlaceholderText` | ✅ |
| ComboBoxItem | `Content` | ✅ |
| MenuFlyoutItem（托盘菜单） | `Text` | ✅ |

**性能余量**：单次刷新 ≈ `0.03 ms/元素`。即使 P1 扩展到 8 个页面、500 个本地化元素，全量刷新仍在 ~15 ms 量级，肉眼无感。

---

## 2. 对计划文档的重要修正

### 2.1 「121 键 × 2 语言」→ 中文是全新翻译，不是迁移

计划 §6 写"沿用现有 `LocalizationManager.cs` 的 120 个键"。
实测：**上游只有 English 与 French 两种语言，共 121 个键，没有任何中文**。

| 项 | 计划假设 | 实际情况 |
|---|---|---|
| 键数 | 120 | **121**（吻合） |
| 语言数 | 隐含中英 | **en + fr**（无中文） |
| 中文来源 | 迁移 | **必须全新翻译 121 条** |
| 品牌名替换 | 未提 | 需把 `ApexSenseBridge` 逐条替换为 `Ds in Apex` |

**影响**：P1 的"本地化基础设施落地"实际包含 121 条中文翻译工作量，需单列。

### 2.2 引擎目录结构（已在《00-开发区域说明.md》记录）

计划 §3.1 的 `src\DsInApex.Engine\ ← 同步自上游 src/` **技术上不可行**：
根 `CMakeLists.txt` 在仓库根，且依赖 `shared/`、`third_party/`。
实际落地为 `engine\` 整仓库镜像。

### 2.3 H.NotifyIcon 版本与 API 需锁定

| 项 | 说明 |
|---|---|
| 计划写法 | 只写了包名，未定版本 |
| 实测结论 | 稳定版最新 = **2.4.1**；2.5.0 仅有 beta |
| 陷阱 | **官方 README 的示例是 2.5.0-beta 的 API** —— README 里的 `ContextMenu` / `ContextMenuThemeMode` 属性在 2.4.1 中**不存在** |
| 2.4.1 正确写法 | 菜单挂 `FrameworkElement.ContextFlyout`（继承属性），配 `ContextMenuMode="PopupMenu"` + `MenuActivation="RightClick"` |

### 2.4 WindowsAppSDK 2.x 的包体积问题（新增待办）

2.5.1 主包是**元包**，实际拆分为多个子包：

```
Microsoft.WindowsAppSDK.Base                     2.0.4
Microsoft.WindowsAppSDK.Foundation               2.3.12
Microsoft.WindowsAppSDK.InteractiveExperiences   2.1.9
Microsoft.WindowsAppSDK.WinUI                    2.3.9
Microsoft.WindowsAppSDK.DWrite                   2.1.0
Microsoft.WindowsAppSDK.Widgets                  2.0.5
Microsoft.WindowsAppSDK.AI                       2.5.5   ← DIA 完全用不到
Microsoft.WindowsAppSDK.ML                       2.1.94  ← DIA 完全用不到
Microsoft.WindowsAppSDK.Search                   2.5.5   ← DIA 完全用不到
Microsoft.WindowsAppSDK.Runtime                  [2.5.1]
```

**实测自包含产物 = 236 MB / 445 个文件**，其中夹带了 `DirectML.dll`、`Microsoft.ML.OnnxRuntime.dll`、
`Microsoft.Asg.SemanticIndex.AiFabric.Compatibility.dll` 等 AI 组件。

**P1 待办**：改为只引用必需子包（Base / Foundation / WinUI / Runtime），目标把体积压到 80–100 MB。
此项对安装包体积与更新下载量影响显著。

---

## 3. 坑点清单

### 坑 1 · WinUI 3 不存在 `DockPanel`（影响面最大）

- **现象**：`XamlCompiler error WMC0001: Unknown type 'DockPanel'`
- **根因**：`DockPanel` 是 WPF 专有（`System.Windows.Controls`）。WinUI 3 继承 UWP 的 XAML 体系，**从未包含过 DockPanel**。
- **实测清单**（在 `Microsoft.UI.Xaml.winmd` 中核对）：

| 控件 | WinUI 3 |
|---|---|
| `DockPanel` | ❌ 不存在 |
| `WrapPanel` | ❌ 不存在 |
| `GridSplitter` | ❌ 不存在 |
| `StackPanel` / `Grid` / `RelativePanel` / `Canvas` / `VariableSizedWrapGrid` | ✅ 存在 |

- **解法**：`DockPanel` → `Grid` 加列定义（本次 Spike 已实测改写通过）。
- **影响**：上游 `MainWindow.xaml`(30.5 KB)、`GameListWindow.xaml`(55.5 KB) **大量使用 DockPanel**，
  P2/P3 的 XAML 工作量必须上调。

#### 附：上游 XAML 全量控件普查（本次已完成，编译器级证据）

对上游全部 7 个 XAML 文件（共约 104 KB）做了元素统计，并用编译器探针一次性验证。
**这是 P1 的关键输入，建议直接作为 XAML 重写的工作清单。**

上游 XAML 文件与体积：

| 文件 | 体积 | 去向 |
|---|---|---|
| `ApexSenseBridgeTray/GameListWindow.xaml` | 57,593 B | → P3 游戏库 |
| `ApexSenseBridgeTray/MainWindow.xaml` | 31,750 B | → P2 仪表盘 + 设置 |
| `playnite/ApexSenseBridge/ApexSenseBridgeSettingsView.xaml` | 8,859 B | → P9 |
| `ApexSenseBridgeTray/LearnedExecutablesWindow.xaml` | 5,836 B | → P3 学习记录 |
| `ApexSenseBridgeTray/App.xaml` | 332 B | → P1 |
| `playnite/ApexSenseBridge/App.xaml` | 596 B | → P9 |
| `playnite/ApexSenseBridge/Localization/en_US.xaml` | 261 B | → P9 |

**必须替换的项**（编译器实测报错）：

| 缺失项 | 上游处数 | 严重度 | 替代方案 |
|---|---|---|---|
| **`Trigger` / `MultiTrigger` / `Condition`** | **36** | 🔴 **最高** | 改用 `VisualStateManager`（见坑 1-A） |
| `DockPanel` | 19 | 🔴 高 | → `Grid` + 行列定义 |
| `DataGrid` + `DataGridTextColumn` | 13 | 🟡 中 | → `CommunityToolkit.WinUI.Controls.DataGrid` |
| `WrapPanel` | 2 | 🟡 中 | → `ItemsWrapGrid` / `VariableSizedWrapGrid` / Toolkit `WrapPanel` |
| `DropShadowEffect`（及 `UIElement.Effect`） | 2 | 🟢 低 | → `ThemeShadow` 或 Composition API |
| `BooleanToVisibilityConverter` | 1 | 🟢 低 | → 自写 `IValueConverter` 或 Toolkit |
| `GridSplitter` | 0（探针验证，上游未用） | — | 将来需要时用 Toolkit |

**确认可安全迁移的项**（编译器通过，无需改动）：

`TextBlock`(77) · `Border`(62) · `StackPanel`(40) · `Grid`(34) · `Button`(24) ·
`RowDefinition`(23) · `ColumnDefinition`(17) · `ContentPresenter`(11) · `CheckBox`(10) ·
`RadioButton`(8) · `Path`(7) · `ItemsControl`(6) · `TextBox`(5) · `ScrollViewer`(5) ·
`Viewbox`(4) · `ScaleTransform`(4) · `ResourceDictionary`(4) · `Image`(3) ·
`VirtualizingStackPanel`(2) · `UserControl`(2) · `ItemsPanelTemplate`(2) · `DataTemplate`(2) ·
`ToggleButton` · `ProgressBar` · `Popup` · `Ellipse` · `ComboBox` · `ControlTemplate`(32) ·
`Style`(21) · `Setter`(190)

> `VirtualizingStackPanel` 值得特别说明：它**在 WinUI 3 中存在**，这是本次普查的一个反直觉结论。

### 坑 1-A · WPF 的样式触发器体系在 WinUI 3 中完全不存在（比 DockPanel 更严重）

- **现象**：`Unknown type 'Trigger'`、`Unknown member 'Triggers'`、`Unknown type 'MultiTrigger'`
- **根因**：WPF 用 `Style.Triggers` 声明式描述状态变化；WinUI 3 继承 UWP 体系，
  **状态变化一律走 `VisualStateManager`**，没有 `Trigger` 这个概念。
- **规模**：上游共用 **32 个 `Trigger` + 2 个 `MultiTrigger`（含 2 个 `Condition`）= 36 处**
- **影响**：
  - 这 36 处不是简单替换标签名，而是要**把声明式触发器改写成 VisualState + 状态组**，
    属于结构性改写，单处成本远高于 `DockPanel` → `Grid`
  - 涉及 hover / pressed / checked / focus 等交互视觉反馈，
    集中在 `GameListWindow.xaml` 与 `MainWindow.xaml` 的样式区
- **建议**：P1 做控件普查时**必须把这项列为首要风险**，并在 M1 阶段先做一个
  "Trigger → VisualStateManager" 的样式重写样板，验证工作量再排期
- **好消息**：`ControlTemplate`(32) 与 `Style`/`Setter`(21/190) 本身可用，
  主要是 `Style.Triggers` 这一段需要重构，不是整个模板推倒重来

### 坑 2 · WinUI 3 不支持 `{DynamicResource}`（已证实）

- **现象**：`XamlCompiler error WMC0001: Unknown type 'DynamicResource'`
- **同一份 XAML 中 `{ThemeResource}` 编译通过** → 证明是 `DynamicResource` 本身缺失，而非资源问题
- **解法**：自建附加属性机制（见 §4），已跑通
- **注意**：这是**编译期错误**，不是运行时静默失效 —— 上游 70 处改动会一次性全部爆出来，不会漏

### 坑 3 · WinUI 3 的 `Window` 没有 `Resources` 属性

- **现象**：想把 `MenuFlyout` 定义在 `Window.Resources` 里供 `StaticResource` 引用，无处可放
- **根因**：WPF 的 `Window` 继承 `FrameworkElement`（有 Resources）；WinUI 3 的 `Window` 不是 `FrameworkElement`
- **解法**：资源放根 `Grid.Resources`，或直接内联到使用处（Spike 采用内联）

### 坑 4 · `list` 命令不支持 `--json`，且无设备时退出码是 2

- **事实**：

| 命令 | `--json` |
|---|---|
| `diagnose` | ✅ |
| `input-status` | ✅ |
| `virtual-ds` | ✅ |
| **`list`** | ❌ 只能正则解析文本 |

- **另一个陷阱**：`list` 在找不到设备时**返回退出码 2**。若按惯例用 `ExitCode == 0` 判成功，
  会把"没插手柄"误判为"命令失败"，需要区分处理。
- **解法**：解析层集中管理正则 + **解析失败降级为显示原始文本**，绝不抛异常（Spike 已实现并通过）
- **参考**：P1/P5 中 `diagnose` 与 `input-status` 应优先走 `--json`，只有 `list` 需要文本解析

### 坑 5 · 托盘图标被 Windows 11 自动折叠

- **现象**：`IsCreated=True` 但通知区域看不到图标
- **根因**：Windows 11 默认把新出现的托盘图标收进「隐藏的图标」面板
- **证据**：注册表 `HKCU\Control Panel\NotifyIconSettings` 中该图标的 `IconOrder` 为空 = 未固定
- **影响**：产品层需考虑首次启动引导用户固定图标，否则用户会以为程序没启动

### 坑 6 · `RadioButton.Checked` 会在 XAML 解析阶段抢先触发

- **现象**：语言选择器在初始化期间误触发一次语言切换
- **解法**：`_ready` 护栏标志，置位前一律忽略（上游 WPF 版同样有 `isInitialized` 护栏，属同源问题）

### 坑 7 · `dotnet new` 没有 WinUI 模板

- **现象**：`dotnet new list` 只有 WPF 模板
- **解法**：两条路 ——
  1. 手写 csproj（本次 Spike 采用，配置项见 §5）
  2. 用 `winapp new -t winui-navview` 调官方模板（已装 `Microsoft.WinAppCli` 0.7.1，**P1 建议走这条**，能直接拿到 NavigationView 骨架）

### 坑 8 · 本机取证手段受限（环境特有，非通用坑）

- **现象**：`Add-Type` 被安全策略拦截，无法用 PowerShell 编译 P/Invoke 或加载 `System.Drawing` 截图
- **绕行方案**（已验证有效）：
  - 窗口存在性 → `Get-Process` 的 `MainWindowHandle` / `MainWindowTitle` / `Responding`
  - 托盘图标存在性 → 查 `HKCU\Control Panel\NotifyIconSettings`
  - UI 状态取证 → **由应用自身把控件实际属性值写入临时文件**（本次探针机制）

### 坑 9 · 上游许可证文档自相矛盾

- **事实**：`LICENSE` 文件写明 **GPL-3.0-or-later**；
  但 `README.md` 第 6 行的 badge 与第 206 行都写 **MIT License**
- **结论**：**以 LICENSE 文件为准**，DIA 必须按 GPL-3.0-or-later 开源
- **DIA 待办**：DIA 自己的 README 必须写对，且保留上游版权声明 + 标注"已修改"

---

## 4. 最终技术选型

### 4.1 语言热切换：候选 1（自定义附加属性）胜出

三个候选方案的裁决：

| 方案 | 裁决 | 理由 |
|---|---|---|
| **候选 1 · 自定义附加属性** | ✅ **采用** | XAML 干净、可覆盖任意目标属性、不依赖绑定引擎特性、实测 0.4ms |
| 候选 2 · VM 属性 + x:Bind | 备用 | 可行但每个页面 VM 都要暴露一遍属性，代码量爆炸 |
| 候选 3 · 索引器 + x:Bind | 未验证 | 候选 1 已跑通，无需继续 |

**落地形态**：单一附加属性 + 按控件类型智能路由（比计划里的 `Localize.Text` / `Localize.Content` 双属性更省事）。

```xml
<TextBlock loc:Localize.Key="Loc_AppName" />
<Button    loc:Localize.Key="Loc_BtnClose" />
<TextBox   loc:Localize.Key="Loc_SearchPlaceholder" />
<MenuFlyoutItem loc:Localize.Key="Loc_TrayExit" />
```

路由表（`Localize.Apply`）：

| 控件类型 | 写入目标 |
|---|---|
| `TextBlock` | `Text` |
| `TextBox` / `PasswordBox` | `PlaceholderText` |
| `RichTextBlock` | `Blocks` |
| `ContentControl`（含 Button/CheckBox/RadioButton/ComboBoxItem/NavigationViewItem…） | `Content` |
| `MenuFlyoutItem` | `Text` |
| 其他 | 兜底写入 `ToolTipService.ToolTip`（不静默丢失） |

刷新机制：`List<WeakReference<DependencyObject>>` 注册表，语言变更时遍历重刷，死引用顺带清理。

### 4.2 其余选型

| 项 | 定版 | 备注 |
|---|---|---|
| TFM | `net10.0-windows10.0.26100.0` | `TargetPlatformMinVersion` = 10.0.19041.0 |
| 部署 | `WindowsPackageType=None` + `WindowsAppSDKSelfContained=true` + `SelfContained=true` | 实测可行 |
| WindowsAppSDK | 2.5.1 | 建议 P1 改为只引子包 |
| H.NotifyIcon.WinUI | **2.4.1**（稳定版上限） | 菜单走 `ContextFlyout` |
| WinUIEx | 2.9.3 | net8.0 目标，可被 net10 引用 |
| CommunityToolkit.Mvvm | 8.4.2 | |
| Microsoft.Extensions.DependencyInjection | 10.0.12 | |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | |

---

## 5. 可复用项目模板

Spike 工程本身即为 P1 的起点，以下三处可直接搬：

### 5.1 csproj 关键配置

```xml
<PropertyGroup>
  <TargetFramework>net10.0-windows10.0.26100.0</TargetFramework>
  <TargetPlatformMinVersion>10.0.19041.0</TargetPlatformMinVersion>
  <UseWinUI>true</UseWinUI>
  <ApplicationManifest>app.manifest</ApplicationManifest>
  <RuntimeIdentifier>win-x64</RuntimeIdentifier>

  <!-- 部署模式 -->
  <WindowsPackageType>None</WindowsPackageType>
  <WindowsAppSDKSelfContained>true</WindowsAppSDKSelfContained>
  <SelfContained>true</SelfContained>

  <PublishTrimmed>false</PublishTrimmed>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

### 5.2 可直接复用的源码文件

| 文件 | 说明 | P1 处置 |
|---|---|---|
| `Localization/Localize.cs` | 附加属性机制 + 诊断导出 | **直接复用**，补充显式 Header/ToolTip 变体 |
| `Localization/LocalizationManager.cs` | 语言解析/回退/格式化 | **直接复用**，接入 `tray_settings.json` 持久化 |
| `Localization/Strings.cs` | 字典结构 | 复用结构，替换为 121 条完整中英对照 |
| `Services/EngineRunner.cs` | 引擎进程调用 | **直接复用**，路径定位换 `InstallLocator` |
| `Services/EngineOutputParser.cs` | `list` 文本解析 | **直接复用** |
| `Models/FlydigiDeviceInfo.cs` | 设备模型 | 直接复用 |
| `app.manifest` | asInvoker + PerMonitorV2 + UTF-8 | 直接复用 |

### 5.3 app.manifest 要点

```xml
<requestedExecutionLevel level="asInvoker" uiAccess="false" />
<dpiAwareness>PerMonitorV2</dpiAwareness>
<longPathAware>true</longPathAware>
<activeCodePage>UTF-8</activeCodePage>
```

**`asInvoker` 是关键**：主 UI 保持普通权限，驱动安装走独立提权进程（计划 §P4 的设计前提）。

---

## 6. 对 P1 的输入

### 6.1 新增工作项（计划中未列）

| # | 工作项 | 说明 |
|---|---|---|
| 1 | ~~WinUI 3 控件普查~~ | ✅ **本次 Spike 已完成**，结果见坑 1 附表 |
| 2 | ~~`Trigger` → `VisualStateManager` 样板~~ | ✅ **已完成**，改写规则与实测验证见 `docs/02-P1开工障碍清除报告.md` |
| 3 | **WindowsAppSDK 子包裁剪** | 从 236MB 压到 ~100MB |
| 4 | **121 条中英文案全量整理** | 含品牌名从 `ApexSenseBridge` 改为 `Ds in Apex` |
| 5 | **`tray_settings.json` 旧版迁移** | 读 `%LOCALAPPDATA%\ApexSenseBridge\tray_settings.json` 的 `Language` 字段，兼容 `auto`/`en`/`fr`，新增 `zh-CN` |

### 6.2 建项建议

P1 建议直接用官方模板起步，避免手写遗漏：

```bash
winapp new -t winui-navview -n DsInApex.App -o E:\DsInApex\src\DsInApex.App
```

再叠加本次 Spike 验证过的：
- 部署模式配置（§5.1）
- 本地化机制（§5.2 前两项）
- 单实例守卫（命名 Mutex `Local\` 前缀）
- 引擎调用层

### 6.3 待补验项

| # | 项 | 前置条件 |
|---|---|---|
| 1 | ~~S4 真实设备解析~~ | ✅ **已完成**（2026-10-03，手柄 DInput 模式。结果见 `docs/02-P1开工障碍清除报告.md`） |
| 2 | S6 FilePicker 实际弹出 | 人工点击（`InitializeWithWindow` 预检已通过） |
| 3 | 托盘右键菜单实际弹出与响应 | 人工点击（注册表已证明图标存在） |
| 4 | 中英切换在托盘菜单上的实际观感 | 人工确认 |

---

## 7. 复现方式

```bash
# 构建
cd E:\DsInApex\spike\DsInApex.Spike
dotnet build -c Debug

# 常规运行（手动界面验证）
bin\Debug\net10.0-windows10.0.26100.0\win-x64\DsInApex.Spike.exe

# 自动化验证（自动切换中英 + 调用引擎，并把控件实际值写入临时文件）
set DSINAPEX_SPIKE_AUTOTEST=1
bin\Debug\net10.0-windows10.0.26100.0\win-x64\DsInApex.Spike.exe

# 取证文件
%TEMP%\dsinapex_s2_probe.txt      当前状态快照
%TEMP%\dsinapex_s2_history.txt    完整时间线（追加）

# 指定引擎路径（可选）
set DSINAPEX_ENGINE_PATH=E:\...\ApexSenseBridge.exe
```

---

## 8. 一句话总结

**方案 A 的可行性风险已经出清**：最要命的"语言热切换"不但跑通，而且性能余量极大（0.4 ms）。
真正的工期风险从"技术能不能做"转移到了"体力活有多少"，按体量排序：

1. **`Trigger` → `VisualStateManager` 重写（36 处）** —— ✅ 规则已确定、样板已实测验证
2. **容器替换**：`DockPanel`(19) + `WrapPanel`(2) → `Grid`
3. **121 条中英文案全量翻译** + 品牌名 `ApexSenseBridge` → `Ds in Apex`
4. **`DataGrid` 引入 CommunityToolkit**（13 处）

> 原列的"WindowsAppSDK 子包裁剪"已实测**否决**：元包承担 buildTransitive wiring，
> 不可替换为子包；`ExcludeAssets` 也无效。改为 P7 安装器压缩解决。

**P1 开工前的技术障碍已全部清除。** 详见 `docs/02-P1开工障碍清除报告.md`。
