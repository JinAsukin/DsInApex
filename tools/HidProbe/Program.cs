// hidprobe - read-only-first HID probe for Flydigi APEX research.
//
// Why this exists: ApexSenseBridge exposes no raw HID or feature-report command,
// so the vendor interfaces (MI_02 = FORCEADAPT, MI_03 = unknown, 64-byte feature)
// have never been probed directly. This tool talks to them at the Win32 HID level.
//
// Safety rules baked in:
//   * enumeration / caps / strings / read / feature-get are strictly read-only
//   * feature-set and output require an explicit --yes (never retried, never looped)
//   * a live bridge session owns MI_02; probe opens with full share and never retries

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace HidProbe;

internal static class Native
{
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagOverlapped = 0x40000000;

    internal const uint DigcfPresent = 0x00000002;
    internal const uint DigcfDeviceInterface = 0x00000010;
    internal const int ErrorIoPending = 997;
    internal const uint WaitObject0 = 0x00000000;
    internal const uint WaitTimeout = 0x00000102;
    internal const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [DllImport("hid.dll")] internal static extern void HidD_GetHidGuid(out Guid hidGuid);
    [DllImport("hid.dll")] internal static extern bool HidD_GetAttributes(IntPtr handle, ref HiddAttributes attributes);
    [DllImport("hid.dll")] internal static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsed);
    [DllImport("hid.dll")] internal static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] internal static extern int HidP_GetCaps(IntPtr preparsed, out HidpCaps caps);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] internal static extern bool HidD_GetManufacturerString(IntPtr handle, IntPtr buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] internal static extern bool HidD_GetProductString(IntPtr handle, IntPtr buffer, int length);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] internal static extern bool HidD_GetSerialNumberString(IntPtr handle, IntPtr buffer, int length);
    [DllImport("hid.dll")] internal static extern bool HidD_GetFeature(IntPtr handle, byte[] buffer, int length);
    [DllImport("hid.dll")] internal static extern bool HidD_SetFeature(IntPtr handle, byte[] buffer, int length);
    [DllImport("hid.dll")] internal static extern bool HidD_GetInputReport(IntPtr handle, byte[] buffer, int length);
    [DllImport("hid.dll")] internal static extern bool HidD_SetOutputReport(IntPtr handle, byte[] buffer, int length);
    [DllImport("hid.dll")] internal static extern bool HidD_GetPhysicalDescriptor(IntPtr handle, IntPtr buffer, int length);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailSize, out uint requiredSize, IntPtr deviceInfoData2);
    [DllImport("setupapi.dll", SetLastError = true)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool ReadFile(IntPtr handle, byte[] buffer, uint count, IntPtr bytesRead, ref NativeOverlapped overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool WriteFile(IntPtr handle, byte[] buffer, uint count, IntPtr bytesWritten, ref NativeOverlapped overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetOverlappedResult(IntPtr handle, ref NativeOverlapped overlapped, out uint transferred, bool wait);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CancelIo(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr handle);
}

internal sealed class HidEntry
{
    public int Index;
    public string Path = "";
    public ushort VendorId;
    public ushort ProductId;
    public ushort Version;
    public ushort UsagePage;
    public ushort Usage;
    public int InputLength;
    public int OutputLength;
    public int FeatureLength;
    public string Manufacturer = "";
    public string Product = "";
    public string Serial = "";
    public string InterfaceNumber = "";
}

internal static class Program
{
    private static readonly ushort[] FlydigiVids = { 0x04B4, 0x37D7 };

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage();
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "list" => CommandList(args),
                "read" => CommandRead(args),
                "feature-get" => CommandFeatureGet(args),
                "feature-scan" => CommandFeatureScan(args),
                "feature-set" => CommandFeatureSet(args),
                "output" => CommandOutput(args),
                _ => Fail($"unknown command: {args[0]}"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("hidprobe - HID interface probe for Flydigi APEX research");
        Console.WriteLine();
        Console.WriteLine("  list [--all] [--json]");
        Console.WriteLine("      Enumerate HID interfaces. Default filters to Flydigi VIDs (04B4 / 37D7).");
        Console.WriteLine();
        Console.WriteLine("  read <index|--path P> [--count N] [--timeout MS] [--raw]");
        Console.WriteLine("      Stream input reports from an interface. Read-only.");
        Console.WriteLine();
        Console.WriteLine("  feature-get <index|--path P> [--id NN] [--len N]");
        Console.WriteLine("      Issue HidD_GetFeature. Read-only in practice, but it does poke the device.");
        Console.WriteLine();
        Console.WriteLine("  feature-set <index|--path P> --hex <bytes> --yes");
        Console.WriteLine("      Issue HidD_SetFeature. Requires --yes. Never retried.");
        Console.WriteLine();
        Console.WriteLine("  output <index|--path P> --hex <bytes> --yes");
        Console.WriteLine("      Send one output report. Requires --yes. Never retried.");
        Console.WriteLine();
        Console.WriteLine("Hex bytes accept: '035AA5A0', '03 5A A5 A0', '03:5A:A5:A0'");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine("error: " + message);
        Console.Error.WriteLine("run 'hidprobe --help'");
        return 1;
    }

    // ---- enumeration -------------------------------------------------------

    private static List<HidEntry> Enumerate()
    {
        var results = new List<HidEntry>();
        Native.HidD_GetHidGuid(out var hidGuid);
        var set = Native.SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            Native.DigcfPresent | Native.DigcfDeviceInterface);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
        {
            throw new InvalidOperationException("SetupDiGetClassDevs failed: " + Marshal.GetLastWin32Error());
        }

        try
        {
            for (uint member = 0; member < 512; member++)
            {
                var iface = new Native.SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<Native.SpDeviceInterfaceData>(),
                };
                if (!Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, member, ref iface))
                {
                    break;
                }

                Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out var required, IntPtr.Zero);
                if (required == 0) continue;

                var detail = Marshal.AllocHGlobal((int)required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, required, out _, IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length == 0) continue;
                    results.Add(Describe(path, results.Count));
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }

        return results;
    }

    private static HidEntry Describe(string path, int index)
    {
        var entry = new HidEntry { Index = index, Path = path };
        var handle = Open(path, out var error);
        if (handle == IntPtr.Zero)
        {
            entry.Product = "<open failed: " + error + ">";
            return entry;
        }

        try
        {
            var attributes = new Native.HiddAttributes { Size = Marshal.SizeOf<Native.HiddAttributes>() };
            if (Native.HidD_GetAttributes(handle, ref attributes))
            {
                entry.VendorId = attributes.VendorId;
                entry.ProductId = attributes.ProductId;
                entry.Version = attributes.VersionNumber;
            }

            if (Native.HidD_GetPreparsedData(handle, out var preparsed) && preparsed != IntPtr.Zero)
            {
                try
                {
                    if (Native.HidP_GetCaps(preparsed, out var caps) >= 0)
                    {
                        entry.UsagePage = caps.UsagePage;
                        entry.Usage = caps.Usage;
                        entry.InputLength = caps.InputReportByteLength;
                        entry.OutputLength = caps.OutputReportByteLength;
                        entry.FeatureLength = caps.FeatureReportByteLength;
                    }
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                }
            }

            var buffer = Marshal.AllocHGlobal(512);
            try
            {
                if (Native.HidD_GetManufacturerString(handle, buffer, 512))
                    entry.Manufacturer = Marshal.PtrToStringUni(buffer) ?? "";
                if (Native.HidD_GetProductString(handle, buffer, 512))
                    entry.Product = Marshal.PtrToStringUni(buffer) ?? "";
                if (Native.HidD_GetSerialNumberString(handle, buffer, 512))
                    entry.Serial = Marshal.PtrToStringUni(buffer) ?? "";
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            Native.CloseHandle(handle);
        }

        var match = System.Text.RegularExpressions.Regex.Match(
            path, "mi_(?<n>[0-9a-f]{2})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success) entry.InterfaceNumber = "MI_" + match.Groups["n"].Value.ToUpperInvariant();
        return entry;
    }

    private static IntPtr Open(string path, out string error)
    {
        error = "";
        var handle = Native.CreateFile(path,
            Native.GenericRead | Native.GenericWrite,
            Native.FileShareRead | Native.FileShareWrite,
            IntPtr.Zero, Native.OpenExisting, Native.FileFlagOverlapped, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            error = "CreateFile failed: " + Marshal.GetLastWin32Error();
            return IntPtr.Zero;
        }
        return handle;
    }

    // ---- commands ----------------------------------------------------------

    private static int CommandList(string[] args)
    {
        var all = args.Contains("--all");
        var json = args.Contains("--json");
        var entries = Enumerate().Where(e => all || FlydigiVids.Contains(e.VendorId)).ToList();

        if (json)
        {
            Console.WriteLine("{");
            Console.WriteLine("  \"count\": " + entries.Count + ",");
            Console.WriteLine("  \"devices\": [");
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var comma = i == entries.Count - 1 ? "" : ",";
                Console.WriteLine("    {\"index\":" + e.Index
                    + ",\"interface\":\"" + e.InterfaceNumber + "\""
                    + ",\"vid\":\"0x" + e.VendorId.ToString("X4") + "\""
                    + ",\"pid\":\"0x" + e.ProductId.ToString("X4") + "\""
                    + ",\"rev\":\"0x" + e.Version.ToString("X4") + "\""
                    + ",\"usage_page\":\"0x" + e.UsagePage.ToString("X4") + "\""
                    + ",\"usage\":\"0x" + e.Usage.ToString("X4") + "\""
                    + ",\"in\":" + e.InputLength
                    + ",\"out\":" + e.OutputLength
                    + ",\"feature\":" + e.FeatureLength
                    + ",\"product\":\"" + Json(e.Product) + "\""
                    + ",\"path\":\"" + Json(e.Path) + "\"}" + comma);
            }
            Console.WriteLine("  ]");
            Console.WriteLine("}");
            return 0;
        }

        Console.WriteLine("{0,-5} {1,-7} {2,-8} {3,-8} {4,-8} {5,-6} {6,-6} {7,-7} {8}",
            "idx", "iface", "vid:pid", "rev", "usage pg", "in", "out", "feat", "product");
        foreach (var e in entries)
        {
            Console.WriteLine("{0,-5} {1,-7} {2,-8} {3,-8} {4,-8} {5,-6} {6,-6} {7,-7} {8}",
                e.Index, e.InterfaceNumber,
                e.VendorId.ToString("X4") + ":" + e.ProductId.ToString("X4"),
                "0x" + e.Version.ToString("X4"),
                "0x" + e.UsagePage.ToString("X4"),
                e.InputLength, e.OutputLength, e.FeatureLength,
                e.Product);
            Console.WriteLine("      path " + e.Path);
        }
        if (entries.Count == 0)
        {
            Console.WriteLine("(no Flydigi interfaces found; pass --all to list every HID device)");
        }
        return 0;
    }

    private static int CommandRead(string[] args)
    {
        if (!TryResolve(args, out var path, out var error)) return Fail(error);
        var count = ArgInt(args, "--count", 5);
        var timeout = ArgInt(args, "--timeout", 2000);

        var handle = Open(path, out error);
        if (handle == IntPtr.Zero) return Fail(error);
        try
        {
            var length = 64;
            if (Native.HidD_GetPreparsedData(handle, out var preparsed) && preparsed != IntPtr.Zero)
            {
                try
                {
                    if (Native.HidP_GetCaps(preparsed, out var caps) >= 0 && caps.InputReportByteLength > 0)
                        length = caps.InputReportByteLength;
                }
                finally { Native.HidD_FreePreparsedData(preparsed); }
            }

            Console.WriteLine("reading up to " + count + " input report(s), " + length
                + " bytes each, " + timeout + " ms timeout each");
            for (var i = 0; i < count; i++)
            {
                var buffer = new byte[length];
                if (!TryOverlappedRead(handle, buffer, timeout, out var read))
                {
                    Console.WriteLine("[" + i + "] timeout - no report arrived");
                    break;
                }
                Console.WriteLine("[" + i + "] " + read + " bytes  " + Hex(buffer, read));
            }
            return 0;
        }
        finally { Native.CloseHandle(handle); }
    }

    private static int CommandFeatureGet(string[] args)
    {
        if (!TryResolve(args, out var path, out var error)) return Fail(error);
        var reportId = ArgInt(args, "--id", 0);
        var length = ArgInt(args, "--len", 0);

        var handle = Open(path, out error);
        if (handle == IntPtr.Zero) return Fail(error);
        try
        {
            if (length <= 0)
            {
                if (Native.HidD_GetPreparsedData(handle, out var preparsed) && preparsed != IntPtr.Zero)
                {
                    try
                    {
                        if (Native.HidP_GetCaps(preparsed, out var caps) >= 0)
                            length = caps.FeatureReportByteLength;
                    }
                    finally { Native.HidD_FreePreparsedData(preparsed); }
                }
            }
            if (length <= 0)
            {
                Console.WriteLine("this interface declares no feature report (length 0)");
                return 0;
            }

            var buffer = new byte[length];
            buffer[0] = (byte)reportId;
            Console.WriteLine("HidD_GetFeature len=" + length + " reportId=0x" + reportId.ToString("X2"));
            if (!Native.HidD_GetFeature(handle, buffer, buffer.Length))
            {
                Console.Error.WriteLine("HidD_GetFeature failed: " + Marshal.GetLastWin32Error());
                return 1;
            }
            Console.WriteLine("  " + Hex(buffer, buffer.Length));
            return 0;
        }
        finally { Native.CloseHandle(handle); }
    }

    private static int CommandFeatureScan(string[] args)
    {
        if (!TryResolve(args, out var path, out var error)) return Fail(error);
        var length = ArgInt(args, "--len", 64);
        var ids = args.Skip(1).Where(a => a.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            .Select(a => int.Parse(a.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture))
            .ToList();
        if (ids.Count == 0) ids = Enumerable.Range(0, 256).ToList();

        var handle = Open(path, out error);
        if (handle == IntPtr.Zero) return Fail(error);
        try
        {
            foreach (var id in ids)
            {
                if (id < 0 || id > 255) continue;
                var buffer = new byte[length];
                buffer[0] = (byte)id;
                if (!Native.HidD_GetFeature(handle, buffer, buffer.Length)) continue;
                if (buffer.Skip(1).All(b => b == 0)) continue;
                Console.WriteLine("0x" + id.ToString("X2") + "  " + Hex(buffer, buffer.Length));
            }
            return 0;
        }
        finally { Native.CloseHandle(handle); }
    }

    private static int CommandFeatureSet(string[] args)
    {
        if (!args.Contains("--yes")) return Fail("feature-set writes to the device; add --yes to confirm");
        if (!TryResolve(args, out var path, out var error)) return Fail(error);
        if (!TryParseHex(args, out var bytes, out error)) return Fail(error);

        var handle = Open(path, out error);
        if (handle == IntPtr.Zero) return Fail(error);
        try
        {
            Console.WriteLine("HidD_SetFeature " + bytes.Length + " bytes: " + Hex(bytes, bytes.Length));
            if (!Native.HidD_SetFeature(handle, bytes, bytes.Length))
            {
                Console.Error.WriteLine("HidD_SetFeature failed: " + Marshal.GetLastWin32Error());
                return 1;
            }
            Console.WriteLine("ok (sent once, not retried)");
            return 0;
        }
        finally { Native.CloseHandle(handle); }
    }

    private static int CommandOutput(string[] args)
    {
        if (!args.Contains("--yes")) return Fail("output writes to the device; add --yes to confirm");
        if (!TryResolve(args, out var path, out var error)) return Fail(error);
        if (!TryParseHex(args, out var bytes, out error)) return Fail(error);

        var handle = Open(path, out error);
        if (handle == IntPtr.Zero) return Fail(error);
        try
        {
            Console.WriteLine("output report " + bytes.Length + " bytes: " + Hex(bytes, bytes.Length));
            if (!Native.HidD_SetOutputReport(handle, bytes, bytes.Length))
            {
                Console.Error.WriteLine("HidD_SetOutputReport failed: " + Marshal.GetLastWin32Error());
                return 1;
            }
            Console.WriteLine("ok (sent once, not retried)");
            return 0;
        }
        finally { Native.CloseHandle(handle); }
    }

    // ---- helpers -----------------------------------------------------------

    private static bool TryOverlappedRead(IntPtr handle, byte[] buffer, int timeout, out int read)
    {
        read = 0;
        using var completed = new ManualResetEvent(false);
        var overlapped = new NativeOverlapped
        {
            EventHandle = completed.SafeWaitHandle.DangerousGetHandle(),
        };

        var ok = Native.ReadFile(handle, buffer, (uint)buffer.Length, IntPtr.Zero, ref overlapped);
        if (!ok)
        {
            var code = Marshal.GetLastWin32Error();
            if (code != Native.ErrorIoPending) return false;
        }

        var waited = Native.WaitForSingleObject(completed.SafeWaitHandle.DangerousGetHandle(),
            timeout <= 0 ? Native.Infinite : (uint)timeout);
        if (waited == Native.WaitTimeout)
        {
            Native.CancelIo(handle);
            Native.GetOverlappedResult(handle, ref overlapped, out _, true);
            return false;
        }

        Native.GetOverlappedResult(handle, ref overlapped, out var transferred, true);
        read = (int)transferred;
        return true;
    }

    private static bool TryResolve(string[] args, out string path, out string error)
    {
        path = "";
        error = "";
        var index = -1;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--path") path = args[i + 1];
            if (args[i] == "--index") int.TryParse(args[i + 1], out index);
        }
        if (args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            int.TryParse(args[1], out index);
        }

        if (path.Length > 0) return true;
        if (index >= 0)
        {
            var entry = Enumerate().FirstOrDefault(e => e.Index == index);
            if (entry is null)
            {
                error = "index " + index + " not found; run 'hidprobe list' first";
                return false;
            }
            path = entry.Path;
            return true;
        }

        error = "specify an index or --path <device path>";
        return false;
    }

    private static bool TryParseHex(string[] args, out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        var raw = "";
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i] == "--hex") raw = args[i + 1];
        }
        if (raw.Length == 0)
        {
            error = "--hex <bytes> is required";
            return false;
        }

        var cleaned = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != ':' && c != '-').ToArray());
        if (cleaned.Length == 0 || cleaned.Length % 2 != 0)
        {
            error = "--hex must contain an even number of hex digits";
            return false;
        }

        var result = new byte[cleaned.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(cleaned.Substring(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out result[i]))
            {
                error = "bad hex at offset " + (i * 2) + ": " + cleaned.Substring(i * 2, 2);
                return false;
            }
        }
        bytes = result;
        return true;
    }

    private static int ArgInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name &&
                int.TryParse(args[i + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }
        return fallback;
    }

    private static string Hex(byte[] data, int length)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < Math.Min(length, data.Length); i++)
        {
            builder.Append(data[i].ToString("X2", CultureInfo.InvariantCulture));
            builder.Append(' ');
        }
        return builder.ToString().TrimEnd();
    }

    private static string Json(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
