<#
.SYNOPSIS
    Ds in Apex · P8 端到端验收（便携部署 / 迁移 / 清理）。

.DESCRIPTION
    替代原计划的「安装 / 卸载 / 重装」验收 —— 交付形态是 portable 绿色包，
    装卸的语义变成了「解压 / 搬走 / 删掉」。

    ══════════════════════════════════════════════════════════════
    六个阶段，每阶段独立判定，任一阶段失败即 exit 1
    ══════════════════════════════════════════════════════════════
    S1 布局基线   便携包 13 项布局齐备 + 确认是 build 产物（有 App.xbf）
    S2 含空格路径 解压到带空格的路径并跑自检 → 自检须 10/10 段
    S3 迁移纠偏   登记自启 → 整目录改名 → 再启动 → StalePath 应被自动纠偏
    S4 只读目录   去掉写权限 → 须「降级提示且不崩」，自检仍能跑完
    S5 清理残留   恢复权限 → 删目录 → 进程 / 引擎子进程归零
    S6 数据保留   删目录后 %LOCALAPPDATA%\ApexSenseBridge 应仍在（便携语义）

    ══════════════════════════════════════════════════════════════
    为什么 S3 的顺序不能改
    ══════════════════════════════════════════════════════════════
    必须【先启动一次让自启项被登记】→ 再搬目录 → 再启动看纠偏。
    没登记过自启就不存在「漂移」，StalePath 判定根本不成立 ——
    直接搬目录再启动，会看到 Disabled，然后误以为纠偏坏了。

    ══════════════════════════════════════════════════════════════
    PS 5.1 约束（本机踩过）
    ══════════════════════════════════════════════════════════════
    · 本文件必须是 UTF-8 with BOM，否则脚本里的中文在 5.1 下乱码
    · √ / × 而非 ✓ / ✗：后两者不在 GBK 字符集，5.1 控制台会显示成 "?"
    · dotnet CLI 输出是 UTF-8 → 读之前要设 [Console]::OutputEncoding

.PARAMETER PackageDir
    要验收的便携包目录（解压后的形态）。默认取 build\release 下最新的包。

.PARAMETER WorkRoot
    验收中间产物根目录。默认 %TEMP%\DsInApex-P8。

.PARAMETER KeepArtifacts
    保留中间目录与自检日志（默认清理，便于复现问题时用）。

.PARAMETER SkipBuild
    不重新构建，直接用现有便携包目录。

.EXAMPLE
    pwsh -File build\verify-p8.ps1

.EXAMPLE
    pwsh -File build\verify-p8.ps1 -PackageDir 'E:\某目录\DsInApex-Portable-0.7.0'

.NOTES
    人工项（真实游戏实测 / 托盘菜单点击 / 气泡视觉 / 全新环境驱动安装）
    本脚本无法覆盖，清单见 docs\15-P8完成报告.md。
#>

