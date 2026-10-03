namespace DsInApex.Core.Models;

/// <summary>
/// 便携（portable）部署布局报告（P6）。
///
/// <para>
/// DIA 的交付形态是 <b>解压即用</b>：不写注册表安装项、不落地到 <c>%ProgramFiles%</c>、
/// 卸载等于删除目录。因此「部署是否完整」必须能被程序自己检查出来 ——
/// 这就是本报告存在的意义：它把「引擎在不在、前置安装器在不在、许可证在不在、
/// 用户数据落在哪」逐条摊开，供设置页显示与自检判定。
/// </para>
///
/// <para>
/// ⚠️ 兼容红线：<b>用户数据目录不随便携化搬迁</b>。设置文件仍是
/// <c>%LOCALAPPDATA%\ApexSenseBridge\tray_settings.json</c>，日志目录同理
/// —— 这是与上游官方版共用同一份数据的唯一前提（见 README §4.4）。
/// 便携化改变的是「程序放哪」，不是「数据放哪」。
/// </para>
/// </summary>
public sealed record PortableLayoutReport
{
    /// <summary>应用根目录（<c>AppContext.BaseDirectory</c>，末尾带分隔符）。</summary>
    public required string AppDirectory { get; init; }

    /// <summary>主程序 exe 的绝对路径。</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>引擎可执行文件路径（未找到时为空串）。</summary>
    public required string EnginePath { get; init; }

    /// <summary>引擎是否随包提供。</summary>
    public required bool EnginePresent { get; init; }

    /// <summary>驱动前置安装器目录（<c>&lt;app&gt;\Prerequisites</c>）。</summary>
    public required string PrerequisitesDirectory { get; init; }

    /// <summary>该目录下实际存在的安装器文件数。</summary>
    public required int PrerequisiteFileCount { get; init; }

    /// <summary>用户数据目录（固定为 <c>%LOCALAPPDATA%\ApexSenseBridge</c>，与上游共用）。</summary>
    public required string DataDirectory { get; init; }

    /// <summary>设置文件路径。</summary>
    public required string SettingsPath { get; init; }

    /// <summary>设置文件当前是否存在。</summary>
    public required bool SettingsExists { get; init; }

    /// <summary>许可证文件路径（GPL-3.0 合规要求随包分发）。</summary>
    public required string LicensePath { get; init; }

    /// <summary>许可证文件是否存在。</summary>
    public required bool LicensePresent { get; init; }

    /// <summary>第三方声明文件是否存在（GPL 合规要件之一）。</summary>
    public required bool ThirdPartyNoticesPresent { get; init; }

    /// <summary>源码获取说明是否存在。</summary>
    public required bool SourceOfferPresent { get; init; }

    /// <summary>应用目录是否可写（决定能否当作真正的绿色版使用）。</summary>
    public required bool AppDirectoryWritable { get; init; }

    /// <summary>本机是否为「安装式」布局残留（同目录下发现安装器痕迹时的提示）。</summary>
    public required bool LooksInstalled { get; init; }

    /// <summary>逐条说明（供设置页与自检日志直接展示）。</summary>
    public required IReadOnlyList<string> Notes { get; init; }

    /// <summary>
    /// 便携部署是否「可用」：引擎与许可证齐备即可跑。
    /// 前置安装器缺失只影响驱动安装功能，不阻塞主程序。
    /// </summary>
    public bool IsDeploymentUsable => EnginePresent && LicensePresent;
}
