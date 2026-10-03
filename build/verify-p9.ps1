#requires -Version 5.1
<#
  Ds in Apex · P9 静态验收（Playnite 插件）

  四个阶段，任一失败 exit 1：
    S1 构建产物完整性（可选 -SkipBuild 复用已有产物）
    S2 身份与版本一致性（extension.yaml / AssemblyInfo / 插件 C# / 仓库 version.json）
    S3 .pext 包结构（zip 根必须有 extension.yaml；且绝不能夹带 Playnite.SDK.dll）
    S4 本地化审计（en/zh 键集合一致 · 占位符一致 · 源码引用的键全覆盖 · 无空值）

  ⚠️ 必须存为 UTF-8 with BOM（PS 5.1 要求）。
  ⚠️ 本脚本<b>只读</b>，不碰注册表/自启项/系统状态 —— 无需崩溃可恢复标记。
  ⚠️ 备注里的 «» 只是排版；脚本输出统一用 ASCII 标记，避免 GBK 缺字。
#>
param(
    [string]$PackagePath = "",
    [string]$PlayniteSdkPath = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$projectDir = Join-Path $root "src\DsInApex.Playnite"
$project = Join-Path $projectDir "DsInApex.Playnite.csproj"
$output = Join-Path $projectDir "bin\Release"
$dist = Join-Path $root "build\release"

$script:failures = 0
$script:checks = 0

function Section($title) {
    Write-Host ""
    Write-Host ("=" * 68)
    Write-Host $title
    Write-Host ("=" * 68)
}

function Check($condition, $description, $detail) {
    $script:checks++
    if ($condition) {
        Write-Host "  [PASS] $description" -ForegroundColor Green
    } else {
        $script:failures++
        Write-Host "  [FAIL] $description" -ForegroundColor Red
    }
    if (-not [string]::IsNullOrWhiteSpace($detail)) {
        Write-Host "         $detail" -ForegroundColor DarkGray
    }
}

function Info($text) {
    Write-Host "  [info] $text" -ForegroundColor DarkGray
}

# ────────────────────────── S1 · 构建产物 ──────────────────────────
Section "S1 构建产物完整性"

$moduleName = "DsInApex.Playnite.dll"
$modulePath = Join-Path $output $moduleName

if (-not $SkipBuild) {
    if ([string]::IsNullOrWhiteSpace($PlayniteSdkPath) -and
        -not [string]::IsNullOrWhiteSpace($env:PLAYNITE_SDK_PATH)) {
        $PlayniteSdkPath = $env:PLAYNITE_SDK_PATH
    }
    if ([string]::IsNullOrWhiteSpace($PlayniteSdkPath)) {
        $cachedSdk = Join-Path $root ".cache\playnite-sdk-6.16.0\lib\net462\Playnite.SDK.dll"
        if (Test-Path -LiteralPath $cachedSdk) { $PlayniteSdkPath = $cachedSdk }
    }
    if ([string]::IsNullOrWhiteSpace($PlayniteSdkPath) -or -not (Test-Path -LiteralPath $PlayniteSdkPath)) {
        Write-Host "  未找到 Playnite.SDK.dll，请用 build-playnite-extension.ps1 构建或传 -PlayniteSdkPath。" -ForegroundColor Yellow
        exit 1
    }

    Write-Host "  编译中（dotnet build -c Release）..."
    & dotnet build $project -c Release -v q "-p:PlayniteSdkPath=$PlayniteSdkPath" | Out-Null
    Check ($LASTEXITCODE -eq 0) "dotnet build 成功" "退出码 $LASTEXITCODE"
} else {
    Info "已跳过构建（-SkipBuild），只校验既有产物"
}

Check (Test-Path -LiteralPath $modulePath) "主程序集存在" $moduleName
Check (Test-Path -LiteralPath (Join-Path $output "extension.yaml")) "extension.yaml 已复制到产物目录"
Check (Test-Path -LiteralPath (Join-Path $output "icon.png")) "icon.png 已复制到产物目录"
Check (Test-Path -LiteralPath (Join-Path $output "Localization\en_US.xaml")) "en_US.xaml 以松散文件随包"
Check (Test-Path -LiteralPath (Join-Path $output "Localization\zh_CN.xaml")) "zh_CN.xaml 以松散文件随包"
Check (-not (Test-Path -LiteralPath (Join-Path $output "Playnite.SDK.dll"))) "产物未夹带 Playnite.SDK.dll"

# XAML 是否真的编译进程序集（baml 在 <Assembly>.g.resources 里）——
# 这是「SDK 风格 csproj 在 net462 下静默跳过 XAML 编译」那个坑的守门人
if (Test-Path -LiteralPath $modulePath) {
    $dllBytes = [IO.File]::ReadAllBytes($modulePath)
    $dllText = [Text.Encoding]::ASCII.GetString($dllBytes)
    Check ($dllText.Contains("DsInApex.Playnite.g.resources")) `
        "XAML 已编译进程序集（g.resources 存在）" "缺它则 InitializeComponent 运行时必炸"
    Check ($dllText.Contains("DsInApex.Playnite.supported_games.json")) `
        "嵌入式游戏库资源在位" "LogicalName 与 SupportedGameCatalog 常量必须一致"
}

# ────────────────────────── S2 · 身份与版本一致性 ──────────────────────────
Section "S2 身份与版本一致性"

$manifestPath = Join-Path $projectDir "extension.yaml"
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8
$yamlId = [regex]::Match($manifest, '(?m)^Id:\s*(.+)$').Groups[1].Value.Trim()
$yamlVersion = [regex]::Match($manifest, '(?m)^Version:\s*(.+)$').Groups[1].Value.Trim()
$yamlModule = [regex]::Match($manifest, '(?m)^Module:\s*(.+)$').Groups[1].Value.Trim()
$yamlType = [regex]::Match($manifest, '(?m)^Type:\s*(.+)$').Groups[1].Value.Trim()

Info "extension.yaml → Id=$yamlId · Version=$yamlVersion · Module=$yamlModule · Type=$yamlType"
Check ($yamlModule -eq $moduleName) "Module 与程序集名一致" "yaml=$yamlModule / 实际=$moduleName"
Check ($yamlType -eq "GenericPlugin") "Type 为 GenericPlugin"

$yamlGuidMatch = [regex]::Match($yamlId, '([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})')
Check $yamlGuidMatch.Success "Id 内含合法 GUID"
$yamlGuid = if ($yamlGuidMatch.Success) { $yamlGuidMatch.Groups[1].Value.ToLowerInvariant() } else { "" }

$assemblyInfo = Get-Content -LiteralPath (Join-Path $projectDir "Properties\AssemblyInfo.cs") -Raw -Encoding UTF8
$asmGuid = [regex]::Match($assemblyInfo, 'Guid\("([0-9a-fA-F-]+)"\)').Groups[1].Value.ToLowerInvariant()
$asmVersion = [regex]::Match($assemblyInfo, 'AssemblyVersion\("([0-9.]+)"\)').Groups[1].Value
Check ($asmGuid -eq $yamlGuid) "AssemblyInfo Guid == extension.yaml Id 后缀" "$asmGuid / $yamlGuid"

$pluginSource = Get-Content -LiteralPath (Join-Path $projectDir "DsInApexPlugin.cs") -Raw -Encoding UTF8
$csGuid = [regex]::Match($pluginSource, 'Guid\.Parse\("([0-9a-fA-F-]+)"\)').Groups[1].Value.ToLowerInvariant()
Check ($csGuid -eq $yamlGuid) "DsInApexPlugin.Id == extension.yaml Id 后缀" "$csGuid / $yamlGuid"

Check ($asmVersion.StartsWith($yamlVersion + ".") -or $asmVersion -eq $yamlVersion) `
    "AssemblyVersion 与 extension.yaml Version 对齐" "asm=$asmVersion / yaml=$yamlVersion"

$versionJsonPath = Join-Path $root "version.json"
if (Test-Path -LiteralPath $versionJsonPath) {
    $versionJson = Get-Content -LiteralPath $versionJsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Check ($versionJson.version -eq $yamlVersion) `
        "与主程序版本同源（仓库 version.json）" "version.json=$($versionJson.version) / yaml=$yamlVersion"
} else {
    Info "仓库根无 version.json，跳过与主程序版本对齐检查"
}