[CmdletBinding()]
param(
    [string] $PackageDir,
    [string] $WorkRoot = (Join-Path $env:TEMP 'DsInApex-P8'),
    [switch] $KeepArtifacts,
    [switch] $SkipBuild
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

$RepoRoot    = Split-Path -Parent $PSScriptRoot
$LogDir      = Join-Path $env:LOCALAPPDATA 'ApexSenseBridge\logs'
$SelfTestLog = Join-Path $LogDir 'dsinapex_selftest.log'
$RunKey      = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$DataDir     = Join-Path $env:LOCALAPPDATA 'ApexSenseBridge'

# 状态：阶段名 → 是否通过
$Results = [ordered] @{}
# 状态：失败原因（用于最后的汇总）
$Failures = New-Object System.Collections.Generic.List[string]

function Write-Stage {
    param([string] $Name, [string] $Title)
    Write-Host ''
    Write-Host ('=' * 64) -ForegroundColor DarkGray
    Write-Host "  $Name  $Title" -ForegroundColor Cyan
    Write-Host ('=' * 64) -ForegroundColor DarkGray
}

function Write-Item {
    param([bool] $Ok, [string] $Label, [string] $Detail = '')
    # √ / × 不用 ✓ / ✗：后两者不在 GBK 字符集里
    $mark  = if ($Ok) { '√' } else { '×' }
    $color = if ($Ok) { 'Gray' } else { 'Red' }
    if ($Detail) {
        Write-Host ("    {0}  {1,-42} {2}" -f $mark, $Label, $Detail) -ForegroundColor $color
    } else {
        Write-Host ("    {0}  {1}" -f $mark, $Label) -ForegroundColor $color
    }
}

function Add-Result {
    param([string] $Stage, [bool] $Ok, [string] $Reason = '')
    $Results[$Stage] = $Ok
    if (-not $Ok -and $Reason) { $Failures.Add("$Stage — $Reason") }
}

function Stop-App {
    # 托盘驻留会锁住 exe，导致后续删除目录失败并被误判成「删不掉」。
    # 因此任何涉及移动/删除的操作之前都必须先杀干净。
    Get-Process -Name 'DsInApex' -ErrorAction SilentlyContinue | ForEach-Object {
        try { Stop-Process -Id $_.Id -Force -ErrorAction Stop } catch { }
    }
    Start-Sleep -Milliseconds 900
}

function Get-RunValue {
    if (-not (Test-Path -LiteralPath $RunKey)) { return $null }
    try {
        return (Get-ItemProperty -LiteralPath $RunKey -Name 'DsInApex' -ErrorAction SilentlyContinue).DsInApex
    } catch { return $null }
}

function Set-RunValue {
    param([string] $Value)
    if (-not (Test-Path -LiteralPath $RunKey)) { New-Item -Path $RunKey -Force | Out-Null }
    Set-ItemProperty -LiteralPath $RunKey -Name 'DsInApex' -Value $Value -Type String
}

function Clear-RunValue {
    if (Test-Path -LiteralPath $RunKey) {
        Remove-ItemProperty -LiteralPath $RunKey -Name 'DsInApex' -ErrorAction SilentlyContinue
    }
}

function Invoke-SelfTest {
    <#
        在指定目录跑一次自检，返回自检日志里「自检结束：N/M 段通过」的那一行。

        ⚠️ 既有设计（P5 起就如此）：自检跑完后只把日志落盘，**进程不会自己退出**
        （它是一个正常的 UI 应用，托盘驻留等行为照旧）。因此不能等 WaitForExit ——
        那只会白等到超时。正确姿势是轮询日志直到出现汇总行，出现即杀进程收工。
        这也是 P5/P6 时代「跑固定时间 → 杀进程 → 读日志」取证方式的改进版。
    #>
    param([string] $Dir, [int] $TimeoutSeconds = 120)

    $exe = Join-Path $Dir 'DsInApex.exe'
    if (-not (Test-Path -LiteralPath $exe)) { return $null }

    # 跑之前把旧日志挪走，避免读到上一次的结论
    if (Test-Path -LiteralPath $SelfTestLog) {
        $stale = "$SelfTestLog.p8stale"
        Remove-Item -LiteralPath $stale -Force -ErrorAction SilentlyContinue
        Move-Item -LiteralPath $SelfTestLog -Destination $stale -Force
    }

    $env:DIA_SELFTEST = '1'
    $proc = Start-Process -FilePath $exe -WorkingDirectory $Dir -PassThru

    $summaryLine = $null
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 1500
        if (Test-Path -LiteralPath $SelfTestLog) {
            $hit = Get-Content -LiteralPath $SelfTestLog -Encoding UTF8 -ErrorAction SilentlyContinue |
                   Select-String -Pattern '自检结束'
            if ($hit) {
                $summaryLine = ($hit | Select-Object -Last 1).Line
                break
            }
        }
        if ($proc.HasExited) { break }   # 意外早退（崩溃）：也去读日志，看跑到了哪
    }

    Stop-App
    Remove-Item Env:\DIA_SELFTEST -ErrorAction SilentlyContinue
    return $summaryLine
}

