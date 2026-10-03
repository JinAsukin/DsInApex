using System.Diagnostics;
using System.Text.Json;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>一次驱动安装尝试的结果。</summary>
public sealed class DriverInstallResult
{
    public required string DriverId { get; init; }
    public bool Success { get; init; }
    public bool NeedsRestart { get; init; }
    public int ExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? LogPath { get; init; }
    public DriverState FinalState { get; init; } = DriverState.Unknown;
}

/// <summary>
/// 驱动安装：校验捆绑安装器 → 提权执行 → 复核结果。
///
/// <para>
/// <b>与上游的关系：</b>C# 重写自 <c>installer/install-usbip.ps1</c> 与
/// <c>ApexSenseBridge.iss</c> 的安装段。判定语义刻意保持一致：
/// SHA-256 先校验、退出码 <c>0</c> 或 <c>3010</c>（需重启）算成功、超时则连进程树一起杀。
/// </para>
///
/// <para>
/// <b>提权模型（关键设计）：</b>主界面以普通权限运行，安装动作经
/// <c>ShellExecute(Verb="runas")</c> **以管理员身份重启自身**（带 <c>--driver-action</c> 参数），
/// 由那个提权实例执行安装、写结果文件、退出。
/// </para>
///
/// <para>
/// <b>为什么不用独立 helper exe：</b>省一个项目、省一份打包清单，
/// 且 unpackaged 下自启动没有 MSIX 限制。
/// </para>
///
/// <para>
/// <b>⚠️ 安全护栏：</b>本服务**永远不会**被自动调用 —— 必须由用户在界面上显式点击，
/// 且必然触发一次 UAC 确认。检测服务（<c>DriverDetectionService</c>）是只读的，两者严格分开。
/// </para>
/// </summary>
public sealed class DriverInstallerService
{
    private const string LogFileName = "dsinapex_drivers.log";
    private const int InstallTimeoutSeconds = 300;

    /// <summary>辅助模式的结果文件（主进程等待子进程退出后读取）。</summary>
    public static string ResultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApexSenseBridge", "driver-install-result.json");

    private readonly DriverDetectionService _detection;

    public DriverInstallerService(DriverDetectionService detection) => _detection = detection;

    /// <summary>
    /// 主进程入口：提权安装指定驱动。
    /// 已提权时直接执行；否则以 <c>runas</c> 重启自身完成。
    /// </summary>
    public async Task<DriverInstallResult> InstallAsync(DriverInfo info)
    {
        if (DriverDetectionService.IsElevated)
        {
            AppLog.Info(LogFileName, $"当前已是管理员，直接安装 {info.Id}");
            return await ExecuteInstallCoreAsync(info).ConfigureAwait(false);
        }

        return await LaunchElevatedHelperAsync(info).ConfigureAwait(false);
    }

    /// <summary>以管理员身份重启自身，进入辅助模式执行安装，并回读结果。</summary>
    private async Task<DriverInstallResult> LaunchElevatedHelperAsync(DriverInfo info)
    {
        string? selfPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(selfPath) || !File.Exists(selfPath))
        {
            return Failure(info.Id, "无法定位主程序路径，提权安装不可用。");
        }

        // 清掉上一次的陈旧结果，避免误读
        try
        {
            if (File.Exists(ResultFilePath)) File.Delete(ResultFilePath);
        }
        catch
        {
            // 删不掉不影响本次（会用时间戳兜底判断）
        }