# ────────────────────────── S3 · .pext 包结构 ──────────────────────────
Section "S3 .pext 包结构"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $candidates = @()
    if (Test-Path -LiteralPath $dist) {
        $candidates = Get-ChildItem -LiteralPath $dist -Filter "DsInApex-Playnite-*.pext" |
            Sort-Object LastWriteTime -Descending
    }
    if ($candidates.Count -gt 0) {
        $PackagePath = $candidates[0].FullName
    }
}

if ([string]::IsNullOrWhiteSpace($PackagePath) -or -not (Test-Path -LiteralPath $PackagePath)) {
    Check $false ".pext 包存在" "未找到 dist\DsInApex-Playnite-*.pext；先跑 build-playnite-extension.ps1"
} else {
    Info ("包：{0}（{1} KB）" -f (Split-Path -Leaf $PackagePath), `
        [math]::Round((Get-Item -LiteralPath $PackagePath).Length / 1KB, 1))

    $entries = & "$env:SystemRoot\System32\tar.exe" -tf $PackagePath 2>$null
    Check ($LASTEXITCODE -eq 0) ".pext 可被当作 zip 读取" "用 System32\tar.exe -tf"

    $normalized = @($entries | ForEach-Object { ($_ -replace '\\', '/').TrimStart('.', '/') })

    Check ($normalized -contains "extension.yaml") "extension.yaml 位于压缩包根目录" "Playnite ExtensionInstaller 只认根目录"
    Check ($normalized -contains $moduleName) "包含主程序集 $moduleName"
    Check ($normalized -contains "icon.png") "包含 icon.png"
    Check (($normalized -contains "Localization/en_US.xaml") -and
           ($normalized -contains "Localization/zh_CN.xaml")) "包含双语本地化文件"
    Check (-not ($normalized | Where-Object { $_ -like "*Playnite.SDK.dll" })) "未夹带 Playnite.SDK.dll"
    Check (-not ($normalized | Where-Object { $_ -like "*.pdb" })) "未夹带调试符号 .pdb"
    Check (-not ($normalized | Where-Object { $_ -like "*ApexSenseBridge.dll" })) "未残留上游旧程序集名"
}

# ────────────────────────── S4 · 本地化审计 ──────────────────────────
Section "S4 本地化审计"

$locDir = Join-Path $projectDir "Localization"
$enPath = Join-Path $locDir "en_US.xaml"
$zhPath = Join-Path $locDir "zh_CN.xaml"

function Read-LocDictionary($path) {
    # ⚠️ 必须显式 -Encoding UTF8：PS 5.1 的 Get-Content 默认按系统 ANSI(GBK) 解码，
    #    而这些 .xaml 是无 BOM 的 UTF-8。GBK 解码会把中文字符后紧邻的 ASCII 字节
    #    （如 </sys:String> 的 '<'）当成双字节字符的第二字节吃掉，导致正则大面积失配、
    #    键值错位 —— 看起来像「翻译缺了一大半」，实际是读取编码的锅。
    $text = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $map = @{}
    foreach ($m in [regex]::Matches($text, '(?s)x:Key="([^"]+)">(.*?)</sys:String>')) {
        $key = $m.Groups[1].Value
        $value = $m.Groups[2].Value
        $placeholders = @([regex]::Matches($value, '\{(\d+)\}') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $map[$key] = [pscustomobject]@{
            Key = $key
            Value = $value
            Placeholders = ($placeholders -join ",")
        }
    }
    return $map
}

$en = Read-LocDictionary $enPath
$zh = Read-LocDictionary $zhPath

Info ("键数：en_US={0} · zh_CN={1}" -f $en.Count, $zh.Count)
Check ($en.Count -gt 0) "英文基底字典非空"

$missingInZh = @($en.Keys | Where-Object { -not $zh.ContainsKey($_) })
$missingInEn = @($zh.Keys | Where-Object { -not $en.ContainsKey($_) })
Check (($missingInZh.Count -eq 0) -and ($missingInEn.Count -eq 0)) `
    "两语言键集合一致" ("zh 缺 {0} 条 · en 缺 {1} 条" -f $missingInZh.Count, $missingInEn.Count)
if ($missingInZh.Count -gt 0) { Info ("zh 缺失：" + ($missingInZh -join ", ")) }
if ($missingInEn.Count -gt 0) { Info ("en 缺失：" + ($missingInEn -join ", ")) }

$placeholderMismatch = @()
foreach ($key in $en.Keys) {
    if ($zh.ContainsKey($key) -and $en[$key].Placeholders -ne $zh[$key].Placeholders) {
        $placeholderMismatch += $key
    }
}
Check ($placeholderMismatch.Count -eq 0) "占位符逐键一致" `
    ($(if ($placeholderMismatch.Count -eq 0) { "0 处不一致" } else { $placeholderMismatch -join ", " }))

$emptyValues = @($en.Keys | Where-Object {
    [string]::IsNullOrWhiteSpace($en[$_].Value) -or ($zh.ContainsKey($_) -and [string]::IsNullOrWhiteSpace($zh[$_].Value))
})
Check ($emptyValues.Count -eq 0) "无空文案" ($(if ($emptyValues.Count -eq 0) { "0 条空值" } else { $emptyValues -join ", " }))

# 键前缀：Playnite 要求全局唯一且带 LOC 前缀
$badPrefix = @($en.Keys | Where-Object { -not $_.StartsWith("LOC") })
Check ($badPrefix.Count -eq 0) "所有键带 LOC 前缀（Playnite 硬要求）" ($badPrefix -join ", ")

# 源码里引用的键必须全部存在
# ⚠️ 用 -Path 通配 + -File，不要用 -LiteralPath + -Include：后者在 PS 5.1 下
#    -Include 会被忽略，连 bin 这种目录都会被返回，随后当文件读就直接抛 PermissionDenied。
$sourceFiles = Get-ChildItem -Path (Join-Path $projectDir "*") -Recurse -File |
    Where-Object {
        ($_.Extension -eq ".cs" -or $_.Extension -eq ".xaml") -and
        $_.FullName -notmatch '\\bin\\|\\obj\\|\\Localization\\'
    }
$referenced = New-Object System.Collections.Generic.HashSet[string]
foreach ($file in $sourceFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($m in [regex]::Matches($text, 'LOCDsInApex_[A-Za-z0-9_]+')) {
        [void]$referenced.Add($m.Value)
    }
}
Info ("源码引用键数：{0}（扫描 {1} 个源文件）" -f $referenced.Count, $sourceFiles.Count)

$undefined = @($referenced | Where-Object { -not $en.ContainsKey($_) } | Sort-Object)
Check ($undefined.Count -eq 0) "源码引用的键全部已定义" `
    ($(if ($undefined.Count -eq 0) { "0 个未定义" } else { $undefined -join ", " }))

$unused = @($en.Keys | Where-Object { -not $referenced.Contains($_) } | Sort-Object)
if ($unused.Count -eq 0) {
    Info "无孤立键（每个键都有引用点）"
} else {
    Info ("孤立键 {0} 个（不阻塞，供清理参考）：{1}" -f $unused.Count, ($unused -join ", "))
}

# ────────────────────────── 汇总 ──────────────────────────
Section "汇总"
Write-Host ("  断言：{0} 项 · 失败：{1} 项" -f $script:checks, $script:failures)
if ($script:failures -gt 0) {
    Write-Host "  P9 静态验收 = 未通过" -ForegroundColor Red
    exit 1
}

Write-Host "  P9 静态验收 = 通过" -ForegroundColor Green
Write-Host ""
Write-Host "  注：本脚本只覆盖「可自动化」的部分。Playnite 运行时行为（扩展能加载、" -ForegroundColor DarkGray
Write-Host "      设置页渲染、真实游戏起停桥接）必须在装有 Playnite 的机器上人工验证，" -ForegroundColor DarkGray
Write-Host "      见 docs/16-P9完成报告.md 的人工项清单。" -ForegroundColor DarkGray
exit 0
