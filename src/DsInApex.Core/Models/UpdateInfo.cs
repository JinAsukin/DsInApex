namespace DsInApex.Core.Models;

/// <summary>
/// 更新检查结果（P6）。
///
/// <para>
/// ⚠️ 与上游的关键差异：上游 <c>UpdateCheckerService</c> 硬编码
/// <c>ReynArts/ApexSenseBridge</c>，并会下载 + 校验 + <b>直接启动官方安装器</b>。
/// DIA 是衍生作品，既不该把用户送去装上游版本（那会把 DIA 覆盖掉），
/// 也不该沿用「下载安装器并执行」这套逻辑 —— <b>便携版没有安装器可跑</b>。
/// 因此这里的动作收敛为：查版本 → 告诉用户 → 打开浏览器到发布页，
/// 由用户自己下载新目录替换（便携版的标准升级姿势）。
/// </para>
/// </summary>
public sealed record UpdateInfo
{
    /// <summary>检查动作本身是否成功完成（网络/解析没出错）。</summary>
    public required bool CheckSucceeded { get; init; }

    /// <summary>本机版本。</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>远端最新版本（无发布时为 null）。</summary>
    public string? LatestVersion { get; init; }

    /// <summary>是否确实有新版本。</summary>
    public required bool HasUpdate { get; init; }

    /// <summary>发布说明正文。</summary>
    public string? ReleaseNotes { get; init; }

    /// <summary>发布页地址（用于「打开浏览器」）。</summary>
    public string? ReleaseUrl { get; init; }

    /// <summary>便携包直链（若发布资产里有 <c>.zip</c>）。</summary>
    public string? PortableAssetUrl { get; init; }

    /// <summary>失败原因（成功时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>「尚无发布版本」这类非错误的空结果。</summary>
    public bool IsNoReleaseYet => CheckSucceeded && string.IsNullOrEmpty(LatestVersion);
}
