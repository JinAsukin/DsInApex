using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.Win32;

namespace DsInApex.Core.Services;

/// <summary>
/// 便携部署布局探查（P6）。
///
/// <para>
/// DIA 的交付形态确定为 <b>portable 绿色版</b>：解压即用、不写安装项、
/// 卸载即删除目录。这个决定带来一条必须自己兜住的责任 ——
/// <b>没有任何安装器会替我们检查部署完整性</b>。
/// 以前 Inno 脚本里那些 <c>Check: ShouldRemoveHidHide</c>、文件存在性校验、
/// 许可证随包投放，现在统统得由程序自己回答「我在哪、我旁边缺了什么」。
/// </para>
///
/// <para>
/// 本服务<b>全程只读</b>：不建目录、不改注册表、不写文件（可写性探测用的是
/// 「创建后立刻删除的空探针文件」，且在只读目录上会安静失败）。
/// </para>
/// </summary>
public static class PortableLayoutService
{
    private const string LogFileName = "dsinapex_portable.log";

    /// <summary>驱动前置安装器目录名（与 csproj 的 <c>Prerequisites\*</c> 复制规则一致）。</summary>
    public const string PrerequisitesFolderName = "Prerequisites";

    /// <summary>随包文件的候选名（大小写与扩展名都做容错查找）。</summary>
    private static readonly string[] LicenseNames = ["LICENSE", "LICENSE.txt", "LICENSE.md"];

    private static readonly string[] ThirdPartyNoticeNames =
        ["THIRD_PARTY_NOTICES.md", "THIRD_PARTY_NOTICES.txt", "THIRDPARTYNOTICES.md"];

    private static readonly string[] SourceOfferNames =
        ["SOURCE_OFFER.md", "SOURCE.md", "SOURCE_CODE.md"];

    /// <summary>旧官方版的安装注册表键（用于识别「这是安装版而不是便携版」）。</summary>
    private static readonly string[] InstallRegistryPaths =
        [@"SOFTWARE\Ds in Apex", @"SOFTWARE\ApexSenseBridge"];

    /// <summary>采集当前部署布局报告。</summary>
    public static PortableLayoutReport Collect()
    {
        string appDirectory = AppContext.BaseDirectory;

        string executable = Environment.ProcessPath
                            ?? (string.IsNullOrWhiteSpace(appDirectory)
                                ? "DsInApex.exe"
                                : Path.Combine(appDirectory, "DsInApex.exe"));

        string enginePath;
        try
        {
            enginePath = EngineLocator.ResolveEngine();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"引擎定位异常：{AppLog.Describe(ex)}");
            enginePath = string.Empty;
        }

        string prerequisitesDirectory = Path.Combine(appDirectory, PrerequisitesFolderName);
        int prerequisiteCount = CountFiles(prerequisitesDirectory);

        string dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ApexSenseBridge");
        string settingsPath = TraySettings.FilePath;

        string licensePath = FindFirstExisting(appDirectory, LicenseNames) ?? Path.Combine(appDirectory, "LICENSE");

        var notes = new List<string>
        {
            $"应用目录    : {Trim(appDirectory)}",
            $"可执行文件  : {executable}",
            $"引擎        : {(string.IsNullOrWhiteSpace(enginePath) ? "未找到（桥接功能不可用）" : enginePath)}",
            $"前置安装器  : {Trim(prerequisitesDirectory)} → {prerequisiteCount} 个文件",
            $"用户数据    : {Trim(dataDirectory)}（固定位置，与官方版共用）",
            $"许可证      : {(File.Exists(licensePath) ? licensePath : "缺失（GPL 合规要求随包分发）")}",
        };

        if (prerequisiteCount == 0)
        {
            notes.Add("提示：未发现驱动前置安装器 → 驱动管理页的「一键安装」将不可用（检测仍正常）。");
        }

        return new PortableLayoutReport
        {
            AppDirectory = appDirectory,
            ExecutablePath = executable,
            EnginePath = enginePath,
            EnginePresent = !string.IsNullOrWhiteSpace(enginePath) && File.Exists(enginePath),
            PrerequisitesDirectory = prerequisitesDirectory,
            PrerequisiteFileCount = prerequisiteCount,
            DataDirectory = dataDirectory,
            SettingsPath = settingsPath,
            SettingsExists = File.Exists(settingsPath),
            LicensePath = licensePath,
            LicensePresent = File.Exists(licensePath),
            ThirdPartyNoticesPresent = FindFirstExisting(appDirectory, ThirdPartyNoticeNames) is not null,
            SourceOfferPresent = FindFirstExisting(appDirectory, SourceOfferNames) is not null,
            AppDirectoryWritable = ProbeWritable(appDirectory),
            LooksInstalled = HasInstallTraces(appDirectory),
            Notes = notes,
        };
    }

    /// <summary>把一个 exe 路径的规范化形式取出来，用于与自启项、运行位置比对。</summary>
    public static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string Trim(string path) => NormalizePath(path);

    private static int CountFiles(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory).Count()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string? FindFirstExisting(string directory, IReadOnlyList<string> names)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (string name in names)
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 大小写容错：Linux 式命名习惯偶尔混进来，别让一个大小写差异判成"缺文件"
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory))
            {
                string fileName = Path.GetFileName(file);
                foreach (string name in names)
                {
                    if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return file;
                    }
                }
            }
        }
        catch
        {
            // 目录不可枚举时按「未找到」处理
        }

        return null;
    }

    /// <summary>
    /// 应用目录可写性探测：建一个极小的探针文件再删掉。
    /// 位于 <c>%ProgramFiles%</c> 或只读介质上时返回 false —— 用户会看到需要提权的提示。
    /// </summary>
    private static bool ProbeWritable(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        string probe = Path.Combine(directory, $".dsinapex-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
                // 建完即随句柄关闭删除
            }
            return true;
        }
        catch
        {
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch
            {
                // 删不掉也无所谓，探针文件名带 GUID，不会撞上真实文件
            }
            return false;
        }
    }

    /// <summary>是否存在「安装式」部署的痕迹（安装注册表项或 Inno 卸载器）。</summary>
    private static bool HasInstallTraces(string appDirectory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(appDirectory))
            {
                if (File.Exists(Path.Combine(appDirectory, "unins000.exe")) ||
                    File.Exists(Path.Combine(appDirectory, "install-manifest.json")))
                {
                    return true;
                }
            }
        }
        catch
        {
            // 忽略
        }

        foreach (string path in InstallRegistryPaths)
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using RegistryKey? key = baseKey.OpenSubKey(path, writable: false);
                    if (key is not null)
                    {
                        return true;
                    }
                }
                catch
                {
                    // 某个视图读不到就试下一个
                }
            }
        }

        return false;
    }
}