        Process? helper;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = selfPath,
                UseShellExecute = true,
                Verb = "runas",   // 触发 UAC
                WorkingDirectory = Path.GetDirectoryName(selfPath) ?? string.Empty,
            };
            psi.ArgumentList.Add("--driver-action=install");
            psi.ArgumentList.Add($"--driver={info.Id}");

            AppLog.Info(LogFileName, $"请求提权安装 {info.Id}");
            helper = Process.Start(psi);
        }
        catch (Exception ex)
        {
            // 用户在 UAC 对话框点了「否」
            AppLog.Warn(LogFileName, $"提权被取消或失败：{AppLog.Describe(ex)}");
            return Failure(info.Id, "提权被取消，驱动未安装。");
        }

        if (helper is null)
        {
            return Failure(info.Id, "无法启动提权进程。");
        }

        await Task.Run(() =>
        {
            helper.WaitForExit();
            helper.Dispose();
        }).ConfigureAwait(false);

        return ReadResultFile(info.Id)
               ?? Failure(info.Id, "提权进程已退出，但未留下结果（可能被中断）。");
    }

    /// <summary>
    /// 在**当前（已提权）**进程内执行安装。辅助模式与「本身就以管理员运行」两条路径共用。
    /// </summary>
    public async Task<DriverInstallResult> ExecuteInstallCoreAsync(DriverInfo info)
    {
        if (string.IsNullOrWhiteSpace(info.Installer))
        {
            return Failure(info.Id, "该驱动没有捆绑安装器（可选组件）。");
        }

        string installerPath = Path.Combine(_detection.PrerequisitesDirectory, info.Installer);

        // ── 1. 存在性 + 完整性（双重保险：即使调用方已校验，这里再验一次）──
        if (!File.Exists(installerPath))
        {
            return Failure(info.Id, $"找不到捆绑安装器：{installerPath}");
        }

        if (!string.IsNullOrWhiteSpace(info.Sha256) &&
            !DriverDetectionService.VerifySha256(installerPath, info.Sha256))
        {
            return Failure(info.Id, "安装器 SHA-256 校验失败，已拒绝执行（可能被篡改或版本不匹配）。");
        }

        // ── 2. 组装参数 ──
        var arguments = new List<string>(info.InstallArgs);
        string? logPath = null;

        if (!string.IsNullOrWhiteSpace(info.LogArgTemplate))
        {
            logPath = Path.Combine(AppLog.DefaultDirectory, $"driver-install-{info.Id}.log");
            arguments.Add(info.LogArgTemplate.Replace("{log}", logPath, StringComparison.Ordinal));
        }

        // ── 3. 执行 ──
        AppLog.Info(LogFileName, $"开始安装 {info.Id}：{installerPath} {string.Join(' ', arguments)}");

        int exitCode;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty,
            };
            foreach (string argument in arguments)
            {
                psi.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(psi)
                ?? throw new InvalidOperationException("安装器进程未能启动。");

            bool exited = await Task.Run(() => process.WaitForExit(InstallTimeoutSeconds * 1000))
                .ConfigureAwait(false);

            if (!exited)
            {
                AppLog.Warn(LogFileName, $"安装 {info.Id} 超时，正在终止其进程树");
                TryKillTree(process);
                return Failure(info.Id, $"安装超过 {InstallTimeoutSeconds} 秒仍未完成，已终止。", logPath);
            }

            exitCode = process.ExitCode;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"安装 {info.Id} 异常：{AppLog.Describe(ex)}");
            return Failure(info.Id, "安装器执行失败：" + AppLog.Describe(ex), logPath);
        }

        AppLog.Info(LogFileName, $"安装器 {info.Id} 退出码 = {exitCode}");

        // ── 4. 退出码判定（0 = 成功；3010 = 成功但需重启，与上游一致）──
        if (exitCode is not (0 or 3010))
        {
            return Failure(info.Id, $"安装器返回非预期退出码 {exitCode}。", logPath, exitCode);
        }

        // ── 5. 复核 ──
        DriverStatus after = _detection.Detect(info);
        bool ok = after.State is DriverState.Running or DriverState.Installed;

        var result = new DriverInstallResult
        {
            DriverId = info.Id,
            Success = ok,
            NeedsRestart = exitCode == 3010 || after.NeedsRestart,
            ExitCode = exitCode,
            FinalState = after.State,
            LogPath = logPath,
            Message = ok
                ? "安装完成。"
                : $"安装器报告成功，但复核仍未就绪（状态={after.State}）。可能需要重启。",
        };

        AppLog.Info(LogFileName, $"安装 {info.Id} 复核：{(ok ? "通过" : "未通过")}，状态={after.State}");
        return result;
    }

    /// <summary>写结果文件（仅辅助模式调用）。</summary>
    public static void WriteResultFile(DriverInstallResult result)
    {
        try
        {
            string path = ResultFilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"写安装结果文件失败：{AppLog.Describe(ex)}");
        }
    }

    private static DriverInstallResult? ReadResultFile(string driverId)
    {
        try
        {
            string path = ResultFilePath;
            if (!File.Exists(path)) return null;

            DriverInstallResult? result = JsonSerializer.Deserialize<DriverInstallResult>(File.ReadAllText(path));
            if (result is null) return null;

            // 防误读陈旧结果
            return string.Equals(result.DriverId, driverId, StringComparison.OrdinalIgnoreCase) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 尽力而为
        }
    }

    private static DriverInstallResult Failure(string driverId, string message, string? logPath = null, int exitCode = -1)
        => new()
        {
            DriverId = driverId,
            Success = false,
            ExitCode = exitCode,
            Message = message,
            LogPath = logPath,
            FinalState = DriverState.Unknown,
        };
}
