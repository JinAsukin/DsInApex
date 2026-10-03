# Ds in Apex · Playnite 扩展

让 **Ds in Apex** 与 Playnite 联动：启动受支持的游戏时自动拉起桥接，把飞智 APEX 手柄
虚拟成原生 PS5 DualSense；游戏退出后干净还原手柄。

> 本扩展衍生自上游 `ApexSenseBridge` 的 Playnite 插件（0.6.3），已按 Ds in Apex 的
> 便携部署架构重写宿主侧逻辑。许可证 **GPL-3.0-or-later**。

## 前置条件

1. 已解压 **Ds in Apex 便携包**（目录里有 `DsInApex.exe`，以及 `engine\ApexSenseBridge.exe`）。
2. 手柄切到 **DInput 模式**（`VID_04B4&PID_2412`）。
3. 前置驱动（ViGEmBus / HidHide / usbip2 等）已在 Ds in Apex 的「驱动管理」页装好。

## 安装

Playnite → **扩展** → **从文件安装扩展** → 选择 `DsInApex-Playnite-<版本>.pext`。

## 使用

1. 在 Ds in Apex 里确认桥接可用（能正常起停会话）。
2. 从 Playnite 正常启动一个游戏：扩展会自动识别配置档并启动桥接。
3. 需要强制某个配置档，或对某游戏关掉桥接：在游戏上**右键 → Ds in Apex**，
   分别设置「启用方式 / 触控板映射 / APEX 板载配置」。
4. 音频触觉（音圈震动 → 转子马达）需要把**系统音频输出设为虚拟 DualSense**，
   并且**真的有游戏在输出音频** —— 没有游戏时相关指标恒为 0 是设计使然，放音乐无效。

## 部署位置怎么找

便携版**不写任何安装项**，所以扩展不会去查注册表，按下面的顺序找 `DsInApex.exe`：

1. **扩展设置里手动指定的路径**（最稳，推荐非常规部署位置用这个）
2. **正在运行的 `DsInApex` 进程**的主模块路径（DIA 常驻托盘，命中率最高）
3. 从插件所在目录向上回溯
4. 开发期布局：祖先目录里有 `DsInApex.sln` 时找 `src\DsInApex.App\bin\**\DsInApex.exe`
5. 常见位置：`%ProgramFiles%\DsInApex`、`%LOCALAPPDATA%\Programs\DsInApex`、
   各磁盘根下的 `\DsInApex`、`\Games\DsInApex`、`\Portable\DsInApex`、`\Tools\DsInApex`

引擎随后按 `<部署根>\engine\ApexSenseBridge.exe` 解析
（也可用环境变量 `DSINAPEX_ENGINE_PATH` 直接指定，与主程序同一开关）。

## 与主程序互斥

扩展**不自己造锁**：它启动的是同一个 C++ 引擎，引擎会持有
`Local\ApexSenseBridge.ActiveSession.Owner.v1`。因此 Ds in Apex 主程序、官方托盘与本扩展
三方互相感知，不会抢手柄。若已有会话，游戏启动会被**取消**并给出提示。

## 更新

检查的是 `JinAsukin/DsInApex` 的发布（与主程序同源），本机版本读部署目录的 `version.json`。
便携版没有安装器，所以扩展**只提示、只引导打开发布页，不会静默安装任何东西**。

## 本地化

`Localization/en_US.xaml`（基底）与 `Localization/zh_CN.xaml`。键以 `LOCDsInApex_` 前缀
保证全局唯一（Playnite 把所有扩展的字典合进同一张资源表）。
