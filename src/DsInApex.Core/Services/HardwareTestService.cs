using System.Globalization;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 硬件测试服务：把引擎 CLI 的安全边界逻辑从界面里抽出来。
///
/// <para>
/// <b>三条不可让步的约束：</b>
/// </para>
///
/// <list type="number">
/// <item><b>会话互斥</b>：标了 <see cref="EngineCommandSpec.NeedsSessionIdle"/> 的命令
/// 在桥接会话活动时直接拒绝执行 —— 测试命令与
/// <c>bridge-triggers</c> 会打开同一个 HID 接口，硬并发的结果是
/// 双方都读到垃圾数据（实测已见 "identity verification failed"）。</item>
///
/// <item><b>写硬件的命令不自动重试</b>：只读命令遇到"设备占用"可以悄悄重试，
/// 但 <c>test-rt</c> / <c>test-rumble</c> 一旦重试，用户可能感到两次扳机或震动。</item>
///
/// <item><b>解析失败必须可见</b>：<see cref="EngineCommandOutcome.IsParsed"/> 为 false 时
/// 界面展示原始文本，绝不静默显示"无数据"。</item>
/// </list>
///
/// <para>
/// 界面层只需调用 <see cref="RunAsync"/>，不必知道引擎路径、参数顺序或退出码语义。
/// </para>
/// </summary>
public sealed class HardwareTestService
{
    private const string LogFileName = "dsinapex_hardware.log";

    /// <summary>
    /// 只读命令因设备占用失败后的重试间隔。
    /// <para>
    /// 实测（2026-10-03）：<c>input-status</c> 首跑报
    /// <c>No valid command 0xEC Apex 4 identity reply arrived after 30 attempts</c>，
    /// 间隔约 1 秒后重试即成功。根因是 Flydigi Space Station 等程序仍在轮询同一接口。
    /// </para>
    /// </summary>
    private static readonly TimeSpan ReadOnlyRetryDelay = TimeSpan.FromMilliseconds(1200);

    private readonly EngineSessionManager _session;
    private readonly object _pathLock = new();
    private string? _cachedEnginePath;

    public HardwareTestService(EngineSessionManager session)
    {
        _session = session;
    }

    /// <summary>当前是否存在活动桥接会话（界面据此禁用写硬件的测试）。</summary>
    public bool IsSessionActive => _session.IsSessionActive;

    /// <summary>引擎是否已定位到（界面据此提示"引擎缺失"）。</summary>
    public bool EngineAvailable => !string.IsNullOrWhiteSpace(ResolveEnginePath());

