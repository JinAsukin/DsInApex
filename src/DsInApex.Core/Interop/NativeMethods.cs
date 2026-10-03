using System.Runtime.InteropServices;
using System.Text;

namespace DsInApex.Core.Interop;

/// <summary>
/// 进程/窗口相关的 Win32 P/Invoke。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Common/NativeMethods.cs</c>（112 行），实现照搬。
/// 供 <c>ProcessMonitorService</c> 使用：枚举进程、取前景窗口、PID → 可执行文件路径。
/// </para>
///
/// <para>
/// <b>为什么不用 .NET 原生 API：</b>提升权限运行的游戏（如反作弊保护的进程）会拒绝
/// <c>Process.MainModule</c> 访问，而 <c>QueryFullProcessImageName</c> 配合
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c> 在多数场景下仍可取到路径 —— 这是上游刻意选择。
/// </para>
/// </summary>
internal static class NativeMethods
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern bool QueryFullProcessImageName(IntPtr hProcess, uint flags, StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumProcesses(
        [Out] uint[] processIds,
        uint bufferSize,
        out uint bytesReturned);

    /// <summary>枚举当前所有进程 PID（容量自适应，上限 32768）。</summary>
    public static uint[] GetProcessIds()
    {
        int capacity = 512;
        while (capacity <= 32768)
        {
            var processIds = new uint[capacity];
            if (!EnumProcesses(
                processIds,
                (uint)(processIds.Length * sizeof(uint)),
                out uint bytesReturned))
            {
                return [];
            }

            int count = (int)(bytesReturned / sizeof(uint));
            if (count < processIds.Length)
            {
                var result = new uint[count];
                Array.Copy(processIds, result, count);
                return result;
            }
            capacity *= 2;
        }
        return [];
    }

    /// <summary>取指定窗口所属进程的可执行文件路径。</summary>
    public static string? GetActiveProcessPath(IntPtr hwnd, out uint processId)
    {
        processId = 0;
        if (hwnd == IntPtr.Zero) return null;

        GetWindowThreadProcessId(hwnd, out processId);
        if (processId == 0) return null;

        return GetProcessPath(processId);
    }

    /// <summary>按 PID 取可执行文件路径（对提升权限的进程亦尽力而为）。</summary>
    public static string? GetProcessPath(uint processId)
    {
        if (processId == 0) return null;

        IntPtr hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (hProcess == IntPtr.Zero) return null;

        try
        {
            var sb = new StringBuilder(32768);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageName(hProcess, 0, sb, ref size))
            {
                return sb.ToString();
            }
        }
        finally
        {
            CloseHandle(hProcess);
        }
        return null;
    }

    /// <summary>取窗口标题。</summary>
    public static string GetActiveWindowTitle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return string.Empty;
        int length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        var sb = new StringBuilder(length + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
