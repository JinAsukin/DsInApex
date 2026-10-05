<#
.SYNOPSIS
    组装 Ds in Apex 便携版（portable 绿色包）。

.DESCRIPTION
    这是 P7 的交付脚本 —— 把「Release 构建产物 + C++ 引擎 + 驱动安装器 + 合规文件」
    按固定目录规范拼成一个解压即用的目录，再压成 ZIP 发布资产。

    ══════════════════════════════════════════════════════════════
    为什么用 `dotnet build -c Release` 而**不是** `dotnet publish`
    ══════════════════════════════════════════════════════════════
    WinUI 3 + Unpackaged + SelfContained 组合下，`dotnet publish` 产出的目录
    **缺少 XAML 编译产物**（`App.xbf` / `Views\*.xbf` / `DsInApex.pri` /
    `Assets\*`）—— 文件都在，但运行 exe 时进程静默退出（退出码 127）。
    `dotnet build` 的输出反而是完整的可运行集合（self-contained 运行时
    在 build 阶段就已复制到输出目录）。本项目据此定案：**交付物取自 build 输出**。

    ══════════════════════════════════════════════════════════════
    目录规范
    ══════════════════════════════════════════════════════════════
    DsInApex-Portable-<版本>\
      ├─ DsInApex.exe                    主程序（双击即用）
      ├─ （自包含 .NET / WindowsAppSDK 运行时）
      ├─ Prerequisites\                  驱动安装器 + 许可证副本
      ├─ engine\                         C++ 引擎（上游二进制，见下方精简说明）
      ├─ LICENSE                         GPL-3.0-or-later
      ├─ THIRD_PARTY_NOTICES.md
      ├─ SOURCE_OFFER.md
      ├─ README.md                       使用者 / 编译者说明
      ├─ version.json                    版本清单（与仓库根同步，更新检查端点读这个）
      └─ SHA256SUMS.txt                  包内逐文件校验清单

.PARAMETER Configuration
    构建配置，默认 Release（便携包只发 Release）。

.PARAMETER Platform
    MSBuild 平台，默认 x64。**必须指定** —— 不指定时 WinUI 的 XAML 资源
    目标可能落到另一套路径上，产物不完整。

.PARAMETER Version
    覆盖版本号。默认从 `src\DsInApex.App\DsInApex.App.csproj` 的 <Version> 读取。

.PARAMETER OutputRoot
    产物根目录，默认 `<仓库>\build\release`。

.PARAMETER SkipBuild
    跳过构建，直接用现有 bin 输出（调试脚本用）。

.PARAMETER KeepEngineTray
    保留引擎包里上游自带的 Tray / Control 可执行与启动脚本（默认剔除）。