    /// <summary>
    /// 执行一条引擎命令。
    /// </summary>
    /// <param name="commandId">命令主词（必须存在于 <see cref="EngineCommandCatalog"/>）。</param>
    /// <param name="options">可选参数。</param>
    /// <param name="cancellationToken">取消（长命令由界面提供"中止"）。</param>
    public async Task<EngineCommandOutcome> RunAsync(
        string commandId,
        EngineCommandOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new EngineCommandOptions();

        EngineCommandSpec? spec = EngineCommandCatalog.Get(commandId);
        if (spec is null)
        {
            AppLog.Warn(LogFileName, $"未知命令：{commandId}");
            return Failure(commandId, commandId, LocalizationService.Shared.Format(
                "Loc_HwUnknownCommand", commandId));
        }

        string arguments = EngineCommandCatalog.BuildArguments(spec, options);

        // ── 约束 1：会话互斥 ──
        if (spec.NeedsSessionIdle && _session.IsSessionActive)
        {
            AppLog.Warn(LogFileName, $"拒绝执行 {commandId}：桥接会话正在运行");
            return Failure(commandId, arguments,
                LocalizationService.Shared.Get("Loc_HwBlockedBySession"));
        }

        string? enginePath = ResolveEnginePath();
        if (string.IsNullOrWhiteSpace(enginePath))
        {
            AppLog.Warn(LogFileName, $"拒绝执行 {commandId}：引擎未找到");
            return Failure(commandId, arguments,
                LocalizationService.Shared.Get("Loc_HwEngineMissing"));
        }

        AppLog.Info(LogFileName, $"执行引擎命令：{arguments}");

        var runner = new EngineRunner(enginePath);
        EngineResult result = await runner
            .RunAsync(arguments, spec.DefaultTimeoutMs, cancellationToken)
            .ConfigureAwait(false);

        // ── 约束 2：只读命令遇设备占用时重试一次 ──
        //    退出码 2 在 list / input-status / test-profile-switch 上都表示
        //    "身份校验没通过或没有设备"，而这在 Space Station 占用时是暂时的。
        bool retried = false;
        if (spec.IsReadOnly && !result.TimedOut && result.ExitCode == 2
            && !cancellationToken.IsCancellationRequested)
        {
            AppLog.Info(LogFileName, $"{commandId} 首次失败（退出码 2），{ReadOnlyRetryDelay.TotalMilliseconds:F0} ms 后重试一次");
            try
            {
                await Task.Delay(ReadOnlyRetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户取消，直接返回首次结果
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                result = await runner
                    .RunAsync(arguments, spec.DefaultTimeoutMs, cancellationToken)
                    .ConfigureAwait(false);
                retried = true;
            }
        }

        EngineCommandOutcome outcome = BuildOutcome(spec, arguments, result, retried);

        AppLog.Info(LogFileName,
            $"{commandId} 结束：退出码={outcome.ExitCode} 耗时={outcome.Duration.TotalMilliseconds:F0} ms " +
            $"解析={(outcome.IsParsed ? "成功" : "降级原始文本")}{(retried ? "（含重试）" : string.Empty)}");

        return outcome;
    }

    /// <summary>
    /// 按命令类型解析输出并组装结论。
    /// </summary>
    private static EngineCommandOutcome BuildOutcome(
        EngineCommandSpec spec, string arguments, EngineResult result, bool retried)
    {
        string stdout = result.StandardOutput ?? string.Empty;
        string stderr = result.StandardError ?? string.Empty;
        string combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stdout + "\n" + stderr;

        object? parsed = null;
        bool isParsed = false;
        string parseError = string.Empty;

        if (!result.TimedOut && result.ExitCode == 0)
        {
            (parsed, isParsed, parseError) = ParseByCommand(spec.Id, stdout);
        }

        string summary = result.TimedOut
            ? LocalizationService.Shared.Format(
                "Loc_HwTimedOut", (spec.DefaultTimeoutMs / 1000).ToString(CultureInfo.InvariantCulture))
            : result.ExitCode == 0
                ? BuildSuccessSummary(spec, combined, parsed, isParsed)
                : BuildFailureSummary(spec.Id, result.ExitCode, stderr, stdout);

        if (retried && result.ExitCode == 0)
        {
            summary = summary + " " + LocalizationService.Shared.Get("Loc_HwRetriedNotice");
        }

        return new EngineCommandOutcome
        {
            CommandId = spec.Id,
            Arguments = arguments,
            ExitCode = result.TimedOut ? -1 : result.ExitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            Duration = result.Duration,
            TimedOut = result.TimedOut,
            IsParsed = isParsed,
            Parsed = parsed,
            Summary = summary,
            ParseError = parseError,
        };
    }

    /// <summary>按命令分派解析器。返回 (产物, 是否解析成功, 失败原因)。</summary>
    private static (object? Parsed, bool IsParsed, string Error) ParseByCommand(string commandId, string stdout)
    {
        switch (commandId)
        {
            case "list":
            {
                // list 无 --json，只能解析文本；没有候选设备时是合法结果而非解析失败
                if (EngineOutputParser.HasNoDevice(stdout))
                {
                    return (null, true, string.Empty);
                }

                IReadOnlyList<FlydigiDeviceInfo> devices = EngineOutputParser.ParseDeviceList(stdout);
                return devices.Count > 0
                    ? (devices, true, string.Empty)
                    : (null, false, "未能从 list 输出中解析出设备条目。");
            }

            case "identify":
                return EngineOutputParser.TryParseIdentity(stdout, out ApexIdentity? identity, out string idError)
                    ? (identity, true, string.Empty)
                    : (null, false, idError);

            case "diagnose":
                return EngineOutputParser.TryParseDiagnose(stdout, out HidDiagnosticReport? diag, out string diagError)
                    ? (diag, true, string.Empty)
                    : (null, false, diagError);

            case "input-status":
                return EngineOutputParser.TryParseInputStatus(stdout, out InputStatusReport? input, out string inputError)
                    ? (input, true, string.Empty)
                    : (null, false, inputError);

            case "virtual-ds":
                return EngineOutputParser.TryParseVirtualDs(stdout, out VirtualDsReport? vds, out string vdsError)
                    ? (vds, true, string.Empty)
                    : (null, false, vdsError);

            case "xinput-status":
                return EngineOutputParser.TryParseXInputSlots(stdout, out IReadOnlyList<int> slots, out string xiError)
                    ? (slots, true, string.Empty)
                    : (null, false, xiError);

            default:
                // 其余命令输出的是自然语言，不做结构化解析 —— 直接展示原文
                return (null, true, string.Empty);
        }
    }

    /// <summary>成功时的一句话结论（按命令定制，中文已本地化）。</summary>
    private static string BuildSuccessSummary(
        EngineCommandSpec spec, string rawText, object? parsed, bool isParsed)
    {
        ILocalizationService loc = LocalizationService.Shared;

        return spec.Id switch
        {
            "list" when parsed is IReadOnlyList<FlydigiDeviceInfo> devices
                => loc.Format("Loc_HwSummary_List", devices.Count),
            "list"
                => loc.Get("Loc_HwSummary_ListEmpty"),

            "identify" when parsed is ApexIdentity identity
                => loc.Format("Loc_HwSummary_Identify",
                    identity.ModelName, identity.Firmware, LinkText(identity.Link)),

            "diagnose" when parsed is HidDiagnosticReport report
                => loc.Format("Loc_HwSummary_Diagnose",
                    report.Count, report.Apex4Interfaces.Count),

            "input-status" when parsed is InputStatusReport status
                => loc.Format("Loc_HwSummary_InputStatus",
                    status.Reports, status.StateChanges, status.Timeouts),

            "virtual-ds" when parsed is VirtualDsReport vds
                => loc.Format("Loc_HwSummary_VirtualDs", vds.Backend),

            "xinput-status" when parsed is IReadOnlyList<int> slots
                => slots.Count == 0
                    ? loc.Get("Loc_HwSummary_XInputNone")
                    : loc.Format("Loc_HwSummary_XInput", string.Join(", ", slots)),

            // 测试类命令：只有出现引擎的成功标志句才算真的成功，
            // 避免"退出码 0 但其实没做任何事"被当成通过
            "test-rt" => loc.Get("Loc_HwSummary_TestRt"),
            "test-rumble" => loc.Get("Loc_HwSummary_TestRumble"),
            "test-profile-switch" => loc.Get("Loc_HwSummary_ProfileSwitch"),
            "apex4-port-test" => loc.Get("Loc_HwSummary_PortTest"),
            "clear" => loc.Get("Loc_HwSummary_Clear"),
            "dry-run" => loc.Get("Loc_HwSummary_DryRun"),
            "stop-active-sessions" => loc.Get("Loc_HwSummary_StopSessions"),
            "restore-controller-visibility" => loc.Get("Loc_HwSummary_RestoreVisibility"),

            _ => isParsed && parsed is null
                ? loc.Get("Loc_HwSummary_Generic")
                : loc.Get("Loc_HwSummary_Generic"),
        };
    }

    /// <summary>失败时的一句话结论：优先用退出码语义，没有语义时回落到引擎原文。</summary>
    private static string BuildFailureSummary(
        string commandId, int exitCode, string stderr, string stdout)
    {
        EngineCommandCatalog.ExitMeaning meaning =
            EngineCommandCatalog.DescribeExitCode(commandId, exitCode);

        ILocalizationService loc = LocalizationService.Shared;
        string message = loc.Get(meaning.Key);

        // 缺键回退：Get 在缺键时返回键名本身，此时用兜底中文
        if (string.IsNullOrEmpty(message) || message == meaning.Key)
        {
            message = meaning.Fallback;
        }

        if (message.Contains("{0}", StringComparison.Ordinal))
        {
            message = loc.Format(meaning.Key, exitCode);
            if (message == meaning.Key)
            {
                message = string.Format(CultureInfo.CurrentCulture, meaning.Fallback, exitCode);
            }
        }

        // 引擎自己的 stderr 往往比退出码更有信息量（例如"请先关闭飞智空间站"），
        // 有就附在后面 —— 这一段刻意保持英文原文，不做翻译。
        string engineDetail = string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
        if (!string.IsNullOrWhiteSpace(engineDetail))
        {
            message = message + Environment.NewLine + engineDetail;
        }

        return message;
    }

    private static string LinkText(LinkMode link) => LocalizationService.Shared.Get(link switch
    {
        LinkMode.Wired => "Loc_Link_Wired",
        LinkMode.Dongle => "Loc_Link_Dongle",
        LinkMode.Bluetooth => "Loc_Link_Bluetooth",
        _ => "Loc_Link_Unknown",
    });

    /// <summary>定位引擎路径（带缓存；解析失败不缓存，允许下次重试）。</summary>
    private string? ResolveEnginePath()
    {
        lock (_pathLock)
        {
            if (!string.IsNullOrWhiteSpace(_cachedEnginePath) && File.Exists(_cachedEnginePath))
            {
                return _cachedEnginePath;
            }

            string path = EngineLocator.ResolveEngine();
            _cachedEnginePath = string.IsNullOrWhiteSpace(path) ? null : path;
            return _cachedEnginePath;
        }
    }

    private static EngineCommandOutcome Failure(string commandId, string arguments, string message)
        => new()
        {
            CommandId = commandId,
            Arguments = arguments,
            ExitCode = -1,
            IsParsed = false,
            Summary = message,
        };
}
