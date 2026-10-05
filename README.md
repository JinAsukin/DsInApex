# Ds in Apex

**把飞智八爪鱼 4（APEX 4）手柄在 PC 上虚拟成原生 PS5 DualSense 的 Windows 客户端。**

目标是让一块「国产手柄」在 PC 游戏里获得只有原生 DualSense 才有的两样东西：

- **自适应扳机（Adaptive Trigger）** —— 通过 FORCEADAPT 反馈驱动 L2/R2 阻尼
- **音频触觉转译（Audio Haptics）** —— 游戏写给虚拟手柄的音频触觉报告，转译成手柄的转子马达震动

界面为 **WinUI 3 原生应用**，完整简体中文，托盘常驻，**便携免安装**。

---

## 目录

- [它和上游是什么关系](#它和上游是什么关系)
- [功能](#功能)
- [系统要求](#系统要求)
- [快速开始](#快速开始)
- [前置驱动](#前置驱动)
- [手柄必须切到 DInput 模式](#手柄必须切到-dinput-模式)
- [从源码构建](#从源码构建)
- [目录结构](#目录结构)
- [数据与配置放在哪](#数据与配置放在哪)
- [常见问题](#常见问题)
- [许可证与致谢](#许可证与致谢)

---

## 它和上游是什么关系

本项目是 [`ReynArts/ApexSenseBridge`](https://github.com/ReynArts/ApexSenseBridge) 的**衍生作品**（初始基于 v0.6.3，自 0.7.1 起引擎基线为 v1.0.0-beta.9，自 **0.7.2 起为 v1.0.0-beta.10**）：

| 层 | 处理方式 |
|---|---|
| **C++ 引擎** | **保持上游原样**（基线 commit `e129848`，dev 分支 HEAD = v1.0.0-beta.10），仅作为二进制随包分发，以便持续跟进上游修复 |
| **用户界面** | 上游的 WPF 托盘界面**整个弃用**，用 **WinUI 3 全量重写**（导航壳 / MVVM / 本地化 / 主题 / 托盘 / 后台行为） |
| **中文支持** | 上游只有英法两种界面语言。中文是**全新翻译**（120+ 词条），不是补第三份字典 |
| **交付形态** | 上游为 Inno 安装器；本项目为**便携绿色包**（解压即用、不写安装项、不注册系统服务） |
| **Playnite 插件** | 上游插件查注册表找安装路径、靠下载安装器更新；本项目改为**沿目录找 `DsInApex.exe`**（绿色版无安装项）、只提示更新；界面文案从硬编码法文改为 en/zh 双语 |

由于 UI 层是重写而非翻译，**本项目的界面不会与上游 WPF 版保持同步** —— 这是明确接受的代价。

兼容性上刻意保持不变的部分：设置文件位置与字段名、会话互斥对象名、引擎命令行参数。
**本程序与官方版可以共用同一份用户数据目录**，学习记录互通。

---

## 功能

**主界面**

| 页面 | 内容 |
|---|---|
| 仪表盘 | 桥接状态、当前游戏、扳机/触觉指示灯、快捷启停 |
| 游戏库 | 200+ 款支持游戏（含自适应扳机/触觉标注）、搜索、筛选、排序、排除、APEX 配置档绑定、封面缓存 |
| 学习记录 | 自动学习「哪个 exe 属于哪个游戏」，支持多选删除与导出 |
| 驱动管理 | 前置驱动状态检测（内核驱动 + 用户态服务双层探测）、一键安装/修复（SHA-256 校验 + UAC 提权） |
| 硬件测试 | 设备识别、扳机测试、震动测试、输入监视、虚拟 DS5 状态、应急复位 |
| 诊断面板 | 一条命令产出完整诊断包（PnP 拓扑 / 冲突扫描 / HID 接口 / XInput 探针 / 引擎自检报告，ZIP 导出） |
| 设置 | 检测策略、启动条件、通知、强制激活、触觉阈值、开机自启、语言、主题 |

**托盘**：20 项后台控制面 —— 状态行、启停桥接、开机自启、硬件测试/诊断入口、恢复手柄可见性（仅在异常遗留时出现）、检查更新、中英切换、退出。

**其它**：中英热切换（无需重启）、单实例、退出清理（托盘退出 / 关窗 / 进程被杀三条路径）、开机自启**路径漂移自动纠偏**。

**Playnite 联动**（独立分发的 `.pext`）：受支持游戏启动时自动拉起桥接并等虚拟 DualSense 就绪，
游戏退出后干净还原手柄；游戏右键菜单可分别设置「启用方式 / 触控板映射 / APEX 板载配置」。
与会话互斥**共用同一个引擎互斥体**，因此与主程序、官方托盘三方不抢手柄。
插件界面支持**简体中文 / 英文**（跟随 Playnite 语言）。

---

## 系统要求

| 项 | 要求 |
|---|---|
| 操作系统 | Windows 10 19041（20H1）或更高；推荐 Windows 11 |
| 架构 | x64 |
| 运行时 | **无需安装 .NET 或 Windows App SDK** —— 便携包自带全部运行时 |
| 手柄 | 飞智八爪鱼 4（APEX 4），**必须切换到 DInput 模式** |
| 驱动 | 首次使用需安装 usbip-win2 与 HidHide（程序内置安装器） |

---

## 快速开始

1. 下载 `DsInApex-Portable-<版本>.zip`，**解压到任意目录**（路径可含空格与中文）。
2. 双击 `DsInApex.exe`。
3. 打开左侧 **驱动管理** 页 → 点「安装/修复」→ 在 UAC 提示中确认 → **重启 Windows**。
   （重启是必须的：usbip-win2 与 HidHide 都是内核驱动。）
4. 把八爪鱼 4 切到 **DInput 模式**（见下节），用 USB 线或接收器连接。
5. 回到仪表盘，点「启动桥接」。手柄即在系统中以 **DualSense** 身份出现。

> **升级**：下载新版本目录替换即可。用户数据与设置位于 `%LOCALAPPDATA%`，不受影响。
> **卸载**：删除整个目录即可。`%LOCALAPPDATA%\ApexSenseBridge`（设置与日志）与已安装的驱动会保留，
> 驱动请在 Windows「设置 → 应用」中自行卸载。

---

## 前置驱动

USB 设备要能被「转接」成另一个设备，必须有内核驱动支撑。本项目捆绑两个，均由**程序内的驱动管理页**负责安装：

| 驱动 | 版本 | 用途 | 许可证 |
|---|---|---|---|
| **usbip-win2** | 0.9.8.0 | USB 设备重定向（虚拟 DualSense 的核心通道） | BSD-2-Clause |
| **HidHide** | 1.5.230 | 隐藏原始手柄的重复接口，避免游戏收到两份输入 | MIT |

许可证原文随包分发在 `Prerequisites\` 内（`USBIP-WIN2-LICENSE.txt` / `HIDHIDE-LICENSE.txt`）。

安装前程序会先校验安装器的 **SHA-256**，再以管理员权限执行。
驱动状态检测同时查询**内核驱动类（`Win32_SystemDriver`）与用户态服务类（`Win32_Service`）** ——
只查后者会得到空集合并把「正在运行」误报成「未运行」。

---

## 手柄必须切到 DInput 模式

这是**最高频的失败原因**。八爪鱼 4 的 XInput 模式在系统中表现为标准 Xbox 手柄，
桥接需要的是它的原始 HID 接口。

切换方式：**飞智游戏厅 / Space Station** 中把连接模式改为 **DInput（PC 直连 / 接收器）**。
切换后设备管理器里应能看到 `VID_04B4 & PID_2412` 的复合设备（MI_00 ~ MI_03）。

---

## 从源码构建

### 依赖

- .NET SDK **10.0.203** 或更高
- Windows SDK **10.0.26100**（含 C++ 工具链则更好，但引擎按二进制引入时非必需）

### 构建

```powershell
# ⚠️ 不要用 dotnet publish —— 见下方说明
dotnet build DsInApex.sln -c Release -p:Platform=x64
```

产物：

```
src\DsInApex.App\bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\DsInApex.exe
```

> 🔴 **为什么不用 `dotnet publish`**
> 在 `WinUI 3 + Unpackaged + SelfContained` 组合下，`dotnet publish` 产出的目录**缺少 XAML 编译产物**
> （`App.xbf` / `Views\*.xbf` / `DsInApex.pri` / `Assets\*`）。文件看着齐全，但进程一启动就静默退出。
> `dotnet build` 的输出反而是完整可运行集合。这是踩过的坑，别再试一次。

### 打便携包

```powershell
powershell -File build\make-portable.ps1
```

脚本会：构建 → 按目录规范组装 → 写入 `version.json` → 生成 `SHA256SUMS.txt` → 压缩为
`build\release\DsInApex-Portable-<版本>.zip` → 打印逐项布局自检结果。

引擎二进制不入版本控制（`vendor/` 在 `.gitignore` 中）。若要自行打包，
先下载官方 [ApexSenseBridge v1.0.0-beta.10 Portable 包](https://github.com/ReynArts/ApexSenseBridge/releases/tag/v1.0.0-beta.10)
解压到 `vendor\portable-1.0.0-beta.10\ApexSenseBridge-Portable\`。

> ⚠️ **必须取 dev 分支的 beta 版，不能取 stable 的正式版** ——
> LT/RT 自适应扳机的不对称识别修复只存在于 dev（1.0.0-beta.9 起），
> 而「降级 32 字节 USB 身份」的识别则要 **1.0.0-beta.10 起**才有
> （上游 issue #26，见下方「引擎升级」一节）；
> stable 的 v0.6.3 两者都没有（右扳机没有阻力，且降级态被误报成支持）。
> 引擎与 `engine/` 源码基线的对应关系记录在 `version.json`。
> ⚠️ `make-portable.ps1` 现在自带**引擎版本守门**：会执行包内
> `engine\ApexSenseBridge.exe help` 并比对 `$EngineVer`，不一致直接失败 ——
> 这条守门就是为了防止"只改源目录、忘改版本号"的静默错配。

### 打 Playnite 插件包

```powershell
powershell -File build\build-playnite-extension.ps1
```

产出 `build\release\DsInApex-Playnite-<版本>.pext`，在 Playnite 里
「扩展 → 从文件安装扩展」装上即可。

- **构建走 `dotnet build`，不要用 MSBuild.exe**：插件 TFM 是 `net462`（Playnite SDK 6.16.0
  只提供 `lib/net462`），本机没有 .NET Framework 参考程序集时由
  `Microsoft.NETFramework.ReferenceAssemblies` NuGet 包补齐。
- 本机未安装 Playnite 时，脚本会从 NuGet 取**钉版 PlayniteSDK 6.16.0** 并做 SHA-256 校验。
- 静态验收：`powershell -File build\verify-p9.ps1`（S1 产物 / S2 身份与版本 / S3 包结构 / S4 本地化审计）。

---

## 目录结构

便携包（`DsInApex-Portable-<版本>\`）：

```
DsInApex.exe                  主程序 —— 双击这个
DsInApex.pri / *.xbf          编译后的 XAML 资源（缺失则无法启动）
（自包含 .NET / Windows App SDK 运行时）
Prerequisites\                驱动安装器 + 各自许可证副本
engine\                       C++ 引擎（ApexSenseBridge.exe / libVIIPER.dll / viiper.exe / Data / Licenses）
LICENSE                       GPL-3.0-or-later 完整文本
NOTICE.md                     衍生作品声明与修改记录（GPL-3.0 §5a）
THIRD_PARTY_NOTICES.md        第三方组件与许可证
SOURCE_OFFER.md               源码获取说明
README.md                     本文件
version.json                  版本清单（更新检查读取）
SHA256SUMS.txt                包内逐文件校验清单
```

> Playnite 插件是**独立分发**的 `.pext`（不在便携包内），见「打 Playnite 插件包」。

源码仓库：

```
src\DsInApex.App\              WinUI 3 主程序（UI / 托盘 / 本地化 / 自检）
src\DsInApex.Core\             业务逻辑（无 UI 依赖）
src\DsInApex.Playnite\         Playnite 插件（net462 + Playnite SDK，独立打包为 .pext）
engine\                        上游 C++ 引擎源码镜像（独立 git，基线 e129848 / dev 分支）
build\make-portable.ps1        便携包打包脚本
build\build-playnite-extension.ps1  Playnite 插件打包脚本
build\verify-p8.ps1 / verify-p9.ps1 主程序 / 插件验收脚本
vendor\                        官方引擎二进制（不入版本控制）
data\supported_games.json      游戏库云端数据源（见下节）
docs\                          各阶段技术报告
```

---

## 游戏库数据源（云端）

游戏库的 215 款游戏数据来自本仓库的 **`data/supported_games.json`**：

- https://cdn.jsdelivr.net/gh/JinAsukin/DsInApex@main/data/supported_games.json
- https://raw.githubusercontent.com/JinAsukin/DsInApex/main/data/supported_games.json

程序**优先走 jsDelivr**（实测 `raw.githubusercontent.com` 在国内直连不可达，jsDelivr 可达），
失败时回落到内置的离线副本（随程序嵌入），因此**断网也能正常使用** —— 只是数据不会更新。

条目字段：每款游戏的支持标记（自适应扳机 / 触觉反馈）、推荐的触控板配置档、
校验过的 Steam AppID、可执行文件名与封面地址。

> 该数据文件以同样的 GPL-3.0-or-later 条款分发。

---

## 数据与配置放在哪

| 内容 | 路径 |
|---|---|
| 设置 | `%LOCALAPPDATA%\ApexSenseBridge\tray_settings.json` |
| 日志 | `%LOCALAPPDATA%\ApexSenseBridge\logs\` |
| 学习记录 | `%LOCALAPPDATA%\ApexSenseBridge\learned_executables.json` |
| 游戏库缓存 | `%LOCALAPPDATA%\ApexSenseBridge\` |

> 这些路径**刻意与官方版保持一致**，两边可以互相读取对方的记录。
> 便携包删除目录**不会**动这里 —— 所以「卸载」是干净的，不会连你的配置一起抹掉。

自检（全部只读，不驱动手柄）：

```powershell
$env:DIA_SELFTEST = '1'; .\DsInApex.exe
# 结果：%LOCALAPPDATA%\ApexSenseBridge\logs\dsinapex_selftest.log（UTF-8 无 BOM）
```

---

## 常见问题

**手柄识别不到 / 启动桥接失败**
先确认手柄在 **DInput 模式**（`VID_04B4&PID_2412`）。其次检查驱动管理页里 usbip-win2 与 HidHide 是否都在运行。
若飞智 Space Station 正在运行，它可能短暂占用设备 —— 稍等一两秒重试通常即可。

**震动没有反应 / 音频触觉数值一直是 0**
音频触觉的数据源是**「游戏写给虚拟 DualSense 的输出报告」**，不是系统音频回环。
在扬声器上放音乐、看视频都**不会**让这个数字动。必须在**支持该特性的游戏**里才会出现。

**界面显示的名称为「Flydigi VADER3」／硬件测试页提示「降级身份」**
旧版说明曾写"功能不受影响"，**那条结论是错的**。
这个字符串是手柄**当前暴露的 USB 身份**，直接决定右扳机能否自适应 —— 详见下一条。
（型号判定本身确实不依赖产品名，走的是 `VID_04B4&PID_2412` 实例 ID。）

**右扳机（RT）没有自适应，左扳机（LT）正常（APEX 4）**
这是 **APEX 4 的固件行为，不是 DIA 的缺陷**：同一台手柄有两种 USB 身份，
而 **VID:PID 两者都是 `04B4:2412`，只能靠产品名与输出报告长度区分**。

| | 降级身份 | 完整身份 |
|---|---|---|
| 产品名 | `Flydigi VADER3`（浏览器 WebHID 常显示 `Flydigi Direwolf 3`） | `Flydigi APEX 4` |
| 输出报告长度 | 32 字节 | 64 字节 |
| RT 自适应 | ❌ 不生效 | ✅ 生效 |
| LT 自适应 | ✅ 生效 | ✅ 生效 |

进入完整身份靠的是**物理连接**：接收器（dongle）在位的同时用有线接入，
或先有线接入再插入接收器（此时手柄强制走有线 DInput）。

**自 0.7.2 起不用再靠产品名猜了**：引擎（上游 v1.0.0-beta.10，issue #26）已能区分两种接口，

```
Adaptive triggers: yes (full 64-byte Apex 4 interface)                      ← 完整
Adaptive triggers: partial (degraded 32-byte Apex 4 interface; LT may work, RT unavailable)
Action: reconnect the controller/receiver until this command reports the full 64-byte Apex 4 interface.
```

硬件测试页的「设备身份」会把三态直接翻译成中文展示（完整 / 部分 / 不支持），
并在降级态原样带出引擎给的处置建议。**判断依据是引擎报的接口形态，不再是猜测。**

> 在此之前（0.6.3 ~ 0.7.1 的引擎）降级态会被 `identify` 报成 `Adaptive triggers: yes` ——
> 即"支持"，而 RT 实际没反应。这正是 issue #26 描述的假阳性。

⚠️ **完整身份很不稳定：一旦发生桥接会话切换（启动／停止桥接），手柄就会协议退化**，
退化后 RT 立刻失效，只能重新插拔／切换连接来恢复。
换言之 **「桥接运行中」与「RT 自适应」目前是互斥的** —— 这是固件限制，DIA 无法绕过。
硬件测试页会实时显示当前属于哪一种身份，可据此判断。

值得说明的是，引擎在这种状态下**仍会报告成功**：`identify` 恒为 `Apex 4`、
桥接日志里 LT/RT 指标完全对称、`write_failures=0`、
`apex4-port-test --forceadapt` 也返回 0 ——
因为引擎只看命令有没有写出去，而降级身份的固件**收到但不执行** RT 那一条。
所以判断依据只能是产品名与报告长度，不能看引擎的"成功"。

**开机自启失效了**
便携版会移动目录。自启项记录的是绝对路径，挪动后即变死链。
程序启动时会自动检测并纠正 —— 前提是你**至少启动过一次**。

**托盘图标不见了**
Windows 11 默认把新托盘图标折叠进「隐藏图标」面板，拖出来即可。

**提示「无法写入程序目录」**
把便携包放在受保护位置（如 `C:\Program Files`）会导致配置写入失败。换个普通目录。

---

## 许可证与致谢

**Ds in Apex 以 [GPL-3.0-or-later](LICENSE) 发布**，是 `ApexSenseBridge` 的衍生作品，
已按 GPL-3.0 §5(a) 标注修改（见 [NOTICE.md](NOTICE.md)）。源码获取方式见 [SOURCE_OFFER.md](SOURCE_OFFER.md)，
第三方组件与各自许可证详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

特别感谢：

- [ReynArts/ApexSenseBridge](https://github.com/ReynArts/ApexSenseBridge) —— C++ 引擎与整体方案（GPL-3.0）
- [VIIPER](https://github.com/Alia5/VIIPER) —— 虚拟 USB 设备后端（GPL-3.0）
- [usbip-win2](https://github.com/vadimgrn/usbip-win2) —— USB 重定向驱动（BSD-2-Clause）
- [HidHide](https://github.com/nefarius/HidHide) —— HID 隐藏驱动（MIT）
- [H.NotifyIcon](https://github.com/HavenDV/H.NotifyIcon) · [WinUIEx](https://github.com/dongle-the-gadget/WinUIEx) ·
  [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) —— WinUI 3 生态组件
