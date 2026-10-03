using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 引擎输出解析层 · P5 扩展部分（结构化报告）。
///
/// <para>
/// <b>与主文件的契约一致：</b>任何解析失败都<b>不抛异常</b>，
/// 只把失败原因写进 <c>error</c>，由调用方降级为展示原始文本。
/// 引擎输出格式是已知脆弱项（上游改一个空格就能让正则失配），
/// 因此"解析不出来"必须是可见的、可诊断的，而不是静默变成"无数据"。
/// </para>
///
/// <para>
/// 本文件中的每个字段名都对照过
/// <c>engine/src/cli/DeviceCommands.cpp</c>、<c>VirtualDualSenseCommand.cpp</c>
/// 的实际 <c>std::cout</c> 语句与 2026-10-03 的实测输出。
/// </para>
/// </summary>
public static partial class EngineOutputParser
{
    /// <summary>
    /// JSON 反序列化选项。
    /// <para>
    /// 模型上普遍标了 <c>[JsonPropertyName]</c>，此处再加 <c>SnakeCaseLower</c>
    /// 作为兜底 —— 万一将来引擎新增字段而我们漏标，仍能对上。
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    // ────────────────────────── identify ──────────────────────────

    [GeneratedRegex(@"^Verified:\s*(?<model>[^(]+?)\s*\((?<meta>[^)]*)\)",
        RegexOptions.Multiline)]
    private static partial Regex IdentityLineRegex();

