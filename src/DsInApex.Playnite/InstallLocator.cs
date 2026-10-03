using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace DsInApex.Playnite
{
    /// <summary>
    /// 部署位置定位器（P9 重写）。
    ///
    /// <para>
    /// <b>为什么不沿用上游的 <c>InstallLocator</c>：</b>上游查的是
    /// <c>HKLM\SOFTWARE\ApexSenseBridge</c> 的 <c>ExecutablePath</c>/<c>InstallPath</c>
    /// —— 那是「官方安装器写注册表」时代的路子。DIA 是 <b>portable 绿色包</b>，
    /// 解压即用、<b>不写任何 HKLM 安装项</b>，所以注册表里永远查不到，
    /// 必须改成「沿目录向上找 <c>DsInApex.exe</c>」。
    /// （结论来自 P8 的 S2/S3：含空格路径 + 整目录搬家后，上溯查找依然成立。）
    /// </para>
    ///
    /// <para><b>部署根判定 = 目录里存在 <c>DsInApex.exe</c></b>（主程序名，不是引擎名）。
    /// 引擎随后按 <c>&lt;部署根&gt;\engine\ApexSenseBridge.exe</c> 解析。</para>
    ///
    /// <para><b>部署根探测顺序（高 → 低）：</b></para>
    /// <list type="number">
    /// <item>用户在插件设置里显式指定的路径（权威，便携部署的兜底）</item>
    /// <item><b>正在运行的 <c>DsInApex</c> 进程主模块路径</b> —— DIA 是常驻托盘程序，
    /// 这个探针在真实使用场景里命中率最高，且完全不需要注册表</item>
    /// <item>从插件自身目录向上回溯（最多 <see cref="MaxAncestorDepth"/> 层）</item>
    /// <item>开发期布局：祖先目录里有 <c>DsInApex.sln</c> 时，找
    /// <c>src\DsInApex.App\bin\**\DsInApex.exe</c>（方便本机联调，免配置）</item>
    /// <item>常见部署位置：<c>%ProgramFiles%\DsInApex</c>、<c>%LOCALAPPDATA%\Programs\DsInApex</c>、
    /// 各固定磁盘根下的 <c>\DsInApex</c> / <c>\Games\DsInApex</c> / <c>\Portable\DsInApex</c> / <c>\Tools\DsInApex</c></item>
    /// </list>
    ///
    /// <para>找不到时返回 <see cref="string.Empty"/>（不抛异常，与上游契约一致）。</para>
    /// </summary>
    internal static class InstallLocator
    {
        /// <summary>主程序文件名 —— 部署根的判定标志。</summary>
        internal const string AppFileName = "DsInApex.exe";

        /// <summary>引擎文件名（上游原样二进制，品牌化不改）。</summary>
        internal const string EngineFileName = "ApexSenseBridge.exe";

        /// <summary>环境变量：直接指定引擎 exe 或所在目录（与主程序 Core.EngineLocator 同名同义）。</summary>
        internal const string EnginePathEnvVar = "DSINAPEX_ENGINE_PATH";

        /// <summary>向祖先回溯的最大层数（防御性上限）。</summary>
        private const int MaxAncestorDepth = 6;

        /// <summary>解决方案文件名 —— 开发期布局的判定标志。</summary>
        private const string SolutionFileName = "DsInApex.sln";

        /// <summary>解析部署根目录（含 <c>DsInApex.exe</c>）。</summary>
        internal static string ResolveDeployRoot(string configuredPath)
        {
            IReadOnlyList<string> ignored;
            return ResolveDeployRoot(configuredPath, out ignored);
        }

        /// <summary>解析部署根目录，并输出逐条探查轨迹（供设置页与诊断展示）。</summary>
        internal static string ResolveDeployRoot(string configuredPath, out IReadOnlyList<string> trace)
        {
            var steps = new List<string>();

            // ── 1. 用户显式指定 ──
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                string fromConfig = FindRootFromStart(configuredPath);
                steps.Add(string.Format("[配置路径] {0} → {1}",
                    configuredPath, string.IsNullOrEmpty(fromConfig) ? "未命中" : fromConfig));
                if (!string.IsNullOrEmpty(fromConfig))
                {
                    trace = steps;
                    return fromConfig;
                }
            }
            else
            {
                steps.Add("[配置路径] 未设置");
            }

            // ── 2. 正在运行的 DsInApex 进程 ──
            string running = FindRootFromRunningApp();
            steps.Add(string.Format("[运行中进程] {0} → {1}",
                AppFileName, string.IsNullOrEmpty(running) ? "未命中" : running));
            if (!string.IsNullOrEmpty(running))
            {
                trace = steps;
                return running;
            }

            // ── 3. 从插件目录向上回溯 ──
            string pluginDir = SafeAssemblyDirectory();
            string fromAncestors = WalkUp(pluginDir);
            steps.Add(string.Format("[插件目录上溯] {0} → {1}",
                pluginDir, string.IsNullOrEmpty(fromAncestors) ? "未命中" : fromAncestors));
            if (!string.IsNullOrEmpty(fromAncestors))
            {
                trace = steps;
                return fromAncestors;
            }

            // ── 4. 开发期布局（repo 根 + src\DsInApex.App\bin） ──
            string fromRepo = FindInRepositoryLayout(pluginDir);
            steps.Add(string.Format("[开发期布局] {0} → {1}",
                SolutionFileName, string.IsNullOrEmpty(fromRepo) ? "未命中" : fromRepo));
            if (!string.IsNullOrEmpty(fromRepo))
            {
                trace = steps;
                return fromRepo;
            }

            // ── 5. 常见部署位置 ──
            string fromCommon = FindInCommonLocations();
            steps.Add(string.Format("[常见位置] → {0}",
                string.IsNullOrEmpty(fromCommon) ? "未命中" : fromCommon));
            if (!string.IsNullOrEmpty(fromCommon))
            {
                trace = steps;
                return fromCommon;
            }

            steps.Add("结论：未找到 " + AppFileName);
            trace = steps;
            return string.Empty;
        }

        /// <summary>
        /// 解析桥接引擎可执行文件。优先级：环境变量 → <c>&lt;根&gt;\engine\</c> →
        /// <c>&lt;根&gt;</c> 同目录 → 在根目录下递归找。
        /// </summary>
        internal static string ResolveEngine(string configuredPath)
        {
            IReadOnlyList<string> ignored;
            return ResolveEngine(configuredPath, out ignored);
        }

        /// <summary>解析桥接引擎可执行文件，并输出逐条探查轨迹。</summary>
        internal static string ResolveEngine(string configuredPath, out IReadOnlyList<string> trace)
        {
            var steps = new List<string>();

            // ── 1. 环境变量覆盖（与 DIA 主程序 Core.EngineLocator 保持同一开关） ──
            string env = Environment.GetEnvironmentVariable(EnginePathEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
            {
                string candidate = env;
                if (Directory.Exists(candidate))
                {
                    candidate = Path.Combine(candidate, EngineFileName);
                }
                bool hit = IsEngine(candidate);
                steps.Add(string.Format("[环境变量] {0} → {1}", candidate, hit ? "命中" : "不存在"));
                if (hit)
                {
                    trace = steps;
                    return Path.GetFullPath(candidate);
                }
            }
            else
            {
                steps.Add("[环境变量] " + EnginePathEnvVar + " 未设置");
            }

            string root = ResolveDeployRoot(configuredPath);
            if (string.IsNullOrEmpty(root))
            {
                steps.Add("[部署根] 未解析出部署根，引擎无从谈起");
                trace = steps;
                return string.Empty;
            }

            // ── 2. 便携包布局：<根>\engine\ ──
            string installed = Path.Combine(root, "engine", EngineFileName);
            bool installedHit = IsEngine(installed);
            steps.Add(string.Format("[便携布局] {0} → {1}", installed, installedHit ? "命中" : "不存在"));
            if (installedHit)
            {
                trace = steps;
                return Path.GetFullPath(installed);
            }

            // ── 3. 与主程序同目录 ──
            string sideBySide = Path.Combine(root, EngineFileName);
            bool sideHit = IsEngine(sideBySide);
            steps.Add(string.Format("[同目录] {0} → {1}", sideBySide, sideHit ? "命中" : "不存在"));
            if (sideHit)
            {
                trace = steps;
                return Path.GetFullPath(sideBySide);
            }

            // ── 4. 兜底：在部署根下递归找（覆盖 vendor\portable-0.6.x 之类的开发期布局） ──
            string loose = FindFirst(root, EngineFileName);
            steps.Add(string.Format("[递归查找] {0} → {1}",
                root, string.IsNullOrEmpty(loose) ? "未命中" : loose));
            trace = steps;
            return string.IsNullOrEmpty(loose) ? string.Empty : Path.GetFullPath(loose);
        }

        /// <summary>该路径是否为 Ds in Apex 主程序。</summary>
        internal static bool IsDeployedApp(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetFileName(path), AppFileName, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }

        /// <summary>该路径是否为桥接引擎可执行文件。</summary>
        internal static bool IsEngine(string path)
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetFileName(path), EngineFileName, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }

        // ────────────────────────── 内部实现 ──────────────────────────

        private static string FindRootFromStart(string start)
        {
            try
            {
                if (IsDeployedApp(start))
                {
                    return Path.GetDirectoryName(Path.GetFullPath(start));
                }

                if (File.Exists(start))
                {
                    return WalkUp(Path.GetDirectoryName(Path.GetFullPath(start)));
                }

                if (Directory.Exists(start))
                {
                    return WalkUp(Path.GetFullPath(start));
                }

                // 路径不存在：按扩展名猜是文件还是目录，退回其父目录再上溯，
                // 这样用户填了一个过期路径也还有救。
                string guess = Path.HasExtension(start)
                    ? Path.GetDirectoryName(start)
                    : start;
                return WalkUp(guess);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string WalkUp(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return string.Empty;
            }

            DirectoryInfo current = TryDirectory(directory);
            for (int depth = 0; current != null && depth <= MaxAncestorDepth; depth++)
            {
                string candidate = Path.Combine(current.FullName, AppFileName);
                if (IsDeployedApp(candidate))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
            return string.Empty;
        }

        private static string FindRootFromRunningApp()
        {
            try
            {
                string processName = Path.GetFileNameWithoutExtension(AppFileName);
                foreach (Process process in Process.GetProcessesByName(processName))
                {
                    try
                    {
                        string module = process.MainModule == null
                            ? null
                            : process.MainModule.FileName;
                        if (IsDeployedApp(module))
                        {
                            return Path.GetDirectoryName(module);
                        }
                    }
                    catch
                    {
                        // 位数不匹配 / 权限不足读不到 MainModule —— 换下一个进程试
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
                // 进程枚举失败不致命，继续走文件系统探测
            }
            return string.Empty;
        }

        private static string FindInRepositoryLayout(string pluginDir)
        {
            if (string.IsNullOrWhiteSpace(pluginDir))
            {
                return string.Empty;
            }

            DirectoryInfo current = TryDirectory(pluginDir);
            for (int depth = 0; current != null && depth <= MaxAncestorDepth; depth++)
            {
                string solution = Path.Combine(current.FullName, SolutionFileName);
                if (File.Exists(solution))
                {
                    string appBin = Path.Combine(current.FullName, "src", "DsInApex.App", "bin");
                    if (Directory.Exists(appBin))
                    {
                        // 先挑 Release，再挑 Debug；同配置取路径最短的那个（最外层）
                        string hit = FindPreferred(appBin);
                        if (!string.IsNullOrEmpty(hit))
                        {
                            return Path.GetDirectoryName(hit);
                        }
                    }
                    return string.Empty;
                }
                current = current.Parent;
            }
            return string.Empty;
        }

        private static string FindPreferred(string root)
        {
            var candidates = new List<string>();
            try
            {
                candidates.AddRange(Directory.EnumerateFiles(root, AppFileName, SearchOption.AllDirectories));
            }
            catch
            {
                return string.Empty;
            }

            return candidates
                .OrderByDescending(p => p.IndexOf(@"\Release\", StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenBy(p => p.Length)
                .FirstOrDefault();
        }

        private static string FindInCommonLocations()
        {
            foreach (string directory in EnumerateCommonDirectories())
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(directory))
                    {
                        continue;
                    }
                    string candidate = Path.Combine(directory, AppFileName);
                    if (IsDeployedApp(candidate))
                    {
                        return Path.GetFullPath(directory);
                    }
                }
                catch
                {
                    // 单个候选目录不可用时继续
                }
            }
            return string.Empty;
        }

        private static IEnumerable<string> EnumerateCommonDirectories()
        {
            var roots = new List<string>();

            AddIfNotEmpty(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            AddIfNotEmpty(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            AddIfNotEmpty(roots, Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
            AddIfNotEmpty(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

            foreach (string root in roots)
            {
                yield return Path.Combine(root, "DsInApex");
            }

            foreach (DriveInfo drive in SafeDrives())
            {
                foreach (string relative in new[] { "DsInApex", @"Games\DsInApex", @"Portable\DsInApex", @"Tools\DsInApex" })
                {
                    yield return Path.Combine(drive.RootDirectory.FullName, relative);
                }
            }
        }

        private static IEnumerable<DriveInfo> SafeDrives()
        {
            DriveInfo[] drives;
            try
            {
                drives = DriveInfo.GetDrives();
            }
            catch
            {
                return new DriveInfo[0];
            }

            return drives.Where(d =>
            {
                try
                {
                    return d.DriveType == DriveType.Fixed && d.IsReady;
                }
                catch
                {
                    return false;
                }
            });
        }

        private static void AddIfNotEmpty(ICollection<string> target, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                target.Add(value);
            }
        }

        private static DirectoryInfo TryDirectory(string path)
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

        private static string FindFirst(string root, string fileName)
        {
            try
            {
                return Directory
                    .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string SafeAssemblyDirectory()
        {
            try
            {
                string location = typeof(InstallLocator).Assembly.Location;
                return string.IsNullOrWhiteSpace(location) ? string.Empty : Path.GetDirectoryName(location);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
