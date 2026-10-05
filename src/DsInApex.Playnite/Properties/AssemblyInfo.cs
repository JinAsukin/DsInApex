using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Ds in Apex · Playnite 插件（衍生自 ApexSenseBridge，GPL-3.0-or-later，已修改）

[assembly: AssemblyTitle("Ds in Apex (Playnite)")]
[assembly: AssemblyDescription("Playnite lifecycle integration for Ds in Apex")]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyCompany("Ds in Apex contributors")]
[assembly: AssemblyProduct("Ds in Apex")]
[assembly: AssemblyCopyright("Copyright © 2026 Ds in Apex contributors")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
[assembly: ComVisible(false)]

// ⚠️ 这个 GUID 必须与 extension.yaml 的 Id 后缀、以及 DsInApexPlugin.Id 三处一致。
// 沿用上游 GUID：让升级安装的插件身份稳定（配置目录名随之变化见 docs/16）。
[assembly: Guid("e41b1737-6753-4b59-bc65-4fdd6a7df7f4")]

// ⚠️ 与 extension.yaml 的 Version 及仓库 version.json 必须同源 ——
//    build\verify-p9.ps1 的 S2 会强制校验（AssemblyVersion 前缀 + version.json 等值）。
[assembly: AssemblyVersion("0.7.2.0")]
[assembly: AssemblyFileVersion("0.7.2.0")]
