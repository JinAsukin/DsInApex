using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.Win32;

namespace DsInApex.Core.Services;

/// <summary>
/// 开机自启（P6）。
///
/// <para>
/// 落点：<c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>，值名 <c>DsInApex</c>，
/// 值形如 <c>"E:\Apps\DsInApex\DsInApex.exe" --autostart</c>。
/// </para>
///
/// <para>
/// <b>为什么不用「启动」文件夹、也不用计划任务：</b>注册表 Run 键是上游 WPF 托盘时代的
/// 同级方案（用户级、无需管理员、一看就懂、可被任务管理器「启动」页管理），
/// 且 HKCU 天然适合便携版 —— 不装服务、不碰 HKLM。
/// </para>
///
/// <para>
/// 🔴 <b>便携版必踩的坑：路径漂移。</b>
/// 注册表里存的是<b>绝对路径</b>。便携版用户把文件夹改名/挪盘/U 盘换机器之后，
/// 这个值就成了死链：系统每次开机都去启动一个不存在的文件，静默失败，
/// 用户只会觉得「自启这功能是坏的」。
/// 因此 <see cref="Query"/> 必须区分「已启用」与「已启用但指向别处」，
/// 并在启动时用 <see cref="RepairIfStale"/> 自动纠偏、把结果记进日志。
/// </para>
/// </summary>
public static class AutoStartService
{
    private const string LogFileName = "dsinapex_autostart.log";

    /// <summary>注册表值名（品牌名 DIA，与上游 <c>ApexSenseBridge</c> 不冲突，两者可共存）。</summary>
    public const string ValueName = "DsInApex";

    /// <summary>注册表键路径（HKCU 相对路径）。</summary>
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>自启时附加的命令行参数：窗口建好即隐藏，只驻留托盘。</summary>
    public const string AutoStartArgument = "--autostart";

    /// <summary>本机当前应有的自启命令行。</summary>
    public static string ExpectedCommand => BuildCommand(CurrentExecutablePath());

    /// <summary>当前进程的 exe 路径（取不到时回落到应用目录下的 <c>DsInApex.exe</c>）。</summary>
    public static string CurrentExecutablePath()
    {
        string? path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string appDirectory = AppContext.BaseDirectory;
        return string.IsNullOrWhiteSpace(appDirectory)
            ? "DsInApex.exe"
            : Path.Combine(appDirectory, "DsInApex.exe");
    }

    /// <summary>把 exe 路径拼成带引号的自启命令行（路径含空格必须加引号，否则系统解析成两个参数）。</summary>
    public static string BuildCommand(string executablePath)
        => $"\"{executablePath}\" {AutoStartArgument}";

    /// <summary>从命令行里解析出 exe 路径（去掉引号与参数）。解析失败返回 null。</summary>
    public static string? ParseExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        string trimmed = command.Trim();
        if (trimmed.StartsWith('"'))
        {
            int end = trimmed.IndexOf('"', 1);
            return end > 1 ? trimmed[1..end] : null;
        }

        // 未加引号：取到第一个空格前（含空格的路径在这种情况下本来也无法正确解析）
        int space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }

    /// <summary>查询当前自启状态（只读）。</summary>
    public static AutoStartStatus Query() => QueryFor(CurrentExecutablePath());

    /// <summary>查询「若可执行文件位于 <paramref name="executablePath"/>」时的自启状态（便于自检注入探针）。</summary>
    public static AutoStartStatus QueryFor(string executablePath)
    {
        string expected = BuildCommand(executablePath);

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            string? registered = key?.GetValue(ValueName) as string;

            if (string.IsNullOrWhiteSpace(registered))
            {
                return new AutoStartStatus
                {
                    State = AutoStartState.Disabled,
                    RegisteredCommand = null,
                    ExpectedCommand = expected,
                    ValueName = ValueName,
                    KeyPath = KeyPath,
                };
            }

            string? registeredExe = ParseExecutablePath(registered);
            bool pointsHere = registeredExe is not null &&
                              string.Equals(
                                  PortableLayoutService.NormalizePath(registeredExe),
                                  PortableLayoutService.NormalizePath(executablePath),
                                  StringComparison.OrdinalIgnoreCase);

            return new AutoStartStatus
            {
                State = pointsHere ? AutoStartState.Enabled : AutoStartState.StalePath,
                RegisteredCommand = registered,
                ExpectedCommand = expected,
                ValueName = ValueName,
                KeyPath = KeyPath,
            };
        }
        catch (Exception ex)
        {
            string reason = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"读取自启项失败：{reason}");

            return new AutoStartStatus
            {
                State = AutoStartState.Unreadable,
                RegisteredCommand = null,
                ExpectedCommand = expected,
                ValueName = ValueName,
                KeyPath = KeyPath,
                Error = reason,
            };
        }
    }

    /// <summary>登记开机自启（覆盖旧值）。</summary>
    public static bool Enable(out string? error)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

            // 普通字符串值。不要用 ExpandString —— 便携版路径里出现 % 时会被系统二次展开
            key.SetValue(ValueName, ExpectedCommand, RegistryValueKind.String);

            AppLog.Info(LogFileName, $"已登记开机自启：{ExpectedCommand}");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"登记开机自启失败：{error}");
            return false;
        }
    }

    /// <summary>移除开机自启（值不存在视为成功）。</summary>
    public static bool Disable(out string? error)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);

            AppLog.Info(LogFileName, "已移除开机自启登记");
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"移除开机自启失败：{error}");
            return false;
        }
    }

    /// <summary>
    /// 启动时调用：发现「已登记但路径漂移」就顺手改写成本机当前路径。
    ///
    /// <para>
    /// 只在 <see cref="AutoStartState.StalePath"/> 时动手 ——
    /// 「本来就没开自启」的用户不该被悄悄打开，那是越权。
    /// </para>
    /// </summary>
    public static AutoStartStatus RepairIfStale()
    {
        AutoStartStatus status = Query();
        if (!status.IsStale)
        {
            return status;
        }

        AppLog.Info(LogFileName,
            $"检测到自启路径漂移：登记值=\"{status.RegisteredCommand}\" 实际位置=\"{status.ExpectedCommand}\" → 自动纠正");

        if (Enable(out string? error))
        {
            return Query();
        }

        AppLog.Warn(LogFileName, $"自启路径纠正失败：{error}");
        return status;
    }

    /// <summary>当前进程是否由开机自启拉起（解析自身命令行）。</summary>
    public static bool StartedByAutoStart()
    {
        try
        {
            return Environment.GetCommandLineArgs().Any(argument =>
                argument.Equals(AutoStartArgument, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }
}