    [GeneratedRegex(@"DeviceType\s+(?<type>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceTypeRegex();

    [GeneratedRegex(@"firmware\s+(?<fw>0[xX][0-9A-Fa-f]+)", RegexOptions.IgnoreCase)]
    private static partial Regex FirmwareRegex();

    [GeneratedRegex(@"^Connection:\s*(?<link>\w+)\s*\(raw\s*(?<raw>\d+)\)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionRegex();

    [GeneratedRegex(@"^Battery level:\s*(?<pct>\d+)\s*(?<charging>\(charging\))?",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex BatteryRegex();

    [GeneratedRegex(@"^Adaptive triggers:\s*(?<value>yes|no)",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AdaptiveRegex();

    /// <summary>
    /// 解析 <c>identify</c> 的文本输出。
    ///
    /// <para>实测样本（2026-10-03）：</para>
    /// <code>
    /// Verified: Apex 4 (k2, DeviceType 84, firmware 0x6837)
    /// Connection: dongle (raw 0)
    /// Adaptive triggers: yes
    /// </code>
    /// </summary>
    public static bool TryParseIdentity(string stdout, out ApexIdentity? identity, out string error)
    {
        identity = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            error = "identify 没有产生任何输出。";
            return false;
        }

        Match head = IdentityLineRegex().Match(stdout);
        if (!head.Success)
        {
            error = "未找到 `Verified:` 行——引擎输出格式可能已变化。";
            return false;
        }

        string model = head.Groups["model"].Value.Trim();
        string meta = head.Groups["meta"].Value;

        // 代号 = 括号内第一个"既不是 DeviceType 也不是 firmware"的片段
        string codename = string.Empty;
        foreach (string segment in meta.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = segment.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("DeviceType", StringComparison.OrdinalIgnoreCase)) continue;
            if (trimmed.StartsWith("firmware", StringComparison.OrdinalIgnoreCase)) continue;
            codename = trimmed;
            break;
        }

        Match deviceType = DeviceTypeRegex().Match(meta);
        Match firmware = FirmwareRegex().Match(meta);
        Match connection = ConnectionRegex().Match(stdout);
        Match battery = BatteryRegex().Match(stdout);
        Match adaptive = AdaptiveRegex().Match(stdout);

        LinkMode link = LinkMode.Unknown;
        if (connection.Success)
        {
            link = connection.Groups["link"].Value.ToUpperInvariant() switch
            {
                "WIRED" => LinkMode.Wired,
                "DONGLE" => LinkMode.Dongle,
                "BLUETOOTH" or "BTH" => LinkMode.Bluetooth,
                _ => LinkMode.Unknown,
            };
        }

        identity = new ApexIdentity
        {
            ModelName = model,
            Codename = codename,
            DeviceType = deviceType.Success && int.TryParse(
                deviceType.Groups["type"].Value, CultureInfo.InvariantCulture, out int dt) ? dt : null,
            Firmware = firmware.Success ? firmware.Groups["fw"].Value : string.Empty,
            Link = link,
            LinkRaw = connection.Success && int.TryParse(
                connection.Groups["raw"].Value, CultureInfo.InvariantCulture, out int raw) ? raw : null,
            BatteryPercent = battery.Success && int.TryParse(
                battery.Groups["pct"].Value, CultureInfo.InvariantCulture, out int pct) ? pct : null,
            IsCharging = battery.Success && battery.Groups["charging"].Success,
            // 引擎只有在确实支持时才打印这一行；缺失即视为不支持
            AdaptiveTriggers = adaptive.Success
                && adaptive.Groups["value"].Value.Equals("yes", StringComparison.OrdinalIgnoreCase),
        };
        return true;
    }

    // ────────────────────────── JSON 类报告 ──────────────────────────

    /// <summary>
    /// 从文本中截出最外层的 JSON 对象。
    ///
    /// <para>
    /// 引擎可能在 JSON 前后混入别的行（例如 stderr 的枚举告警被合并进同一段文本），
    /// 因此不能直接把整段丢给反序列化。
    /// </para>
    /// </summary>
    private static bool TryExtractJsonObject(string text, out string json)
    {
        json = string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        json = text[start..(end + 1)];
        return true;
    }

    private static bool TryParseJsonReport<T>(
        string text, string commandName, out T? value, out string error) where T : class
    {
        value = null;
        error = string.Empty;

        if (!TryExtractJsonObject(text, out string json))
        {
            error = $"{commandName} 的输出里没有找到 JSON 对象——可能命令失败并返回了纯文本。";
            return false;
        }

        try
        {
            T? parsed = JsonSerializer.Deserialize<T>(json, ReportJsonOptions);
            if (parsed is null)
            {
                error = $"{commandName} 的 JSON 反序列化结果为 null。";
                return false;
            }

            value = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"{commandName} 的 JSON 解析失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>解析 <c>input-status --json</c>。</summary>
    public static bool TryParseInputStatus(
        string stdout, out InputStatusReport? report, out string error)
        => TryParseJsonReport(stdout, "input-status", out report, out error);

    /// <summary>解析 <c>virtual-ds --json</c>。</summary>
    public static bool TryParseVirtualDs(
        string stdout, out VirtualDsReport? report, out string error)
        => TryParseJsonReport(stdout, "virtual-ds", out report, out error);

    /// <summary>解析 <c>diagnose --json</c>。</summary>
    public static bool TryParseDiagnose(
        string stdout, out HidDiagnosticReport? report, out string error)
        => TryParseJsonReport(stdout, "diagnose", out report, out error);

    // ────────────────────────── 纯文本类结果 ──────────────────────────

    [GeneratedRegex(@"^connected_xinput=(?<value>.+)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex XInputStatusRegex();

    /// <summary>
    /// 解析 <c>xinput-status</c>：返回已连接的 XInput 槽位。
    ///
    /// <para>实测输出：<c>connected_xinput=none</c> 或 <c>connected_xinput=0,1</c>。</para>
    /// </summary>
    public static bool TryParseXInputSlots(string stdout, out IReadOnlyList<int> slots, out string error)
    {
        slots = [];
        error = string.Empty;

        Match match = XInputStatusRegex().Match(stdout);
        if (!match.Success)
        {
            error = "未找到 `connected_xinput=` 行。";
            return false;
        }

        string value = match.Groups["value"].Value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return true;   // 合法结果：没有任何 XInput 设备
        }

        var parsed = new List<int>();
        foreach (string token in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token.Trim(), CultureInfo.InvariantCulture, out int slot))
            {
                parsed.Add(slot);
            }
        }

        slots = parsed;
        return true;
    }

    /// <summary>
    /// 从命令的成功输出里提取"用户实际能感觉到什么"的判定依据。
    ///
    /// <para>
    /// 引擎对测试类命令的成功提示是自然语言（英文），例如
    /// <c>If you felt a resistance begin part-way through RT, the Windows -&gt; APEX FORCEADAPT path works.</c>
    /// 我们不翻译它，只判断"是否出现了成功标志句"，由界面给中文结论。
    /// </para>
    /// </summary>
    public static bool HasSuccessMarker(string commandId, string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return false;

        return commandId switch
        {
            "test-rt" => stdout.Contains("RT reset to Normal", StringComparison.OrdinalIgnoreCase),
            "test-rumble" => stdout.Contains("Grip rumble stopped", StringComparison.OrdinalIgnoreCase),
            "clear" => stdout.Contains("reset to Normal", StringComparison.OrdinalIgnoreCase),
            "test-profile-switch" => stdout.Contains("Profile-switch diagnostic passed", StringComparison.OrdinalIgnoreCase),
            "restore-controller-visibility" => stdout.Contains("restored", StringComparison.OrdinalIgnoreCase),
            "stop-active-sessions" => stdout.Contains("No active bridge remains", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }
}
