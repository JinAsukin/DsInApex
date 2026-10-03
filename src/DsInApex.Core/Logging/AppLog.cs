using System.Globalization;
using System.Text;

namespace DsInApex.Core.Logging;

/// <summary>
/// 文件日志。
///
/// 迁移自上游 <c>ApexSenseBridgeTray/Common/AppLog.cs</c>，行为保持一致：
/// - 时间戳格式 "s"（ISO 8601 可排序）
/// - UTF-8 无 BOM
/// - 追加写入，写入加锁
/// - **任何异常都吞掉** —— 日志绝不能拖垮进程监控或界面
/// </summary>
public static class AppLog
{
    private static readonly object SyncRoot = new();

    /// <summary>
    /// 默认日志目录。
    ///
    /// ⚠️ 刻意沿用上游的 <c>ApexSenseBridge</c> 目录名，不改为 <c>DsInApex</c>，理由有三：
    /// 1. **引擎（C++，未改动）写的就是这个目录**，改名会导致日志分散两处；
    /// 2. P8 验收基准取自 <c>%LOCALAPPDATA%\ApexSenseBridge\logs\tray_bridge.log</c>；
    /// 3. 用户从旧版升级时日志连续，便于排查。
    ///
    /// 品牌化的目录迁移留到 P7 统一处理。
    /// </summary>
    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ApexSenseBridge",
            "logs");

    /// <summary>写入默认目录。</summary>
    public static void WriteLine(string fileName, string message)
        => WriteLine(DefaultDirectory, fileName, message);

    /// <summary>写入指定目录。</summary>
    public static void WriteLine(string directory, string fileName, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
            {
                return;
            }

            // 防目录穿越：只取文件名部分
            string safeFileName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(safeFileName)) return;

            lock (SyncRoot)
            {
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.AppendAllText(
                    Path.Combine(directory, safeFileName),
                    DateTime.Now.ToString("s", CultureInfo.InvariantCulture) + " " +
                    (message ?? string.Empty) + "\r\n",
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
            // 日志失败绝不能影响主流程
        }
    }

    // ── 便捷方法（上游没有，P1 新增，便于区分级别） ──

    public static void Info(string fileName, string message) => WriteLine(fileName, "[INFO] " + message);

    public static void Warn(string fileName, string message) => WriteLine(fileName, "[WARN] " + message);

    public static void Error(string fileName, string message) => WriteLine(fileName, "[ERROR] " + message);

    /// <summary>把异常压成单行，避免多行堆栈污染日志。</summary>
    public static string Describe(Exception ex)
        => ex is null ? "(null)" : $"{ex.GetType().Name}: {ex.Message}";
}
