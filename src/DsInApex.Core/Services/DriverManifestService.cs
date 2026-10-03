using System.Reflection;
using System.Text;
using System.Text.Json;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 驱动清单服务：读嵌入的 <c>driver-manifest.json</c>，产出 <see cref="DriverInfo"/> 列表。
///
/// <para>
/// <b>背景：</b>上游这份信息散在三处 ——
/// <c>installer/driver-manifest.json</c>（机器可读）、
/// <c>installer/ApexSenseBridge.iss</c>（<c>#define</c> 常量）、
/// <c>installer/install-usbip.ps1</c>（又硬编码一遍版本号与 SHA-256）。
/// 三处各写一遍，改版本时极易漏改。<b>DIA 收敛为单一真源 = 这份嵌入 JSON。</b>
/// </para>
/// </summary>
public sealed class DriverManifestService
{
    private const string LogFileName = "dsinapex_drivers.log";

    /// <summary>嵌入资源名 = <c>&lt;RootNamespace&gt;.&lt;LogicalName&gt;</c>。</summary>
    private const string EmbeddedResourceName = "DsInApex.Core.Data.driver-manifest.json";

    /// <summary>顶层非驱动对象的键（元数据）。</summary>
    private static readonly HashSet<string> MetaKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "schema",
    };

    private IReadOnlyList<DriverInfo>? _cache;

    /// <summary>载入清单（幂等，结果缓存）。失败时返回空列表并记日志。</summary>
    public IReadOnlyList<DriverInfo> Load()
    {
        if (_cache is not null) return _cache;

        try
        {
            string? json = ReadEmbeddedJson();
            if (string.IsNullOrWhiteSpace(json))
            {
                AppLog.Warn(LogFileName, "驱动清单嵌入资源缺失");
                return _cache = [];
            }

            _cache = Parse(json);
            AppLog.Info(LogFileName, $"驱动清单已载入：{_cache.Count} 项（{string.Join(", ", _cache.Select(x => x.Id))}）");
            return _cache;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"驱动清单解析失败：{AppLog.Describe(ex)}");
            return _cache = [];
        }
    }

    public DriverInfo? Find(string id)
        => Load().FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));

    private static string? ReadEmbeddedJson()
    {
        Assembly assembly = Assembly.GetExecutingAssembly();
        using Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
        if (stream is null) return null;

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static List<DriverInfo> Parse(string json)
    {
        var result = new List<DriverInfo>();

        using JsonDocument doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return result;

        foreach (JsonProperty property in doc.RootElement.EnumerateObject())
        {
            if (MetaKeys.Contains(property.Name)) continue;
            if (property.Value.ValueKind != JsonValueKind.Object) continue;

            JsonElement item = property.Value;
            result.Add(new DriverInfo
            {
                Id = property.Name,
                DisplayName = GetString(item, "displayName", property.Name),
                Version = GetString(item, "version", string.Empty),
                Installer = GetString(item, "installer", string.Empty),
                Sha256 = GetString(item, "sha256", string.Empty).ToUpperInvariant(),
                ProductCode = GetString(item, "productCode", string.Empty),
                Services = GetStringArray(item, "services"),
                OriginalInf = GetStringArray(item, "originalInf"),
                InstallArgs = GetStringArray(item, "installArgs"),
                LogArgTemplate = GetString(item, "logArgTemplate", string.Empty),
                Optional = GetBool(item, "optional"),
                Note = GetString(item, "note", string.Empty),
            });
        }

        return result;
    }

    private static string GetString(JsonElement item, string name, string fallback)
    {
        if (!item.TryGetProperty(name, out JsonElement value)) return fallback;
        if (value.ValueKind != JsonValueKind.String) return fallback;
        string? text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    private static string[] GetStringArray(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (JsonElement element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } s)
            {
                list.Add(s);
            }
        }
        return [.. list];
    }

    private static bool GetBool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) && parsed,
            _ => false,
        };
    }
}
