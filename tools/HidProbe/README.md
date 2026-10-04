# hidprobe — APEX 4 HID 接口探针

独立于 `ApexSenseBridge` 引擎的**用户态 HID 探针**。存在的理由：引擎没有暴露任何
raw HID / feature report 命令，所以厂商接口只能自己直接对话。

> **红线**：本工具在 `tools/` 下，**不引用、不修改 `engine/`**（引擎目录只读）。
> 它也不参与 DsInApex 主程序构建，是纯研究工具。

## 构建与运行

```bash
cd /e/DsInApex/tools/HidProbe
dotnet build HidProbe.csproj -c Release
./bin/Release/net10.0-windows/hidprobe.exe list
```

⚠️ 与 DIA 主程序同一条规矩：**用 `dotnet build`，不要用 `dotnet publish`**。

## 命令

| 命令 | 性质 | 说明 |
|---|---|---|
| `list [--all] [--json]` | 只读 | 枚举 HID 接口，默认只列飞智 VID（`04B4` / `37D7`） |
| `read <idx\|--path P> [--count N] [--timeout MS]` | 只读 | 流式读输入报告 |
| `feature-get <idx> [--id NN] [--len N]` | 只读语义 | `HidD_GetFeature` |
| `feature-scan <idx> [--len N] [0x.. 0x..]` | 只读语义 | 扫描哪些 feature report ID 有响应 |
| `feature-set <idx> --hex <bytes> --yes` | **写** | `HidD_SetFeature`，须 `--yes`，**只发一次不重试** |
| `output <idx> --hex <bytes> --yes` | **写** | `HidD_SetOutputReport`，须 `--yes`，**只发一次不重试** |

hex 支持 `035AA5A0` / `03 5A A5 A0` / `03:5A:A5:A0` 三种写法。

## 安全约束（写死在代码里，别删）

1. 枚举 / caps / 读流 / feature-get 全程只读。
2. **写操作必须显式 `--yes`**，且**从不重试**——硬件写重试会让用户感到两次震动/阻力
   （这是 DIA 项目踩过的坑，见工作区 README §5.12）。
3. 打开设备用 `FILE_SHARE_READ | FILE_SHARE_WRITE`，与桥接会话共存，不抢独占。
4. **MI_03 用途未明，禁止盲写。** 它可能是固件升级/工厂通道，乱写有触发升级模式的风险。

## 本机实测基线（2026-10-04，APEX 4 完整身份 / DInput 模式）

```
idx   iface   vid:pid      rev      usage pg  in   out  feat
25    MI_00   04B4:2412    0x0100   0x0001    10   0    0
27    MI_02   04B4:2412    0x0100   0xFFA0    32   64   0     <- FORCEADAPT 端口（引擎用）
28    MI_03   04B4:2412    0xFFEF   64   64   64      <- 用途未知，唯一有 feature 通道的
```

- 设备 `USB\VID_04B4&PID_2412`，`REV_0100`，父驱动 `usbccgp`，各接口 `HidUsb`
  → **纯 HID，无 DFU 接口**（DFU 会是 `Class_FE SubClass_01`）
  → 固件提取**不能走标准 DFU**。
- `MI_03` 不发自发输入报告（`read` 超时），是**被动应答**接口。
- `MI_03` 的 feature 通道：扫遍 `0x00`–`0xFF`，**只有 `0x05` 有响应**，返回固定
  64 字节：`04 58 00 00 00 00 08 00` 重复 8 次。含义未知（疑似常量 stub）。
- 注意 `0x05` 同时也是 APEX 4 的命令报告 ID（`kApex4CommandReportId`），是否巧合待考。

## 已知坑

- 🔴 **`HidP_GetCaps` 返回的是 NTSTATUS，成功值是 `0x00110000`，不是 0。**
  按 `== 0` 判断会把成功当失败，caps 全填 0 —— 首版就踩了这个，表现为
  "usage page 全 0x0000、报告长度全 0"，与引擎 `diagnose` 对不上。判断要用 `>= 0`。
- TFM 必须是 `net10.0-windows`：用 `net10.0` 会因为 `NativeOverlapped` 报 CA1416 平台警告。
