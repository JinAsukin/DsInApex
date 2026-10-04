namespace DsInApex.Core.Services;

/// <summary>
/// 引擎可执行文件定位器（DIA 版，精简重写）。
///
/// <para>
/// <b>为什么不照搬上游 <c>InstallLocator</c>：</b>上游是「安装式 WPF 托盘」，
/// 要查注册表 <c>HKLM\SOFTWARE\ApexSenseBridge</c>（32/64 两个视图）、
/// <c>%ProgramFiles%</c>、以及 <c>build-win\Release</c> 之类构建目录；
/// 而 DIA 是 <b>unpackaged 自包含</b>应用，引擎就放在应用目录旁边，
/// <b>不查别人装的引擎</b>。因此 146 行砍到约 60 行，且不引入
/// <c>Microsoft.Win32.Registry</c> 包（net10.0 下非内置，要显式引用）。
/// </para>
///
/// <para>优先级（高 → 低）：</para>
/// <list type="number">
/// <item>环境变量 <c>DSINAPEX_ENGINE_PATH</c>（Spike 期就有的调试开关，保留）</item>
/// <item><c>&lt;应用目录&gt;\engine\ApexSenseBridge.exe</c> —— 安装后布局</item>
/// <item>向上回溯祖先目录找 <c>vendor\portable-1.0.0-beta.9\ApexSenseBridge-Portable\ApexSenseBridge.exe</c>
/// —— 开发期布局（bin 深度约 7 层，所以要回溯）</item>
/// <item><c>&lt;应用目录&gt;\ApexSenseBridge.exe</c> —— 引擎与主程序同目录</item>
/// </list>
///
/// <para>找不到时返回 <see cref="string.Empty"/>（与上游契约一致，不抛异常）。</para>
/// </summary>
public static class EngineLocator
{
    /// <summary>引擎文件名（与上游一致，品牌化不改 —— 引擎是上游原样的二进制）。</summary>
    public const string EngineFileName = "ApexSenseBridge.exe";

    /// <summary>环境变量名：直接指定引擎 exe 或所在目录。</summary>
    public const string EnginePathEnvVar = "DSINAPEX_ENGINE_PATH";

    /// <summary>
    /// 开发期 vendor 布局的相对路径。
    ///
    /// <para>
    /// ⚠️ <b>升级引擎时这里必须同步改</b>（2026-10-03：0.6.3 → 1.0.0-beta.9）。
    /// 下方另有「vendor 下递归兜底」保证改漏了也能找到，但
    /// <b>多版本并存时递归会取哪一个是不确定的</b> —— 所以别依赖兜底。
    /// </para>
    /// </summary>
    private const string VendorRelative = @"vendor\portable-1.0.0-beta.9\ApexSenseBridge-Portable";

    /// <summary>向祖先回溯的最大层数（防御性上限）。</summary>
    private const int MaxAncestorDepth = 8;

    /// <summary>
    /// 解析引擎路径。<paramref name="legacyPath"/> 为上游遗留的候选路径（兜底）。
    /// </summary>
    public static string ResolveEngine(string? legacyPath = null)
        => ResolveInternal(legacyPath, out _);

    /// <summary>
    /// 解析引擎路径，并输出逐条候选的探查轨迹（供自检与诊断页展示）。
    /// </summary>
    public static string ResolveEngineWithTrace(out IReadOnlyList<string> trace)
        => ResolveInternal(null, out trace);

    private static string ResolveInternal(string? legacyPath, out IReadOnlyList<string> trace)
    {
        var steps = new List<string>();

        // ── 1. 环境变量覆盖 ──
        string? env = Environment.GetEnvironmentVariable(EnginePathEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
        {
            string candidate = env;
            if (Directory.Exists(candidate))
            {
                candidate = Path.Combine(candidate, EngineFileName);
            }

            bool hit = File.Exists(candidate);
            steps.Add($"[环境变量] {candidate} → {(hit ? "命中" : "不存在")}");
            if (hit)
            {
                trace = steps;
                return Path.GetFullPath(candidate);
            }
        }
        else
        {
            steps.Add($"[环境变量] {EnginePathEnvVar} 未设置");
        }

        string appDir = AppContext.BaseDirectory;

        // ── 2. 安装后布局：<app>\engine\ ──
        if (!string.IsNullOrWhiteSpace(appDir))
        {
            string installed = Path.Combine(appDir, "engine", EngineFileName);
            bool hit = File.Exists(installed);
            steps.Add($"[安装布局] {installed} → {(hit ? "命中" : "不存在")}");
            if (hit)
            {
                trace = steps;
                return Path.GetFullPath(installed);
            }
        }

        // ── 3. 开发期布局：向祖先回溯找 vendor ──
        DirectoryInfo? dir = TryGetDirectory(appDir);
        for (int depth = 0; dir is not null && depth < MaxAncestorDepth; depth++)
        {
            string vendorRoot = Path.Combine(dir.FullName, "vendor");
            if (Directory.Exists(vendorRoot))
            {
                string devLayout = Path.Combine(dir.FullName, VendorRelative, EngineFileName);
                if (File.Exists(devLayout))
                {
                    steps.Add($"[开发布局] {devLayout} → 命中");
                    trace = steps;
                    return Path.GetFullPath(devLayout);
                }

                // 版本号可能变（portable-0.6.4 …），故兜底在 vendor 下递归找一次
                string? loose = FindFirst(vendorRoot, EngineFileName);
                if (loose is not null)
                {
                    steps.Add($"[开发布局·递归] {loose} → 命中");
                    trace = steps;
                    return Path.GetFullPath(loose);
                }

                steps.Add($"[开发布局] {devLayout} → 不存在（vendor 已找到但无引擎）");
            }

            dir = dir.Parent;
        }

        // ── 4. 同目录 ──
        if (!string.IsNullOrWhiteSpace(appDir))
        {
            string sideBySide = Path.Combine(appDir, EngineFileName);
            bool hit = File.Exists(sideBySide);
            steps.Add($"[同目录] {sideBySide} → {(hit ? "命中" : "不存在")}");
            if (hit)
            {
                trace = steps;
                return Path.GetFullPath(sideBySide);
            }
        }

        // ── 5. 遗留路径兜底 ──
        if (!string.IsNullOrWhiteSpace(legacyPath) && File.Exists(legacyPath))
        {
            steps.Add($"[遗留路径] {legacyPath} → 命中");
            trace = steps;
            return Path.GetFullPath(legacyPath);
        }

        steps.Add("结论：未找到引擎可执行文件");
        trace = steps;
        return string.Empty;
    }

    private static DirectoryInfo? TryGetDirectory(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? null : new DirectoryInfo(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFirst(string root, string fileName)
    {
        try
        {
            return Directory
                .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            // vendor 目录不可读时继续走后续策略
            return null;
        }
    }
}
