#requires -Version 5.1
<#
  Ds in Apex · Playnite 插件打包脚本（P9）

  产出：dist\DsInApex-Playnite-<版本>.pext
        （.pext = ZIP，且 extension.yaml 必须在压缩包根目录 —— Playnite 的
         ExtensionInstaller 用 ZipFile 直接读根下的 extension.yaml）

  ⚠️ 本脚本必须存为 UTF-8 **with BOM**，否则 PowerShell 5.1 按 GBK 解析直接 ParserError。
     用 Write 工具直出的 .ps1 没有 BOM，改完务必检查：
       [IO.File]::WriteAllText($p, $t, [Text.UTF8Encoding]::new($true))

  ⚠️ 构建走 `dotnet build`，不走 VS 的 MSBuild.exe：
     ① 本机没有 .NET Framework 4.6.2 参考程序集，由 csproj 里的
        Microsoft.NETFramework.ReferenceAssemblies NuGet 包兜住；
     ② dotnet CLI 是本项目环境下唯一可用的构建入口。
     ③ 插件 TFM 必须是 net462 —— PlayniteSDK 6.16.0 的 nupkg 里只有 lib/net462。
#>
param(
    [string]$PlayniteSdkPath = "",
    [string]$Configuration = "Release",
    [string]$OutputDir = "",
    [switch]$KeepIntermediate
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root "src\DsInApex.Playnite"
$project = Join-Path $projectDir "DsInApex.Playnite.csproj"
$output = Join-Path $projectDir "bin\$Configuration"
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    # 与 make-portable.ps1 落在同一处（build\release\），发布资产集中管理；
    # 该目录已被 .gitignore 的 /build/* 覆盖，无需额外规则。
    $OutputDir = Join-Path $root "build\release"
}

$sdkCache = Join-Path $root ".cache\playnite-sdk-6.16.0"
$sdkPackage = Join-Path $sdkCache "playnitesdk.6.16.0.nupkg"
# 上游钉版哈希（api.nuget.org 上 playnitesdk/6.16.0 的 nupkg）
$sdkPackageSha256 = "B83C0553C479894F922E27F638D4DB75F90B8C5B3A5659F6FA85E764A607FF24"
$sdkDownloadUrl = "https://api.nuget.org/v3-flatcontainer/playnitesdk/6.16.0/playnitesdk.6.16.0.nupkg"

function Fail($message) {
    Write-Host ""
    Write-Host "[FAIL] $message" -ForegroundColor Red
    exit 1
}

function Ok($message) {
    Write-Host "[ OK ] $message" -ForegroundColor Green
}

# ────────────────────────── 1. 解析 Playnite SDK ──────────────────────────

if ([string]::IsNullOrWhiteSpace($PlayniteSdkPath) -and
    -not [string]::IsNullOrWhiteSpace($env:PLAYNITE_SDK_PATH)) {
    $PlayniteSdkPath = $env:PLAYNITE_SDK_PATH
}

