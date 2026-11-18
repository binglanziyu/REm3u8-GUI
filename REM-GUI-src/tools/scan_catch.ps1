# =====================================================================
#  tools\scan_catch.ps1 - 无痕 catch 人工门禁（重构方案第 4 章 E5）
#
#  扫描 csharp\ 下全部 .cs，找出"捕获了异常但块内没有任何记录/重抛"的 catch 块。
#  无痕 catch 会让故障静默消失，本脚本把这条约束固化成可执行门禁。
#
#  用法：
#    powershell -ExecutionPolicy Bypass -File tools\scan_catch.ps1
#    powershell -ExecutionPolicy Bypass -File tools\scan_catch.ps1 -ShowDetail
#
#  退出码：0 = 无无痕 catch；1 = 存在无痕 catch
#  留痕口径：块正文出现 Note.Swallow / Logger.Diag / Diag( / Debug.WriteLine / log. / throw
#  例外：Interop\Native.cs 的 P/Invoke 能力探测已在方案中单独登记
# =====================================================================
[CmdletBinding()]
param(
    [string]$Root = "",
    [switch]$ShowDetail
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrEmpty($Root)) {
    $Root = Join-Path (Split-Path -Parent $PSScriptRoot) 'csharp'
}
if (-not (Test-Path -LiteralPath $Root)) {
    Write-Host "找不到源码目录：$Root"
    exit 1
}

$markers = @('Note.Swallow', 'Note.Diag', 'Logger.Diag', 'Diag(', 'Debug.WriteLine', 'log.', 'throw')
$total = 0
$rawTotal = 0
$blindList = @()

$files = Get-ChildItem -LiteralPath $Root -Filter *.cs -Recurse -File | Sort-Object FullName
foreach ($f in $files) {
    $lines = [System.IO.File]::ReadAllLines($f.FullName, [System.Text.Encoding]::UTF8)
    $rel = $f.FullName.Substring($Root.Length).TrimStart([char]'\')

    $rawTotal += ([regex]::Matches([System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8), 'catch\s*[\(\{]')).Count
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $ln = $lines[$i]
        if ($ln -notmatch 'catch\s*[\(\{]') { continue }
        if ($ln -match '^\s*//') { continue }

        $total += ([regex]::Matches($ln, 'catch\s*[\(\{]')).Count
        # 逐行累积 catch 块正文，直到大括号配平
        $depth = 0
        $body = New-Object System.Text.StringBuilder
        $j = $i
        $opened = $false
        while ($j -lt $lines.Length) {
            $cur = $lines[$j]
            foreach ($ch in $cur.ToCharArray()) {
                if ($ch -eq [char]123) { $depth++; $opened = $true }
                elseif ($ch -eq [char]125) { $depth-- }
            }
            if ($opened) { [void]$body.AppendLine($cur) }
            if ($opened -and $depth -le 0) { break }
            if (($j - $i) -gt 400) { break }
            $j++
        }

        $text = $body.ToString()
        $hasTrace = $false
        foreach ($mk in $markers) {
            if ($text.Contains($mk)) { $hasTrace = $true; break }
        }
        if (-not $hasTrace) {
            $blindList += ($rel + ' : 第 ' + ($i + 1) + ' 行')
        }
        $i = $j
    }
}

Write-Host ('catch 关键字：{0}    独立扫描块：{1}    无痕 catch：{2}' -f $rawTotal, $total, $blindList.Count)
if ($blindList.Count -gt 0) {
    if ($ShowDetail) {
        foreach ($b in $blindList) { Write-Host ('  [无痕] ' + $b) }
    }
    Write-Host '存在无痕 catch：请补 Note.Swallow(ex) 或明确重抛后再提交。'
    exit 1
}
Write-Host '检查通过：没有无痕 catch。'
exit 0
