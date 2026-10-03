namespace DsInApex.Core.Models;

/// <summary>
/// 一条「已学习」的可执行文件 → 游戏绑定关系（持久化到 <c>learned_executables.json</c>）。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Models/LearnedExecutableBinding.cs</c>（42 行）。
/// </para>
///
/// <para>
/// <b>⚠️ 该模型是跨应用共享契约</b>：DIA 与官方版共用同一份
/// <c>%LOCALAPPDATA%\ApexSenseBridge\learned_executables.json</c>，
/// 因此字段名（写入 JSON 时的键名）与服务端解析逻辑都<b>不可改</b>。
/// </para>
/// </summary>
public sealed class LearnedExecutableBinding
{
    /// <summary>可执行文件绝对路径（归一化后作为主键）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>文件名（冗余存储，便于展示）。</summary>
    public string Executable { get; set; } = string.Empty;

    public string GameTitle { get; set; } = string.Empty;

    public string GameNormalized { get; set; } = string.Empty;

    public int SteamAppId { get; set; }

    /// <summary>识别方式描述（如 <c>poll 'foo.exe'</c>）。</summary>
    public string DetectionMethod { get; set; } = string.Empty;

    /// <summary>首次识别时间（ISO 8601，UTC）。</summary>
    public string FirstSeenUtc { get; set; } = string.Empty;

    /// <summary>最近识别时间（ISO 8601，UTC）。</summary>
    public string LastSeenUtc { get; set; } = string.Empty;

    public int SuccessfulSessions { get; set; }

    public LearnedExecutableBinding Clone() => new()
    {
        Path = Path,
        Executable = Executable,
        GameTitle = GameTitle,
        GameNormalized = GameNormalized,
        SteamAppId = SteamAppId,
        DetectionMethod = DetectionMethod,
        FirstSeenUtc = FirstSeenUtc,
        LastSeenUtc = LastSeenUtc,
        SuccessfulSessions = SuccessfulSessions,
    };
}
