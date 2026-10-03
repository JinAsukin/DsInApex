namespace DsInApex.Core.Models;

/// <summary>
/// 引擎 <c>list</c> 命令解析出的单个飞智 HID 接口信息。
/// 字段与上游 <c>printDevice()</c>（src/cli/CommandSupport.cpp:172）的输出逐项对应。
/// </summary>
public sealed record FlydigiDeviceInfo(
    int Index,
    string Product,
    string VendorId,
    string ProductId,
    string UsagePage,
    string Usage,
    int InputReportLength,
    int OutputReportLength)
{
    public override string ToString()
        => $"[{Index}] {Product}  {VendorId}:{ProductId}  "
         + $"usage {UsagePage}/{Usage}  reports in={InputReportLength} out={OutputReportLength}";
}
