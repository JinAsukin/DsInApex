<#
.SYNOPSIS
    采集 APEX 4 在两种 USB 身份下的 `ApexSenseBridge.exe identify` 原始输出。

.DESCRIPTION
    上游 issue #26（ReynArts/ApexSenseBridge）的维护者要求：
    「在新的测试构建里，两种状态下各跑一次 identify 并把两份输出贴回来」。

    两种状态：
      · 降级 32 字节接口 —— 产品名报 Flydigi VADER3，LT 与震动可用、RT 不可用；
      · 完整 64 字节接口 —— 产品名报 Flydigi APEX 4，左右扳机自适应都可用。
    身份切换靠【物理连接】，软件侧触发不了：**接收器（dongle）在位的同时用有线接入**
    （或先有线再插接收器）才会进入完整身份。

    本脚本每 2 秒跑一次 identify，把「扳机行取值」不同的状态各留一份原文，
    集齐两种（或超时）即止。运行期间你只要按提示插拔/切换连接即可。

.PARAMETER EnginePath
    引擎可执行文件路径。省略时在 vendor\portable-* 下按**版本号降序**自动挑选最新的那个。

.PARAMETER Seconds
    最长观察秒数（默认 300）。集齐两种身份会提前结束。

.PARAMETER OutputPath
    报告落盘路径。默认落在 %TEMP%\DsInApex-identify-states.txt。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\capture-identify-states.ps1

.NOTES
    本脚本只跑只读命令 identify（身份交换），不写任何硬件状态、不启停桥接。
    ⚠️ 本文件必须是 UTF-8 with BOM —— PS 5.1 对无 BOM 的 UTF-8 脚本会解析失败。
#>
[CmdletBinding()]
param(
    [string] $EnginePath,
    [int] $Seconds = 300,
    [string] $OutputPath
)

$ErrorActionPreference = 'Continue'

# ⚠️ 刻意【不设】'Stop'：本脚本要反复调用原生 exe，而 PS 5.1 在
#    $ErrorActionPreference='Stop' 下会把 `原生命令 2>&1` 的 stderr 记录
#    升级成**终止性错误**（NativeCommandError），一次身份校验失败就把整个脚本打死。
#    （首版就是这么挂的：engine identify 报身份校验失败 → 脚本 exit 1，什么都没采到。）
#    这里改用"自己判退出码"的方式，把所有失败都当数据看。

# ────────────────────────── 工具函数 ──────────────────────────

function Write-Head([string] $text) {
    Write-Host ''
    Write-Host "── $text " -ForegroundColor Cyan -NoNewline
    Write-Host ('─' * [Math]::Max(1, 58 - $text.Length)) -ForegroundColor DarkCyan
}

# 把 portable-<版本> 目录名换成可排序的键。
# 注意：不能直接字符串比较 —— "beta.9" 会排在 "beta.10" 之后（'9' > '1'）。
function Get-PortableSortKey([string] $folderName) {
    $v = $folderName -replace '^portable-', ''
    $tag = ''
    if ($v.Contains('-')) {
        $i = $v.IndexOf('-')
        $tag = $v.Substring($i + 1)
        $v = $v.Substring(0, $i)
    }

    $nums = @($v.Split('.') | ForEach-Object { '{0:D5}' -f [int]$_ })
    while ($nums.Count -lt 4) { $nums += '00000' }

    # 正式版排在任何预发布之前；预发布按标签里的最后一段数字比大小
    $pre = 99999
    if ($tag.Length -gt 0) {
        $pre = 0
        foreach ($seg in $tag.Split('.')) {
            if ($seg -match '^\d+$') { $pre = [int]$seg }
        }
    }

    return (($nums -join '.') + '-' + ('{0:D5}' -f $pre))
}

