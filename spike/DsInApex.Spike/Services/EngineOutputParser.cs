using System.Text.RegularExpressions;
using DsInApex.Spike.Models;

namespace DsInApex.Spike.Services;

/// <summary>
/// 引擎文本输出解析层。
///
/// 为什么必须存在：<c>list</c> 命令**不支持 --json**（只有 diagnose / input-status /
/// virtual-ds 支持），设备列表只能解析文本。因此本层集中管理所有正则，
/// 并且**解析失败必须降级为显示原始文本**，绝不抛异常。
///
/// 对应上游输出格式（src/cli/DeviceCommands.cpp:235 + CommandSupport.cpp:172）：
/// <code>
/// Found 2 candidate(s):
///
/// [0] Flydigi APEX 4
///     VID:PID      04B4:2412
///     Usage page   0x0001  usage 0x0004
///     Reports      input=64 output=64 bytes
/// </code>
/// </summary>
public static partial class EngineOutputParser
{
    public const string NoDeviceMarker = "No APEX 4/5 vendor HID interface found";

    [GeneratedRegex(@"^\[(\d+)\]\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex EntryRegex();

    [GeneratedRegex(@"VID:PID\s+([0-9A-Fa-f]{4}):([0-9A-Fa-f]{4})")]
    private static partial Regex VidPidRegex();

    [GeneratedRegex(@"Usage page\s+(0x[0-9A-Fa-f]+)\s+usage\s+(0x[0-9A-Fa-f]+)")]
    private static partial Regex UsageRegex();

    [GeneratedRegex(@"Reports\s+input=(\d+)\s+output=(\d+)\s+bytes")]
    private static partial Regex ReportsRegex();

    /// <summary>引擎是否明确报告"未找到设备"。</summary>
    public static bool HasNoDevice(string stdout)
        => stdout.Contains(NoDeviceMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>引擎是否报告"找到 N 个候选"。</summary>
    public static bool HasCandidates(string stdout)
        => stdout.Contains("candidate(s):", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 解析设备列表。任何一步不匹配都只是"该项缺失"，不抛异常；
    /// 完全解析不出条目时返回空列表，由调用方降级展示原始文本。
    /// </summary>
    public static IReadOnlyList<FlydigiDeviceInfo> ParseDeviceList(string stdout)
    {
        var results = new List<FlydigiDeviceInfo>();
        if (string.IsNullOrWhiteSpace(stdout)) return results;

        MatchCollection entries = EntryRegex().Matches(stdout);
        if (entries.Count == 0) return results;

        for (int i = 0; i < entries.Count; i++)
        {
            Match entry = entries[i];

            // 每个条目的正文区间 = 本条命中之后 → 下一条命中之前
            int blockStart = entry.Index + entry.Length;
            int blockEnd = (i + 1 < entries.Count) ? entries[i + 1].Index : stdout.Length;
            string block = stdout[blockStart..blockEnd];

            if (!int.TryParse(entry.Groups[1].Value, out int index))
            {
                index = i;   // 序号解析失败就用位置序号兜底
            }

            string product = entry.Groups[2].Value.Trim();

            var vidPid = VidPidRegex().Match(block);
            var usage = UsageRegex().Match(block);
            var reports = ReportsRegex().Match(block);

            results.Add(new FlydigiDeviceInfo(
                Index: index,
                Product: product,
                VendorId: vidPid.Success ? vidPid.Groups[1].Value.ToUpperInvariant() : "----",
                ProductId: vidPid.Success ? vidPid.Groups[2].Value.ToUpperInvariant() : "----",
                UsagePage: usage.Success ? usage.Groups[1].Value : "?",
                Usage: usage.Success ? usage.Groups[2].Value : "?",
                InputReportLength: reports.Success && int.TryParse(reports.Groups[1].Value, out int inLen) ? inLen : -1,
                OutputReportLength: reports.Success && int.TryParse(reports.Groups[2].Value, out int outLen) ? outLen : -1));
        }

        return results;
    }
}
