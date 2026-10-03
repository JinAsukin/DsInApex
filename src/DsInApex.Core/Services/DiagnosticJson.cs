using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DsInApex.Core.Services;

/// <summary>
/// 诊断报告的统一 JSON 序列化配置。
///
/// <para>
/// <b>两个刻意的选择：</b>
/// </para>
///
/// <list type="bullet">
/// <item><b>snake_case</b>：与上游 PowerShell 脚本（<c>ConvertTo-Json</c> 前的
/// <c>[ordered]@{}</c> 键名）保持一致，报告可以直接对照着看。</item>
///
/// <item><b>不转义非 ASCII</b>：设备名称常是中文
/// （实测：<c>符合 HID 标准的供应商定义设备</c>）。默认的
/// <see cref="JavaScriptEncoder"/> 会把它们写成 <c>\u7B26\u5408...</c>，
/// 报告就没法肉眼读了。这里写的是本地文件、不进 HTML，放宽转义是安全的。
/// </item>
/// </list>
/// </summary>
internal static class DiagnosticJson
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize<T>(T value, bool indented = false)
        => JsonSerializer.Serialize(value, indented ? Indented : Compact) + Environment.NewLine;
}