.PARAMETER KeepEngineDrivers
    保留 `engine\Drivers\`（默认剔除 —— 与 `Prerequisites\` 是同一批安装器，重复约 34 MB）。

.PARAMETER NoZip
    只组装目录，不压缩。

.EXAMPLE
    pwsh -File build\make-portable.ps1

.EXAMPLE
    # 只想快速验证组装逻辑，不重新构建
    powershell -File build\make-portable.ps1 -SkipBuild -NoZip
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Platform      = 'x64',
    [string] $Version,
    [string] $OutputRoot,
    [switch] $SkipBuild,
    [switch] $KeepEngineTray,
    [switch] $KeepEngineDrivers,
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# dotnet CLI 的输出是 UTF-8，而 Windows PowerShell 5.1 默认按本地代码页（中文区 936）解码
# → 「正在确定要还原的项目…」会显示成「姝ｅ湪纭畾…」。显式把控制台编码钉到 UTF-8。
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding           = New-Object System.Text.UTF8Encoding($false)
} catch {
    # 某些宿主（如被重定向到管道）不允许改编码，忽略即可 —— 不影响打包结果
}

# ══════════════════════════════════════════════════════════════
#  0 · 路径与常量
# ══════════════════════════════════════════════════════════════

$RepoRoot   = Split-Path -Parent $PSScriptRoot          # build\ 的上一级 = 仓库根
if ([string]::IsNullOrWhiteSpace($RepoRoot)) { $RepoRoot = (Get-Location).Path }

$AppCsproj      = Join-Path $RepoRoot 'src\DsInApex.App\DsInApex.App.csproj'
$SolutionFile   = Join-Path $RepoRoot 'DsInApex.sln'
$EngineSource   = Join-Path $RepoRoot 'vendor\portable-1.0.0-beta.10\ApexSenseBridge-Portable'
$RepoReadme     = Join-Path $RepoRoot 'README.md'
$RepoVersion    = Join-Path $RepoRoot 'version.json'

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $RepoRoot 'build\release'
}

$RepoSlug   = 'JinAsukin/DsInApex'
$EngineVer  = '1.0.0-beta.10'
$EngineBase = 'e129848'

function Write-Step([string] $text) {
    Write-Host ''
    Write-Host "── $text " -ForegroundColor Cyan -NoNewline
    Write-Host ('─' * [Math]::Max(1, 60 - $text.Length)) -ForegroundColor DarkCyan
}

function Write-Ok([string] $text)   { Write-Host "   [OK]   $text" -ForegroundColor Green }
function Write-Info([string] $text) { Write-Host "   [..]   $text" -ForegroundColor Gray }
function Write-Warn2([string] $text){ Write-Host "   [警告] $text" -ForegroundColor Yellow }

function Format-Size([long] $bytes) {
    if ($bytes -ge 1GB) { return ('{0:N2} GB' -f ($bytes / 1GB)) }
    if ($bytes -ge 1MB) { return ('{0:N1} MB' -f ($bytes / 1MB)) }
    if ($bytes -ge 1KB) { return ('{0:N1} KB' -f ($bytes / 1KB)) }
    return "$bytes B"
}

function Get-DirSize([string] $path) {
    if (-not (Test-Path -LiteralPath $path)) { return 0 }
    $sum = (Get-ChildItem -LiteralPath $path -Recurse -File -Force |
            Measure-Object -Property Length -Sum).Sum
    if ($null -eq $sum) { return 0 }
    return [long] $sum
}

# ══════════════════════════════════════════════════════════════
#  1 · 版本号
# ══════════════════════════════════════════════════════════════

Write-Step '读取版本号'

if ([string]::IsNullOrWhiteSpace($Version)) {
    if (-not (Test-Path -LiteralPath $AppCsproj)) {
        throw "找不到 csproj：$AppCsproj"
    }

    [xml] $proj = Get-Content -LiteralPath $AppCsproj -Raw -Encoding UTF8
    $declared = @()
    foreach ($group in $proj.Project.PropertyGroup) {
        if ($null -ne $group.Version -and -not [string]::IsNullOrWhiteSpace($group.Version)) {
            $declared += $group.Version
        }
    }
    if ($declared.Count -eq 0) { throw "csproj 里没有 <Version> 节点" }

    $Version = [string] $declared[0]
}

$Version = $Version.Trim()
Write-Ok "版本号 = $Version（来源：$(if ($declared) { 'csproj' } else { '命令行' }))"

if ($Version -notmatch '^\d+\.\d+\.\d+') {
    Write-Warn2 "版本号不是典型 x.y.z 形式，疑似误填：$Version"
}

$Tag        = "v$Version"
$PackageDir = Join-Path $OutputRoot "DsInApex-Portable-$Version"
$ZipPath    = Join-Path $OutputRoot "DsInApex-Portable-$Version.zip"

# ══════════════════════════════════════════════════════════════
#  2 · 构建
# ══════════════════════════════════════════════════════════════

if (-not $SkipBuild) {
    Write-Step "构建 Release 产物（$Configuration / $Platform）"

    # 驻留托盘的 DIA 会锁住 exe/dll，导致 MSB3027/3021 —— 这是常态，不是偶发。
    # 自检模式运行后进程不会自动退出，所以打包前一律清理。
    $running = Get-Process -Name 'DsInApex' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Info "发现 $($running.Count) 个常驻 DsInApex 进程 → 终止（否则文件被锁）"
        $running | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 700
    }

    & dotnet build $SolutionFile -c $Configuration "-p:Platform=$Platform" -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "构建失败（dotnet build 退出码 $LASTEXITCODE）"
    }
    Write-Ok '构建成功'
} else {
    Write-Step '跳过构建（-SkipBuild）'
}

# ══════════════════════════════════════════════════════════════
#  3 · 定位构建输出
# ══════════════════════════════════════════════════════════════

Write-Step '定位构建输出目录'

$appBinRoot = Join-Path $RepoRoot "src\DsInApex.App\bin\$Platform\$Configuration"
$buildOutput = $null

if (Test-Path -LiteralPath $appBinRoot) {
    $exe = Get-ChildItem -LiteralPath $appBinRoot -Recurse -File -Filter 'DsInApex.exe' -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1
    if ($exe) { $buildOutput = $exe.Directory.FullName }
}

if (-not $buildOutput) {
    # 兜底：Platform 段可能不存在（未指定 Platform 时路径形态不同）
    $exe = Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'src\DsInApex.App\bin') -Recurse -File -Filter 'DsInApex.exe' -ErrorAction SilentlyContinue |
           Where-Object { $_.FullName -like "*\$Configuration\*" } |
           Sort-Object LastWriteTime -Descending |
           Select-Object -First 1
    if ($exe) { $buildOutput = $exe.Directory.FullName }
}

if (-not $buildOutput) {
    throw "找不到构建输出（DsInApex.exe）。先不带 -SkipBuild 跑一次。"
}

Write-Ok "输出目录 = $buildOutput"

# ── 完整性闸门：XAML 产物与 PRI 必须存在，否则包出来是「双击无反应」的废品 ──
$required = @('DsInApex.exe', 'DsInApex.pri', 'App.xbf', 'MainWindow.xbf')
$missing  = @()
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildOutput $name))) { $missing += $name }
}
if ($missing.Count -gt 0) {
    throw ("构建输出缺少关键文件：{0}。`n" +
           "       高度怀疑你用的是 dotnet publish 的产物 —— publish 在 " +
           "WinUI3+Unpackaged 下会丢 XAML 资源，务必改用 dotnet build。" -f ($missing -join ', '))
}
Write-Ok 'XAML 产物与 PRI 齐备'

