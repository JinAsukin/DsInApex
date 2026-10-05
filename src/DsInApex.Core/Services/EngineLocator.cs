using System.Globalization;

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
/// <item>向上回溯祖先目录找 <c>vendor\portable-&lt;版本&gt;\ApexSenseBridge-Portable\ApexSenseBridge.exe</c>
/// —— 开发期布局（bin 深度约 7 层，所以要回溯）；vendor 下并存多个版本时<b>取版本最高的那个</b></item>
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
    /// 开发期 vendor 便携包的目录名前缀与包内引擎子目录。
    ///
    /// <para>
    /// 🔴 <b>这里刻意不写死具体版本号。</b>
    /// 早先写的是 <c>vendor\portable-1.0.0-beta.9\ApexSenseBridge-Portable</c>，
    /// 属于"改引擎时容易漏改的第四处"——<c>make-portable.ps1</c> 已经有两处要改，
    /// 再叠一处，实测就出现过 <c>vendor</c> 里 beta.9 / beta.10 并存、
    /// 开发期自检却仍然命中旧版的情况（表现是"新引擎的修复本地复现不出来"）。
    /// </para>
    ///
    /// <para>
    /// 现在改为扫描 <c>vendor\portable-*\</c> 并按<b>版本号降序</b>取第一个存在的，
    /// 多版本并存时结果确定且永远取最新。新增引擎只需把包放进来，无需改代码。
    /// </para>
    /// </summary>
    private const string VendorPortablePrefix = "portable-";

    /// <summary>便携包内的引擎子目录（上游打包布局，固定不变）。</summary>
    private const string PortablePayloadDir = "ApexSenseBridge-Portable";

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
                string? newest = FindNewestVendorPayload(vendorRoot, out string detail);
                if (newest is not null)
                {
                    steps.Add($"[开发布局] {newest} → 命中（{detail}）");
                    trace = steps;
                    return Path.GetFullPath(newest);
                }

                // vendor 存在但结构不认识 —— 保留一次递归兜底，至少不至于完全找不到
                string? loose = FindFirst(vendorRoot, EngineFileName);
                if (loose is not null)
                {
                    steps.Add($"[开发布局·递归] {loose} → 命中（vendor 下无标准便携包布局）");
                    trace = steps;
                    return Path.GetFullPath(loose);
                }

                steps.Add($"[开发布局] {vendorRoot} 已找到但无引擎（{detail}）");
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

    /// <summary>
    /// 在 <c>vendor</c> 下扫描 <c>portable-*</c>，返回<b>版本最高</b>且确实含引擎的那一个。
    ///
    /// <para>
    /// 排序完全确定（版本降序 → 目录名降序），所以多版本并存时结果可复现，
    /// 不会像"递归找第一个"那样依赖文件系统的返回顺序。
    /// </para>
    /// </summary>
    /// <param name="detail">供诊断轨迹展示的一句话说明（无论成功失败都有内容）。</param>
    private static string? FindNewestVendorPayload(string vendorRoot, out string detail)
    {
        List<string> candidates;
        try
        {
            candidates = [.. Directory.GetDirectories(vendorRoot, VendorPortablePrefix + "*")];
        }
        catch (Exception exception)
        {
            detail = $"枚举 vendor 失败：{exception.Message}";
            return null;
        }

        if (candidates.Count == 0)
        {
            detail = $"没有 {VendorPortablePrefix}* 目录";
            return null;
        }

        // 降序：把 (a,b) 反过来传给"x 比 y 新"的比较器，最新的排在最前
        candidates.Sort((a, b) => ComparePortableFolder(
            Path.GetFileName(b), Path.GetFileName(a)));

        foreach (string folder in candidates)
        {
            string exe = Path.Combine(folder, PortablePayloadDir, EngineFileName);
            if (File.Exists(exe))
            {
                detail = $"{candidates.Count} 个便携包中取版本最高者 {Path.GetFileName(folder)}";
                return exe;
            }
        }

        detail = $"{candidates.Count} 个 {VendorPortablePrefix}* 目录里都没有 " +
                 $"{PortablePayloadDir}\\{EngineFileName}";
        return null;
    }

    /// <summary>
    /// 比较两个 <c>portable-&lt;版本&gt;</c> 目录名的版本新旧。
    /// 返回 <c>&gt;0</c> 表示 <paramref name="x"/> 更新。
    ///
    /// <para>
    /// 只处理本项目实际会出现的形态（<c>0.6.3</c> / <c>1.0.0-beta.10</c>）：
    /// 数字段逐位比较 → 正式版优于预发布 → 预发布序号大者更优 →
    /// 最后按标签字典序兜底。<b>刻意不引入 SemVer 库</b>：
    /// 只为排目录名多一个依赖不划算，且真出现异常形态时此处的兜底也是确定的。
    /// </para>
    /// </summary>
    private static int ComparePortableFolder(string x, string y)
    {
        (int[] xNumbers, int xOrdinal, string xTag) = SplitPortableVersion(x);
        (int[] yNumbers, int yOrdinal, string yTag) = SplitPortableVersion(y);

        int length = Math.Max(xNumbers.Length, yNumbers.Length);
        for (int i = 0; i < length; i++)
        {
            int left = i < xNumbers.Length ? xNumbers[i] : 0;
            int right = i < yNumbers.Length ? yNumbers[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        if (xOrdinal != yOrdinal)
        {
            return xOrdinal.CompareTo(yOrdinal);   // 正式版取 int.MaxValue，故稳定版胜出
        }

        return string.CompareOrdinal(xTag, yTag);
    }

    /// <summary>
    /// 拆解 <c>portable-</c> 之后的版本串。
    /// 正式版（无预发布段）的序号取 <see cref="int.MaxValue"/>，因此排序时优于任何预发布。
    /// </summary>
    private static (int[] Numbers, int Ordinal, string Tag) SplitPortableVersion(string folderName)
    {
        string name = folderName.StartsWith(VendorPortablePrefix, StringComparison.OrdinalIgnoreCase)
            ? folderName[VendorPortablePrefix.Length..]
            : folderName;

        int dash = name.IndexOf('-', StringComparison.Ordinal);
        string numeric = dash >= 0 ? name[..dash] : name;
        string tag = dash >= 0 ? name[(dash + 1)..] : string.Empty;

        var numbers = new List<int>();
        foreach (string part in numeric.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            numbers.Add(int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
                ? value
                : 0);
        }

        if (tag.Length == 0)
        {
            return ([.. numbers], int.MaxValue, string.Empty);
        }

        // 预发布序号取标签里最后一段数字：beta.10 → 10，beta → 0
        int ordinal = 0;
        foreach (string part in tag.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                ordinal = value;
            }
        }

        return ([.. numbers], ordinal, tag);
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
