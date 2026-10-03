using DsInApex.Core.Logging;

namespace DsInApex.App.Diagnostics;

/// <summary>
/// 应用自检。
///
/// 触发方式：设置环境变量 <c>DIA_SELFTEST=1</c> 后启动应用。
///
/// <para>
/// P1 用它验收「导航壳可用 + 中英热切换生效」—— 这两项都依赖 UI 交互，
/// 没有钩子就只能靠人工点，无法留下可复查的证据。
/// P8 的手柄实测验收可以在此基础上扩展真实硬件检查。
/// </para>
///
/// <para>
/// 自检只读不写业务状态：导航会在页面上留下痕迹，语言会临时切到英文再切回，
/// 但**不会**修改 tray_settings.json。
/// </para>
/// </summary>
public static class SelfTest
{
    /// <summary>环境变量名。</summary>
    public const string EnvVarName = "DIA_SELFTEST";

    private const string LogFileName = "dsinapex_selftest.log";

    /// <summary>本次启动是否请求了自检。</summary>
    public static bool IsRequested =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVarName), "1", StringComparison.Ordinal);

    public static void Log(string message) => AppLog.Info(LogFileName, message);

    public static void LogHeader(string title)
        => AppLog.Info(LogFileName, $"===== {title} =====");
}