function Resolve-EnginePath {
    param([string] $root)

    $vendor = Join-Path $root 'vendor'
    if (-not (Test-Path -LiteralPath $vendor)) { return $null }

    $dirs = @(Get-ChildItem -LiteralPath $vendor -Directory -Filter 'portable-*' -ErrorAction SilentlyContinue)
    if ($dirs.Count -eq 0) { return $null }

    $ordered = $dirs | Sort-Object -Property @{ Expression = { Get-PortableSortKey $_.Name } } -Descending
    foreach ($d in $ordered) {
        $candidate = Join-Path $d.FullName 'ApexSenseBridge-Portable\ApexSenseBridge.exe'
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    return $null
}

# 从 identify 输出里取「扳机行」的取值，并归类。
# 引擎 1.0.0-beta.10 起有三种取值（partial / yes / no），更早只有 yes / no。
function Get-TriggerState([string] $text) {
    $m = [regex]::Match($text, '(?mi)^Adaptive triggers:\s*(?<v>[^\r\n]*)')
    if (-not $m.Success) { return 'Unknown' }

    $v = $m.Groups['v'].Value.Trim()
    if ($v -like 'partial*') { return 'Partial' }
    if ($v -like 'yes*') { return 'Full' }
    if ($v -like 'no*') { return 'None' }
    return "Unknown($v)"
}

# 只读命令遇设备占用会失败（退出码 2）—— 隔 1 秒重试一次即可；写硬件命令绝不重试。
function Invoke-IdentifyOnce([string] $engine) {
    for ($attempt = 1; $attempt -le 2; $attempt++) {
        $out = & $engine identify 2>&1 | Out-String
        $code = $LASTEXITCODE
        if ($code -eq 0) { return @{ Code = 0; Text = $out.Trim() } }
        if ($attempt -eq 1) { Start-Sleep -Seconds 1 }
    }
    return @{ Code = $code; Text = $out.Trim() }
}

# ────────────────────────── 主流程 ──────────────────────────

$root = Split-Path -Parent $PSScriptRoot      # tools\ 的上一级 = 仓库根
if ([string]::IsNullOrWhiteSpace($root)) { $root = (Get-Location).Path }

if ([string]::IsNullOrWhiteSpace($EnginePath)) {
    $EnginePath = Resolve-EnginePath -root $root
}

if ([string]::IsNullOrWhiteSpace($EnginePath) -or -not (Test-Path -LiteralPath $EnginePath)) {
    Write-Host '找不到引擎可执行文件。' -ForegroundColor Red
    Write-Host '请把上游 Portable 包解压到 vendor\portable-<版本>\ApexSenseBridge-Portable\，' -ForegroundColor Yellow
    Write-Host '或用 -EnginePath 显式指定 ApexSenseBridge.exe。' -ForegroundColor Yellow
    exit 1
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $env:TEMP 'DsInApex-identify-states.txt'
}

Write-Head 'APEX 4 双身份 identify 采集'
Write-Host "  引擎        : $EnginePath" -ForegroundColor Gray

# ⚠️ 不能用 (Select-String ...).Matches.Groups[1] —— 没匹配到时对空集合取 .Groups 会抛异常，
#    而脚本开头把 $ErrorActionPreference 设成了 Stop，会直接把整个脚本打死。
$engineVersion = ''
$helpText = (& $EnginePath help 2>&1 | Out-String)
$versionMatch = [regex]::Match($helpText, '(?m)^Release:\s*(.+)$')
if ($versionMatch.Success) { $engineVersion = $versionMatch.Groups[1].Value.Trim() }
else {
    $looseMatch = [regex]::Match($helpText, '(?m)ApexSenseBridge\s+([0-9][0-9A-Za-z\.\-]*)')
    if ($looseMatch.Success) { $engineVersion = $looseMatch.Groups[1].Value.Trim() }
}

if ($engineVersion) { Write-Host "  引擎自报版本: $engineVersion" -ForegroundColor Gray }
else { Write-Host '  引擎自报版本: (未能从 help 输出中取到)' -ForegroundColor DarkYellow }
Write-Host "  最长观察    : $Seconds 秒（集齐两种身份会提前结束）" -ForegroundColor Gray
Write-Host ''
Write-Host '  请按下面的顺序操作（软件无法替你切换）：' -ForegroundColor White
Write-Host '   1) 先保持当前连接不动，等它把「降级态」记下来；' -ForegroundColor White
Write-Host '   2) 然后【在接收器保持插着的同时，用 USB 线把手柄接到电脑】；' -ForegroundColor White
Write-Host '   3) 等到出现「完整态」即自动结束。' -ForegroundColor White
Write-Host ''

$states = [ordered] @{}
$deadline = (Get-Date).AddSeconds($Seconds)
$tick = 0

while ((Get-Date) -lt $deadline -and $states.Count -lt 2) {
    $tick++
    $result = Invoke-IdentifyOnce -engine $EnginePath

    if ($result.Code -ne 0) {
        # 退出码非 0 时**不记录**：可能是没插手柄 / 设备被占用，
        # 把这种输出当成一种"身份"会让报告里出现假状态。
        $reason = if ($result.Text.Length -gt 0) { $result.Text } else { '(无输出)' }
        Write-Host ("  [{0:HH:mm:ss}] identify 退出码 {1} —— 未记录：{2}" -f (Get-Date), $result.Code, $reason) -ForegroundColor DarkYellow
    }
    else {
        $state = Get-TriggerState -text $result.Text
        $stamp = '{0:HH:mm:ss}' -f (Get-Date)

        if (-not $states.Contains($state)) {
            $states[$state] = @{ At = $stamp; Text = $result.Text; Code = $result.Code }
            $color = if ($state -eq 'Full') { 'Green' } elseif ($state -eq 'Partial') { 'Yellow' } else { 'Gray' }
            Write-Host "  [$stamp] 首次捕获状态：$state" -ForegroundColor $color
            Write-Host '             （原文已留存）' -ForegroundColor DarkGray
        }
        else {
            Write-Host "  [$stamp] 状态仍是 $state（第 $tick 次采样）" -ForegroundColor DarkGray
        }
    }

    if ($states.Count -lt 2) { Start-Sleep -Seconds 2 }
}

# ────────────────────────── 出报告 ──────────────────────────

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('APEX 4 · identify 双身份输出采集报告')
$lines.Add('采集时间 : ' + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'))
$lines.Add('引擎     : ' + $EnginePath)
if ($engineVersion) { $lines.Add('引擎版本 : ' + $engineVersion) }
$lines.Add('')

$expected = @('Partial', 'Full')
$missing = @($expected | Where-Object { -not $states.Contains($_) })

$lines.Add('捕获到的状态：' + ($(if ($states.Count -gt 0) { ($states.Keys -join ', ') } else { '(无)' })))
if ($missing.Count -gt 0) {
    $lines.Add('仍缺状态     ：' + ($missing -join ', ') +
               '  ← 请确认是不是漏了「接收器在位 + 有线接入」这一步')
}
$lines.Add('')
$lines.Add('=' * 70)

foreach ($state in $states.Keys) {
    $entry = $states[$state]
    $lines.Add('')
    $lines.Add("【$state】捕获于 $($entry.At)（identify 退出码 $($entry.Code)）")
    $lines.Add('-' * 70)
    $lines.Add($entry.Text)
    $lines.Add('-' * 70)
}

if ($states.Count -lt 2) {
    $lines.Add('')
    $lines.Add('注：只捕获到一种状态。完整 64 字节身份需要【接收器在位的同时用有线接入】，')
    $lines.Add('    改连接方式后重跑本脚本即可补齐（跑一次约 10 秒就能确认状态）。')
}

$utf8Bom = New-Object System.Text.UTF8Encoding($true)
[System.IO.File]::WriteAllText($OutputPath, ($lines -join "`r`n"), $utf8Bom)

Write-Head '结果'
Write-Host "  捕获状态数  : $($states.Count) / 2" -ForegroundColor $(if ($states.Count -ge 2) { 'Green' } else { 'Yellow' })
Write-Host "  报告已写入  : $OutputPath" -ForegroundColor Cyan
if ($missing.Count -gt 0) {
    Write-Host ("  仍缺        : " + ($missing -join ', ')) -ForegroundColor Yellow
    Write-Host '  提示        : 请在【接收器保持插着】的同时用 USB 线连手柄，然后重跑本脚本。' -ForegroundColor Yellow
}
Write-Host ''
Write-Host '  把报告里两个【】段落之间的原文直接贴到上游 issue #26 即可。' -ForegroundColor White

exit 0