if ([string]::IsNullOrWhiteSpace($PlayniteSdkPath)) {
    $installCandidates = @(
        $env:PLAYNITE_INSTALL_DIR,
        (Join-Path $env:ProgramFiles "Playnite"),
        (Join-Path ${env:ProgramFiles(x86)} "Playnite")
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($candidate in $installCandidates) {
        $sdk = Join-Path $candidate "Playnite.SDK.dll"
        if (Test-Path -LiteralPath $sdk) {
            $PlayniteSdkPath = $sdk
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($PlayniteSdkPath)) {
    Write-Host "本机未安装 Playnite，改用钉版官方 PlayniteSDK 6.16.0 包（SHA-256 校验）..."
    New-Item -ItemType Directory -Force -Path $sdkCache | Out-Null

    $cachedHash = if (Test-Path -LiteralPath $sdkPackage) {
        (Get-FileHash -LiteralPath $sdkPackage -Algorithm SHA256).Hash
    } else { "" }

    if ($cachedHash -ne $sdkPackageSha256) {
        if (Test-Path -LiteralPath $sdkPackage) {
            Remove-Item -LiteralPath $sdkPackage -Force
        }
        Invoke-WebRequest -UseBasicParsing -Uri $sdkDownloadUrl -OutFile $sdkPackage
    }

    $actualHash = (Get-FileHash -LiteralPath $sdkPackage -Algorithm SHA256).Hash
    if ($actualHash -ne $sdkPackageSha256) {
        Fail "PlayniteSDK 包 SHA-256 不匹配：期望 $sdkPackageSha256，实际 $actualHash"
    }

    $expanded = Join-Path $sdkCache "lib\net462\Playnite.SDK.dll"
    if (-not (Test-Path -LiteralPath $expanded)) {
        $zipCopy = Join-Path $sdkCache "playnitesdk.6.16.0.zip"
        Copy-Item -LiteralPath $sdkPackage -Destination $zipCopy -Force
        # nupkg 就是 zip；用 tar 解包（本机禁用 Add-Type，不用 Expand-Archive 也行）
        & "$env:SystemRoot\System32\tar.exe" -xf $zipCopy -C $sdkCache
        if ($LASTEXITCODE -ne 0) { Fail "解包 PlayniteSDK 失败。" }
    }

    if (-not (Test-Path -LiteralPath $expanded)) {
        Fail "Playnite.SDK.dll 未能在 SDK 包中找到：$expanded"
    }
    $PlayniteSdkPath = $expanded
}

if (-not (Test-Path -LiteralPath $PlayniteSdkPath)) {
    Fail "Playnite.SDK.dll 不存在：$PlayniteSdkPath"
}
Ok "Playnite SDK：$PlayniteSdkPath"

# ────────────────────────── 2. 编译 ──────────────────────────

Write-Host ""
Write-Host "编译插件（$Configuration）..."
& dotnet build $project -c $Configuration -v m "-p:PlayniteSdkPath=$PlayniteSdkPath"
if ($LASTEXITCODE -ne 0) {
    Fail "插件编译失败。"
}

$moduleName = "DsInApex.Playnite.dll"
$required = @(
    (Join-Path $output $moduleName),
    (Join-Path $output "extension.yaml"),
    (Join-Path $output "icon.png"),
    (Join-Path $output "Localization\en_US.xaml"),
    (Join-Path $output "Localization\zh_CN.xaml")
)
foreach ($file in $required) {
    if (-not (Test-Path -LiteralPath $file)) {
        Fail "缺少编译产物：$file"
    }
}

# ⚠️ Playnite.SDK.dll 绝不能随包分发（宿主自带）
if (Test-Path -LiteralPath (Join-Path $output "Playnite.SDK.dll")) {
    Fail "产物目录混入了 Playnite.SDK.dll —— 检查 csproj 的 <Private>false</Private>。"
}
Ok "编译产物齐备：$output"

# ────────────────────────── 3. 打包 .pext ──────────────────────────

$manifest = Get-Content -LiteralPath (Join-Path $output "extension.yaml") -Raw
$extensionId = [regex]::Match($manifest, '(?m)^Id:\s*(.+)$').Groups[1].Value.Trim()
$version = [regex]::Match($manifest, '(?m)^Version:\s*(.+)$').Groups[1].Value.Trim()
if ([string]::IsNullOrWhiteSpace($extensionId) -or [string]::IsNullOrWhiteSpace($version)) {
    Fail "extension.yaml 缺少可用的 Id / Version。"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$packagePath = Join-Path $OutputDir ("DsInApex-Playnite-{0}.pext" -f $version)
$tempZip = Join-Path $OutputDir ("DsInApex-Playnite-{0}.zip" -f $version)

foreach ($stale in @($packagePath, $tempZip)) {
    if (Test-Path -LiteralPath $stale) {
        Remove-Item -LiteralPath $stale -Force
    }
}

Write-Host ""
Write-Host "打包 .pext..."
# 通配子项（而不是目录本身）：让 extension.yaml 落在压缩包根目录
Compress-Archive -Path (Join-Path $output "*") -DestinationPath $tempZip -CompressionLevel Optimal
Move-Item -LiteralPath $tempZip -Destination $packagePath

if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    Fail "打包未产生预期文件：$packagePath"
}

$sizeKb = [math]::Round((Get-Item -LiteralPath $packagePath).Length / 1KB, 1)
Ok "打包完成：$packagePath（$sizeKb KB）"
Write-Host ""
Write-Host "安装方式：Playnite → 扩展 → 从文件安装扩展 → 选择上述 .pext" -ForegroundColor Cyan
