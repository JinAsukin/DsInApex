# NOTICE · 衍生作品声明与修改说明

> 本文件承载 **GNU GPL v3.0 §5(a)** 要求的「已修改」显著标注，以及上游作品归属。
> 许可证的**完整文本**见同目录 `LICENSE`（GPL-3.0-or-later，逐字原样）。

---

## 本程序

**Ds in Apex**（简称 DIA）是 **ApexSenseBridge 的修改版本（modified version）**，
依据 **GNU General Public License v3.0 or later** 分发。

```
SPDX-License-Identifier: GPL-3.0-or-later
```

本程序不提供任何担保，包括但不限于适销性与特定用途适用性。

---

## 原始作品 / Original work

| 项 | 内容 |
|---|---|
| 名称 | ApexSenseBridge |
| 仓库 | https://github.com/ReynArts/ApexSenseBridge |
| 基线版本 | v0.6.3（commit `e438507`） |
| 版权 | Copyright (c) ApexSenseBridge contributors |
| 许可证 | GNU General Public License v3.0 or later |

> ⚠️ 上游 README 中曾出现 "MIT" 字样，属**文档错误**；
> 以上游仓库的 `LICENSE` 文件（GPL-3.0-or-later）为准。

---

## 修改说明 / Modifications

修改日期：**2026-10-03**

Ds in Apex 由「上游 C++ 引擎层（原样保留）」+「全新 WinUI 3 界面层与 Core 层」构成：

1. **引擎层（`engine/`）保持上游源码不变**。
   保留原样是刻意的技术决定：这样上游的引擎缺陷修复可以持续合并跟进。
   随包分发的 `engine/ApexSenseBridge.exe` 等二进制取自上游官方 v0.6.3 Portable 发布包，**未经重新编译**。
2. 上游的 **WPF 托盘界面**（`ApexSenseBridgeTray/`）已被**完整重写**为 WinUI 3 应用，
   因此本作品的界面层**不再与上游同步**。
   → 随包产物**不再包含**上游的 `ApexSenseBridgeTray.exe` / `ApexSenseBridgeControl.exe`
   与配套启动脚本；它们的功能由 `DsInApex.exe` 承担。
3. 新增 **Core 层**：进程监控、游戏库与学习记录、驱动管理、硬件测试、
   诊断收集、托盘后台行为（开机自启 / 通知 / 恢复状态呈现）等。
4. **界面语言**：上游仅有英（en）/ 法（fr）两种；本作品界面为简体中文 + 英文，
   中文为全新翻译，法语界面已移除。
5. **交付形态**：由上游的 Inno Setup 安装器改为 **portable 绿色包**
   （解压即用、不写安装项、不注册系统服务、卸载即删除目录）。
6. **品牌名**由 `ApexSenseBridge` 变更为 `Ds in Apex`。

   **例外（为兼容性与引擎约定刻意保留原名）**：
   - 用户数据目录 `%LOCALAPPDATA%\ApexSenseBridge\`
   - 设置文件 `tray_settings.json`
   - 日志 `tray_bridge.log`
   - 会话互斥对象名 `Local\ApexSenseBridge.ActiveSession.Owner.v1`
   - 引擎可执行文件名 `ApexSenseBridge.exe`

---

## 第三方组件

本项目包含第三方组件，各自的许可证与来源详见 **`THIRD_PARTY_NOTICES.md`**。

---

## 完整许可证文本

- 仓库内：`LICENSE`（GPL-3.0 完整文本，逐字原样）
- 官方来源：https://www.gnu.org/licenses/gpl-3.0.txt