# ── 版本一致性校验：exe 里的产品版本必须与目录名一致 ──
$exePath = Join-Path $buildOutput 'DsInApex.exe'
$exeVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($exePath)
Write-Info "exe 产品版本 = $($exeVersion.ProductVersion) / 文件版本 = $($exeVersion.FileVersion)"
if ($exeVersion.ProductVersion -and $exeVersion.ProductVersion -notlike "$Version*") {
    Write-Warn2 "exe 版本与打包版本号不一致 —— 确认 csproj 的 <Version> 是否已更新"
}

# ══════════════════════════════════════════════════════════════
#  4 · 组装
# ══════════════════════════════════════════════════════════════

Write-Step "组装 → $PackageDir"

if (Test-Path -LiteralPath $PackageDir) {
    Remove-Item -LiteralPath $PackageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PackageDir -Force | Out-Null

# 4.1 应用 + 自包含运行时
Copy-Item -Path (Join-Path $buildOutput '*') -Destination $PackageDir -Recurse -Force
Write-Ok "应用与运行时已复制（$(Format-Size (Get-DirSize $PackageDir))）"

# 4.2 引擎
if (-not (Test-Path -LiteralPath $EngineSource)) {
    throw ("找不到引擎目录：$EngineSource`n" +
           "       vendor\ 不入版本控制（.gitignore），需要先放置官方 1.0.0-beta.10 portable 包。")
}

$engineDst = Join-Path $PackageDir 'engine'
New-Item -ItemType Directory -Path $engineDst -Force | Out-Null

$engineSkip = New-Object System.Collections.Generic.List[string]

if (-not $KeepEngineDrivers) {
    # engine\Drivers\ 里的两个安装器与包根 Prerequisites\ 是同一批文件（约 34 MB）。
    # DIA 的驱动管理页走 Prerequisites，引擎自带的 Install-Drivers 脚本 DIA 不使用。
    $engineSkip.Add('Drivers')
}

if (-not $KeepEngineTray) {
    # 上游托盘版 / 控制面板 / 启动脚本：DIA 用 WinUI 3 全量重写了这些，
    # 留着只会让用户对着四个 exe 发懵「我到底该点哪个」。
    $engineSkip.AddRange([string[]] @(
        'ApexSenseBridgeTray.exe',
        'ApexSenseBridgeTray.exe.config',
        'ApexSenseBridgeControl.exe',
        'Install-Drivers.cmd',
        'Install-Drivers.ps1',
        'Start-ApexSenseBridge.cmd',
        'README-PORTABLE.txt'
    ))
}

$engineCopied = 0
Get-ChildItem -LiteralPath $EngineSource -Force | ForEach-Object {
    if ($engineSkip -contains $_.Name) {
        Write-Info "跳过 engine\$($_.Name)"
        return
    }
    Copy-Item -LiteralPath $_.FullName -Destination $engineDst -Recurse -Force
    $engineCopied++
}
Write-Ok "引擎已复制：$engineCopied 个顶层项（$(Format-Size (Get-DirSize $engineDst))）"

# 4.2.1 引擎版本守门（★ 2026-10-05 新增）
# 🔴 为什么必须有这一条：换引擎要在【三处】同步改 —— 本文件的 $EngineSource、
#    $EngineVer（此处上方），以及 src\DsInApex.Core\Services\EngineLocator.cs 的
#    vendor 目录名。只改源目录而不改 $EngineVer 时，version.json 声明的引擎版本
#    与实际打包进去的二进制不符，而【编译与打包全绿、零提示】——
#    0.7.1 真实踩过一次。所以这里直接问二进制"你是谁"，对不上就硬失败。
$engineExeInPack = Join-Path $engineDst 'ApexSenseBridge.exe'
if (-not (Test-Path -LiteralPath $engineExeInPack)) {
    throw "引擎包内没有 ApexSenseBridge.exe：$engineExeInPack"
}

$engineHelp = ''
try {
    # help 是只读命令，不碰硬件；引擎把 Release 行打在输出第二行
    $engineHelp = (& $engineExeInPack help 2>&1 | Out-String)
} catch {
    Write-Warn2 "无法执行引擎 help（$($_.Exception.Message)）→ 跳过版本守门"
}

if ($engineHelp -match 'Release:\s*(?<ver>[0-9][0-9A-Za-z\.\-]*)') {
    $packedVer = $Matches['ver'].Trim()
    if ($packedVer -ne $EngineVer) {
        throw ("引擎版本不一致：打包进去的是 $packedVer，但 `$EngineVer 声明的是 $EngineVer`n" +
               "       请把 `$EngineVer / `$EngineBase（本文件）与 EngineLocator.cs 的 vendor 目录名一起改。")
    }
    Write-Ok "引擎版本守门通过：包内 ApexSenseBridge.exe 自报 $packedVer"
} else {
    $firstLine = ($engineHelp -split "`r?`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
    Write-Warn2 ("引擎 help 输出里没有 Release 行 → 无法守门（输出首行：" + $firstLine + "）")
}

if ($engineSkip.Count -gt 0) {
    Write-Info "本次剔除 $($engineSkip.Count) 项上游冗余（-KeepEngineTray / -KeepEngineDrivers 可保留）"
}

# 4.3 合规文件（csproj 已复制进 build 输出；这里只做缺失兜底）
foreach ($name in @('LICENSE', 'NOTICE.md', 'THIRD_PARTY_NOTICES.md', 'SOURCE_OFFER.md')) {
    $target = Join-Path $PackageDir $name
    if (Test-Path -LiteralPath $target) { continue }
    $source = Join-Path $RepoRoot $name
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination $target -Force
        Write-Info "$name 从仓库根补齐（csproj 未复制成功）"
    } else {
        Write-Warn2 "$name 既不在输出目录也不在仓库根 —— GPL 合规缺件！"
    }
}

# 4.4 用户向 README
if (Test-Path -LiteralPath $RepoReadme) {
    Copy-Item -LiteralPath $RepoReadme -Destination (Join-Path $PackageDir 'README.md') -Force
    Write-Ok 'README.md 已随包'
} else {
    Write-Warn2 '仓库根缺少 README.md —— 便携包将没有使用说明'
}

# ══════════════════════════════════════════════════════════════
#  5 · version.json（更新检查端点 1 读取的清单）
# ══════════════════════════════════════════════════════════════

Write-Step '生成 version.json'

$releaseUrl = "https://github.com/$RepoSlug/releases/tag/$Tag"
$assetUrl   = "https://github.com/$RepoSlug/releases/download/$Tag/DsInApex-Portable-$Version.zip"

$notesText = @(
    "Ds in Apex $Version",
    "· 飞智 APEX 4 → 原生 PS5 DualSense 虚拟化，WinUI 3 原生中文界面",
    "· 解压即用：运行 DsInApex.exe；首次使用请在「驱动管理」页安装前置驱动并重启",
    "· 上游引擎基线：ApexSenseBridge $EngineVer ($EngineBase)"
) -join "`n"

$versionJson = [ordered] @{
    version        = $Version
    releasedAt     = (Get-Date -Format 'yyyy-MM-dd')
    releaseUrl     = $releaseUrl
    portableAsset  = $assetUrl
    notes          = $notesText
    engineVersion  = $EngineVer
    engineBaseline = $EngineBase
    repository     = "https://github.com/$RepoSlug"
    license        = 'GPL-3.0-or-later'
}

$jsonText = ($versionJson | ConvertTo-Json -Depth 4) + "`n"

# 仓库根（main 分支 → jsDelivr 端点）与包内各一份，保证两者永远同步
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($RepoVersion, $jsonText, $utf8NoBom)
[System.IO.File]::WriteAllText((Join-Path $PackageDir 'version.json'), $jsonText, $utf8NoBom)
Write-Ok "已写入：$RepoVersion 与 <包>\version.json"

# ══════════════════════════════════════════════════════════════
#  6 · SHA256SUMS.txt（包内逐文件校验）
# ══════════════════════════════════════════════════════════════

Write-Step '生成 SHA256SUMS.txt'

$packageFull = (Resolve-Path -LiteralPath $PackageDir).Path
$lines = New-Object System.Collections.Generic.List[string]
$hashed = 0

Get-ChildItem -LiteralPath $PackageDir -Recurse -File -Force |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($packageFull.Length).TrimStart('\').Replace('\', '/')
        if ($relative -eq 'SHA256SUMS.txt') { return }   # 不给自己算哈希
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        $lines.Add("$hash *$relative")
        $hashed++
    }

$sumsPath = Join-Path $PackageDir 'SHA256SUMS.txt'
[System.IO.File]::WriteAllLines($sumsPath, $lines, $utf8NoBom)
Write-Ok "已登记 $hashed 个文件"

# ══════════════════════════════════════════════════════════════
#  7 · 压缩
# ══════════════════════════════════════════════════════════════

$dirSize = Get-DirSize $PackageDir
$zipSize = 0

if (-not $NoZip) {
    Write-Step '压缩为 ZIP'

    if (Test-Path -LiteralPath $ZipPath) { Remove-Item -LiteralPath $ZipPath -Force }

    # ⚠️ -Path 传目录本身（不是 dir\*），这样包内根目录 = DsInApex-Portable-<版本>\
    Compress-Archive -Path $PackageDir -DestinationPath $ZipPath -CompressionLevel Optimal -Force

    if (-not (Test-Path -LiteralPath $ZipPath)) { throw '压缩失败：ZIP 未生成' }

    $zipItem  = Get-Item -LiteralPath $ZipPath
    $zipSize  = $zipItem.Length
    $dirSize2 = $dirSize
    $ratio    = if ($dirSize2 -gt 0) { [Math]::Round(100.0 * $zipSize / $dirSize2, 1) } else { 0 }

    Write-Ok "ZIP 已生成：$(Format-Size $zipSize)（原始 $(Format-Size $dirSize2)，压缩率 $ratio%）"
} else {
    Write-Step '跳过压缩（-NoZip）'
}

# ══════════════════════════════════════════════════════════════
#  8 · 报告
# ══════════════════════════════════════════════════════════════

$fileCount = (Get-ChildItem -LiteralPath $PackageDir -Recurse -File -Force | Measure-Object).Count

Write-Host ''
Write-Host '════════════════════════════════════════════════════════════' -ForegroundColor DarkCyan
Write-Host " Ds in Apex 便携包 · $Version" -ForegroundColor White
Write-Host '════════════════════════════════════════════════════════════' -ForegroundColor DarkCyan
Write-Host ("  目录      : {0}" -f $PackageDir)
Write-Host ("  文件数    : {0}" -f $fileCount)
Write-Host ("  解压体积  : {0}" -f (Format-Size $dirSize))
if ($zipSize -gt 0) {
    Write-Host ("  ZIP       : {0}" -f $ZipPath)
    Write-Host ("  ZIP 体积  : {0}" -f (Format-Size $zipSize))
    Write-Host ("  ZIP SHA256: {0}" -f (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash)
}

Write-Host ''
Write-Host '  目录规范自检：' -ForegroundColor White
$layout = [ordered] @{
    'DsInApex.exe'              = (Join-Path $PackageDir 'DsInApex.exe')
    'DsInApex.pri'              = (Join-Path $PackageDir 'DsInApex.pri')
    'engine\ApexSenseBridge.exe'= (Join-Path $PackageDir 'engine\ApexSenseBridge.exe')
    'engine\libVIIPER.dll'      = (Join-Path $PackageDir 'engine\libVIIPER.dll')
    'engine\viiper.exe'         = (Join-Path $PackageDir 'engine\viiper.exe')
    'Prerequisites\'            = (Join-Path $PackageDir 'Prerequisites')
    'LICENSE'                   = (Join-Path $PackageDir 'LICENSE')
    'NOTICE.md'                 = (Join-Path $PackageDir 'NOTICE.md')
    'THIRD_PARTY_NOTICES.md'    = (Join-Path $PackageDir 'THIRD_PARTY_NOTICES.md')
    'SOURCE_OFFER.md'           = (Join-Path $PackageDir 'SOURCE_OFFER.md')
    'README.md'                 = (Join-Path $PackageDir 'README.md')
    'version.json'              = (Join-Path $PackageDir 'version.json')
    'SHA256SUMS.txt'            = (Join-Path $PackageDir 'SHA256SUMS.txt')
}
$layoutFail = 0
foreach ($key in $layout.Keys) {
    $exists = Test-Path -LiteralPath $layout[$key]
    if (-not $exists) { $layoutFail++ }
    # 用 √ / × 而不是 ✓ / ✗：后两者不在 GBK 字符集里，
    # Windows PowerShell 5.1 的传统控制台会把它们显示成 "?"。
    $mark  = if ($exists) { '√' } else { '×' }
    $color = if ($exists) { 'Gray' } else { 'Red' }
    Write-Host ("    {0}  {1}" -f $mark, $key) -ForegroundColor $color
}

Write-Host ''
if ($layoutFail -eq 0) {
    Write-Host '  结论：布局完整，可以发布。' -ForegroundColor Green
} else {
    Write-Host "  结论：$layoutFail 项目缺失，别发。" -ForegroundColor Red
}
Write-Host ''
Write-Host '  下一步（发布）：' -ForegroundColor White
Write-Host "    git tag $Tag && git push origin $Tag"
Write-Host "    gh release create $Tag `"<ZIP 路径>`" --title `"Ds in Apex $Version`" --notes-file <说明文件>"

if ($layoutFail -gt 0) { exit 1 }
