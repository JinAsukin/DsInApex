# 源码获取说明 · Source offer（GPL-3.0 合规）

Ds in Apex（DIA）依据 **GNU General Public License v3.0 or later** 分发。
因此，任何拿到本程序二进制的人都**有权获得与之对应的完整源码**。
本文件说明到哪里取、以及二进制与源码的对应关系。

---

## 1. 本作品的源码

| 项 | 位置 |
|---|---|
| 主仓库 | https://github.com/JinAsukin/DsInApex |
| 许可证 | GNU GPL v3.0-or-later（**完整文本**见仓库 `LICENSE`） |
| 修改声明 | 仓库 `NOTICE.md`（GPL-3.0 §5(a) 要求的「已修改」标注） |
| 第三方声明 | 仓库 `THIRD_PARTY_NOTICES.md` |

便携包（`DsInApex-Portable-<版本>.zip`）里的 `DsInApex.exe`，
由**同一版本号对应的源码**构建。发布页的每个 release 都标注了它对应的源码版本；
包内 `version.json` 也记录了版本号与引擎基线。

## 2. 引擎层（上游来源）

DIA 的 `engine/` 目录是上游引擎仓库的**原样镜像**（独立 git，基线 commit `e129848`，即 dev 分支 HEAD 的 v1.0.0-beta.10）。

| 项 | 位置 |
|---|---|
| 上游仓库 | https://github.com/ReynArts/ApexSenseBridge |
| 上游许可证 | GNU GPL v3.0-or-later |
| 便携包中的 `engine/ApexSenseBridge.exe` | 取自上游官方 v1.0.0-beta.10 Portable 发布包，未重新编译 |

> 也就是说：**DIA 发布包里没有任何「既不属于 DIA 源码、也不属于上游源码」的二进制**。
> 唯一的例外是 P4 捆绑的第三方驱动安装器（USBip / HidHide），
> 它们各自的许可证与来源见 `THIRD_PARTY_NOTICES.md` 与 `Prerequisites/` 目录内的说明。

## 3. 自行构建

需要 .NET SDK 10 与 Windows SDK 10.0.26100（构建路径必须为纯英文）。

```powershell
git clone https://github.com/JinAsukin/DsInApex
cd DsInApex
dotnet build DsInApex.sln -c Release -p:Platform=x64
```

> ⚠️ **不要用 `dotnet publish`**：在 WinUI 3 + Unpackaged 组合下它会丢失 XAML 编译产物
> （`*.xbf` / `*.pri` / `Assets/`），产物启动即静默退出。用 `dotnet build`。

打便携包（组装目录 + 写 `version.json` + 生成校验清单 + 压 ZIP）：

```powershell
powershell -File build\make-portable.ps1
```

引擎二进制不需要自行编译：从上游官方 v1.0.0-beta.10 Portable 发布包取用即可，
解压到 `vendor\portable-1.0.0-beta.10\ApexSenseBridge-Portable\`（该目录不入版本控制）。
若确实要自行编译引擎，按上游仓库的 CMake 流程在 `engine/` 内构建。

## 4. 便携版的「卸载」

DIA 不做系统安装：删除解压出来的目录即可。
但请注意，**用户数据不在程序目录内**，需要单独清理：

```
%LOCALAPPDATA%\ApexSenseBridge\        ← 设置、学习记录、日志
```

该目录与上游官方版**共用**（这是刻意保留的兼容设计，用于无缝迁移设置与学习记录）。