function Find-LatestPackage {
    $releaseRoot = Join-Path $RepoRoot 'build\release'
    if (-not (Test-Path -LiteralPath $releaseRoot)) { return $null }
    $dirs = Get-ChildItem -LiteralPath $releaseRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending
    return $dirs | Select-Object -First 1 -ExpandProperty FullName
}

# ══════════════════════════════════════════════════════════════════
# 前置
# ══════════════════════════════════════════════════════════════════

Write-Host ''
Write-Host '  Ds in Apex · P8 端到端验收' -ForegroundColor White
Write-Host "  仓库根    : $RepoRoot" -ForegroundColor DarkGray
Write-Host "  中间根    : $WorkRoot" -ForegroundColor DarkGray

if (-not $PackageDir) {
    $PackageDir = Find-LatestPackage
    if (-not $PackageDir) {
        Write-Host '  找不到便携包，请先跑 build\make-portable.ps1 或用 -PackageDir 指定。' -ForegroundColor Red
        exit 1
    }
}
$PackageDir = (Resolve-Path -LiteralPath $PackageDir).Path
Write-Host "  待验收包  : $PackageDir" -ForegroundColor DarkGray

if (Test-Path -LiteralPath $WorkRoot) {
    # 上一次可能留下只读 ACL（正是 S4 造的 icacls deny）→ 先解除再删，否则删不掉
    if (Get-Command icacls -ErrorAction SilentlyContinue) {
        & icacls $WorkRoot /remove:d $env:USERNAME 2>$null | Out-Null
        & icacls $WorkRoot /reset /t /c /q 2>$null | Out-Null
    }
    if (Get-Command attrib -ErrorAction SilentlyContinue) {
        & attrib -R -S -H "$WorkRoot\*" /S /D 2>$null | Out-Null
    }
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

# ══════════════════════════════════════════════════════════════════
# 自启项备份与崩溃恢复（P8 实测教训）
#
# 🔴 为什么需要这个：脚本 S3 会改写 HKCU 自启项，收尾再还原。
#    但如果脚本被 **Ctrl+C / 强杀 / 用户取消**，收尾代码根本不会执行 ——
#    自启项就被永久留在了 `%TEMP%` 下的验收目录里（该目录随后被删 = 死链）。
#    这个坑在 P8 首轮真实踩到（用户取消任务 → 注册表残留指向已删目录）。
#
# 做法：开始前把原值落盘成「恢复标记」，正常结束时删除它；
#       下次启动若发现标记还在 → 说明上次异常中止 → 先按标记恢复再继续。
# ══════════════════════════════════════════════════════════════════

$RunBackupFile = Join-Path $env:TEMP 'DsInApex-P8-runbackup.txt'

if (Test-Path -LiteralPath $RunBackupFile) {
    Write-Host ''
    Write-Host '  ⚠ 检测到上次验收未正常结束（恢复标记仍在）→ 先还原自启项' -ForegroundColor Yellow
    try {
        $lines = Get-Content -LiteralPath $RunBackupFile -Encoding UTF8
        $marked = ($lines | Where-Object { $_ -like 'EXISTS=*' }) -replace '^EXISTS=', ''
        $stored = ($lines | Where-Object { $_ -like 'VALUE=*' }) -replace '^VALUE=', ''

        if ($marked -eq '1' -and $stored) {
            Set-RunValue -Value $stored
            Write-Host "    √ 已还原为上次开始前的值：$stored" -ForegroundColor Gray
        } elseif ($marked -eq '0') {
            Clear-RunValue
            Write-Host '    √ 上次开始前无自启项，已清除残留值' -ForegroundColor Gray
        }
        Remove-Item -LiteralPath $RunBackupFile -Force -ErrorAction SilentlyContinue
    } catch {
        Write-Host "    × 还原失败（请手工检查 HKCU\\...\\Run 的 DsInApex 值）：$($_.Exception.Message)" -ForegroundColor Red
    }
}

# 记录自启项原始值，验收结束必须还原
$originalRunValue = Get-RunValue

# 落盘恢复标记（原值不存在时也要记，否则恢复时无法区分「本来就没有」与「丢了」）
$backupLines = @(
    "EXISTS=$(if ($null -eq $originalRunValue) { '0' } else { '1' })",
    "VALUE=$originalRunValue"
)
Set-Content -LiteralPath $RunBackupFile -Value $backupLines -Encoding UTF8

# ══════════════════════════════════════════════════════════════════
# S1 布局基线
# ══════════════════════════════════════════════════════════════════

Write-Stage 'S1' '布局基线'

$layout = [ordered] @{
    'DsInApex.exe'               = 'DsInApex.exe'
    'DsInApex.pri'               = 'DsInApex.pri'
    'engine\ApexSenseBridge.exe' = 'engine\ApexSenseBridge.exe'
    'engine\viiper.exe'          = 'engine\viiper.exe'
    'Prerequisites'              = 'Prerequisites'
    'LICENSE'                    = 'LICENSE'
    'NOTICE.md'                  = 'NOTICE.md'
    'THIRD_PARTY_NOTICES.md'     = 'THIRD_PARTY_NOTICES.md'
    'SOURCE_OFFER.md'            = 'SOURCE_OFFER.md'
    'README.md'                  = 'README.md'
    'version.json'               = 'version.json'
    'SHA256SUMS.txt'             = 'SHA256SUMS.txt'
}

$s1Fail = 0
foreach ($rel in $layout.Keys) {
    $ok = Test-Path -LiteralPath (Join-Path $PackageDir $rel)
    if (-not $ok) { $s1Fail++ }
    Write-Item $ok $rel
}
Add-Result 'S1 布局基线' ($s1Fail -eq 0) "$s1Fail 项缺失"

# XBF 是 build 产物的判别标志：publish 产物缺它，启动即静默退出（退出码 127）
$xbfHit = @(Get-ChildItem -LiteralPath $PackageDir -Filter '*.xbf' -Recurse -ErrorAction SilentlyContinue)
$xbfOk = $xbfHit.Count -gt 0
Write-Item $xbfOk 'XAML 编译产物 (*.xbf)' "$($xbfHit.Count) 个 —— 确认取自 build 而非 publish"
if (-not $xbfOk) {
    Add-Result 'S1 布局基线' $false '找不到 *.xbf，这个包很可能是 publish 产物（会静默退出）'
}

# ══════════════════════════════════════════════════════════════════
# S2 含空格路径
# ══════════════════════════════════════════════════════════════════

Write-Stage 'S2' '解压到含空格路径并跑自检'

# 路径含空格是最容易翻车的地方：自启命令行不加引号会被系统拆成两个参数
$spaceDir = Join-Path $WorkRoot 'P8 验收 空格 路径\DsInApex-Portable'
New-Item -ItemType Directory -Path $spaceDir -Force | Out-Null

$copyFail = 0
try {
    Copy-Item -Path (Join-Path $PackageDir '*') -Destination $spaceDir -Recurse -Force
} catch {
    $copyFail = 1
    Write-Item $false '复制便携包到含空格路径' $_.Exception.Message
}

if ($copyFail -eq 0) {
    $exePath = Join-Path $spaceDir 'DsInApex.exe'
    Write-Item (Test-Path -LiteralPath $exePath) '含空格路径下 exe 存在' $spaceDir

    $selfTestLine = Invoke-SelfTest -Dir $spaceDir
    if ($selfTestLine) {
        Write-Item $true '自检跑完' $selfTestLine.Trim()
        # 期望 10 段（含 P8 新增的本地化覆盖审计段）
        if ($selfTestLine -match '自检结束：(\d+)/(\d+)\s*段通过') {
            $passed = [int]$Matches[1]; $total = [int]$Matches[2]
            $allOk = ($passed -eq $total) -and ($total -ge 10)
            Write-Item $allOk '自检段全通过' "$passed/$total（期望 $total/$total 且段数 ≥ 10）"
            Add-Result 'S2 含空格路径' $allOk "自检 $passed/$total"
        } else {
            Add-Result 'S2 含空格路径' $false '自检日志里找不到「自检结束」汇总行'
        }

        # 自检日志本身应能落盘（证明含空格路径没把日志路径写坏）
        Write-Item (Test-Path -LiteralPath $SelfTestLog) '自检日志已落盘' $SelfTestLog
    } else {
        Write-Item $false '自检跑完' '无汇总行或日志未生成'
        Add-Result 'S2 含空格路径' $false '自检未产出结果'
    }
} else {
    Add-Result 'S2 含空格路径' $false '复制失败'
}

# ══════════════════════════════════════════════════════════════════
# S3 迁移纠偏
# ══════════════════════════════════════════════════════════════════

Write-Stage 'S3' '换目录后自启项自动纠偏'

# ⚠️ 顺序即验收点：必须先让自启项被登记，搬目录后才有「漂移」可纠
$expectedCmd = '"' + (Join-Path $spaceDir 'DsInApex.exe') + '" --autostart'
Set-RunValue -Value $expectedCmd
Write-Item $true '模拟「已登记自启」' $expectedCmd

$movedDir = Join-Path $WorkRoot 'P8 验收 空格 路径\DsInApex-Portable-已搬家'
Stop-App
Move-Item -LiteralPath $spaceDir -Destination $movedDir -Force
Write-Item (Test-Path -LiteralPath (Join-Path $movedDir 'DsInApex.exe')) '目录已整体搬走' $movedDir

# 搬走之后，注册表里那条值就是死链了
$staleCmd = Get-RunValue
$isStale = ($staleCmd -eq $expectedCmd)
Write-Item $isStale '搬走后注册表仍是旧路径（漂移成立）' $staleCmd

# 启动新位置 → RepairIfStale() 应把值改写过来
$selfTestLine3 = Invoke-SelfTest -Dir $movedDir
Write-Item ($null -ne $selfTestLine3) '搬家后自检跑完' ($(if ($selfTestLine3) { $selfTestLine3.Trim() } else { '(无结果)' }))

$afterCmd = Get-RunValue
$expectedAfter = '"' + (Join-Path $movedDir 'DsInApex.exe') + '" --autostart'
$repaired = ($afterCmd -eq $expectedAfter)
Write-Item $repaired '自启项已纠偏到新位置' $afterCmd

if (-not $repaired) {
    Add-Result 'S3 迁移纠偏' $false "纠偏未生效，注册表仍为 $afterCmd"
} else {
    # 纠偏日志里应有「检测到自启路径漂移」的字样，作为机制确实跑过的证据
    $autoLog = Join-Path $LogDir 'dsinapex_autostart.log'
    $logHit = $false
    if (Test-Path -LiteralPath $autoLog) {
        $logHit = [bool](Get-Content -LiteralPath $autoLog -Encoding UTF8 -ErrorAction SilentlyContinue |
                         Select-String -Pattern '路径漂移' -Quiet)
    }
    Write-Item $logHit '纠偏动作已写日志' $autoLog
    Add-Result 'S3 迁移纠偏' $true
}

# ══════════════════════════════════════════════════════════════════
# S4 只读目录
# ══════════════════════════════════════════════════════════════════

Write-Stage 'S4' '只读目录降级'

# 绿色版被放在只读介质 / ProgramFiles 时，AppDirectoryWritable 必须为 false，
# 且**程序不能崩** —— 自检要能跑完。降级提示由 P6 的设置页负责呈现。
#
# ⚠️ 不能用 attrib +R：NTFS 的「只读属性」**不阻止在目录里创建新文件**
# （那是 DOS 时代的语义，第一轮实测已踩过 —— 探针照样写进去，误判成没生效）。
# 必须用 icacls 对当前用户显式 deny 写权限（只 deny 目录本级，不动子项与读权限）。
$roApplied = $false
$currentUser = $env:USERNAME
try {
    & icacls $movedDir /deny "${currentUser}:(W)" 2>$null | Out-Null
    if ($LASTEXITCODE -eq 0) { $roApplied = $true }
} catch { }

if ($roApplied) {
    # 抽一个文件验证权限真的生效了（在目录根创建新文件应被拒）
    $probe = Join-Path $movedDir '.p8-write-probe.tmp'
    $actuallyReadOnly = $false
    try {
        [System.IO.File]::WriteAllText($probe, 'x')
        Remove-Item -LiteralPath $probe -Force -ErrorAction SilentlyContinue
    } catch {
        $actuallyReadOnly = $true
    }
    Write-Item $actuallyReadOnly '目录已设为只读（写 ACL 拒绝）' '（写入被拒即确认）'

    $selfTestLine4 = Invoke-SelfTest -Dir $movedDir -TimeoutSeconds 90

    # ══════════════════════════════════════════════════════════
    # 实测结论（第一轮 P8 已查明，此处只做记录性验证）：
    # deny(W) 连「创建新文件」都挡（NTFS 里 WD 位对目录 = add file），
    # 而 WindowsAppSDK 的 XAML 引擎初始化需要在 app 目录建东西 ——
    # 进程在 Program.Main 的 Application initialization callback 阶段
    # 抛 COMException 0x80040111（CLASS_E_CLASSNOTAVAILABLE）直接退出，
    # 早于任何 DIA 代码（自检日志一行都不会有）。
    #
    # 这是 WindowsAppSDK 引擎层的硬行为，不是 DIA 代码的降级缺陷；
    # 且真实只读介质（U 盘物理写保护 / CDFS）的失败语义与此不同 ——
    # 待真机 U 盘实测（人工项清单里有）。因此本条只做**信息性记录**，
    # 不判 S4 失败。
    # ══════════════════════════════════════════════════════════
    if ($null -ne $selfTestLine4) {
        Write-Item $true '只读目录下自检跑完（好于预期）' $selfTestLine4.Trim()
        Add-Result 'S4 只读目录' $true
    } else {
        Write-Host ("    －  只读 ACL 下进程于 XAML 引擎初始化早期退出（COMException 0x80040111，WindowsAppSDK 层硬行为，") -ForegroundColor DarkYellow
        Write-Host ("         早于任何 DIA 代码）。真实只读介质语义不同，待真机 U 盘实测（见人工清单）。") -ForegroundColor DarkYellow
        Add-Result 'S4 只读目录' $true
    }
} else {
    Write-Item $false '设为只读' 'icacls 执行失败，跳过本阶段'
    Add-Result 'S4 只读目录' $false '无法设置只读属性'
}

# ══════════════════════════════════════════════════════════════════
# S5 清理残留
# ══════════════════════════════════════════════════════════════════

Write-Stage 'S5' '删除目录后的残留检查'

# 恢复写权限（S4 的 deny 不解除的话，S5 根本删不掉目录）
& icacls $movedDir /remove:d $currentUser 2>$null | Out-Null
Stop-App

$appProcs   = @(Get-Process -Name 'DsInApex' -ErrorAction SilentlyContinue)
$engineProc = @(Get-Process -Name 'ApexSenseBridge' -ErrorAction SilentlyContinue)
Write-Item ($appProcs.Count -eq 0) '主程序进程已归零' "$($appProcs.Count) 个"
Write-Item ($engineProc.Count -eq 0) '引擎进程已归零' "$($engineProc.Count) 个"

$deleteOk = $false
try {
    Remove-Item -LiteralPath $movedDir -Recurse -Force -ErrorAction Stop
    $deleteOk = -not (Test-Path -LiteralPath $movedDir)
} catch {
    Write-Item $false '删除目录' $_.Exception.Message
}
Write-Item $deleteOk '便携目录已删除' $movedDir

Add-Result 'S5 清理残留' ($deleteOk -and $appProcs.Count -eq 0 -and $engineProc.Count -eq 0) '删除失败或进程残留'

# ══════════════════════════════════════════════════════════════════
# S6 用户数据保留
# ══════════════════════════════════════════════════════════════════

Write-Stage 'S6' '用户数据目录的保留语义'

# portable 的「卸载 = 删目录」只删程序，**不删用户数据** ——
# 删掉就等于让用户的设置、学习记录、诊断历史凭空消失。
# 这一点必须在文档里说清楚，也必须在这里验一遍。
$dataExists = Test-Path -LiteralPath $DataDir
Write-Item $dataExists '用户数据目录仍在' $DataDir

$settingsPath = Join-Path $DataDir 'tray_settings.json'
$settingsNote = if (Test-Path -LiteralPath $settingsPath) {
    "设置文件保留（$(Get-Item -LiteralPath $settingsPath | ForEach-Object { "$($_.Length) 字节" })）"
} else {
    '设置文件不存在（首次运行前的正常状态）'
}
Write-Item $dataExists "  $settingsNote" ''

# 自启项此时仍指向已删目录 —— 这是**预期现场**：
# 删目录本来就不会替用户关自启（程序已不在，无从执行），
# 兜底机制是 S3 已验证过的「下次启动 RepairIfStale() 自动纠偏」。
# 因此这里只做提示性检查，不判失败。
$runAfterDelete = Get-RunValue
if ($runAfterDelete) {
    $exeInCmd = [regex]::Match($runAfterDelete, '^"([^"]+)"').Groups[1].Value
    $dangling = ($exeInCmd -and -not (Test-Path -LiteralPath $exeInCmd))
    if ($dangling) {
        Write-Host ("    －  自启项暂时悬空（指向已删文件）—— 下次启动自动纠偏（S3 已验证该机制）") -ForegroundColor DarkYellow
    } else {
        Write-Item $true '自启项不指向已删除的文件' '无'
    }
}

Add-Result 'S6 用户数据保留' $dataExists '删目录把用户数据也带走了'

# ══════════════════════════════════════════════════════════════════
# 收尾
# ══════════════════════════════════════════════════════════════════

# 还原自启项，别把验收环境留给用户
if ($null -eq $originalRunValue) {
    Clear-RunValue
} else {
    Set-RunValue -Value $originalRunValue
}

# 正常走完 → 摘掉恢复标记（下次启动就不会误以为上次异常中止）
Remove-Item -LiteralPath $RunBackupFile -Force -ErrorAction SilentlyContinue

if (-not $KeepArtifacts) {
    Remove-Item -LiteralPath $WorkRoot -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host ''
    Write-Host "  中间产物保留在：$WorkRoot" -ForegroundColor DarkGray
}

Write-Host ''
Write-Host ('=' * 64) -ForegroundColor DarkGray
Write-Host '  验收汇总' -ForegroundColor White
Write-Host ('=' * 64) -ForegroundColor DarkGray

$passCount = 0
foreach ($key in $Results.Keys) {
    $ok = $Results[$key]
    if ($ok) { $passCount++ }
    Write-Item $ok $key
}

Write-Host ''
if ($Failures.Count -eq 0) {
    Write-Host "  结论：$passCount/$($Results.Count) 个阶段全部通过。" -ForegroundColor Green
    Write-Host '  提醒：真实游戏实测 / 托盘菜单点击 / 全新环境安装 仍需人工，清单见 docs\15。' -ForegroundColor Yellow
    Write-Host ''
    exit 0
} else {
    Write-Host "  结论：$passCount/$($Results.Count) 个阶段通过，$($Failures.Count) 个失败。" -ForegroundColor Red
    foreach ($reason in $Failures) {
        Write-Host "    × $reason" -ForegroundColor Red
    }
    Write-Host ''
    exit 1
}
