# =====================================================================
#  smoke_test.ps1 — 冷启动 / 内存 / DPI / 目录残留 冒烟测试
#  用途：不进入界面交互，只验证 exe 能否起来、窗口多大、空闲是否空转、
#        程序目录有没有被写出多余文件。发布前跑一遍即可。
#  用法：powershell -ExecutionPolicy Bypass -File tools\smoke_test.ps1
#        默认测 ..\dist\REM-GUI.exe，截图落到 %TEMP%，不污染仓库
# =====================================================================
param(
    [string]$Exe = '',
    [string]$Shot = '',
    [int]$HoldMs = 2500
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $root 'dist'))) { $root = Split-Path -Parent $root }
if (-not $Exe)  { $Exe  = Join-Path $root 'dist\REM-GUI.exe' }
if (-not $Shot) { $Shot = Join-Path $env:TEMP 'rem_gui_smoke.png' }

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class W {
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("shcore.dll")] public static extern int GetProcessDpiAwareness(IntPtr h, out int v);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
}
"@

if (-not (Test-Path -LiteralPath $Exe)) { throw "找不到 $Exe" }
$exeDir = Split-Path -Parent $Exe
$before = @(Get-ChildItem -LiteralPath $exeDir -Force | Select-Object -ExpandProperty Name)

Get-Process -Name 'REM-GUI' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$sw = [System.Diagnostics.Stopwatch]::StartNew()
$p = Start-Process -FilePath $Exe -PassThru
$hwnd = [IntPtr]::Zero
$visibleMs = -1
while ($sw.ElapsedMilliseconds -lt 8000) {
    $p.Refresh()
    if ($p.HasExited) { break }
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
        $hwnd = $p.MainWindowHandle
        $visibleMs = $sw.ElapsedMilliseconds
        break
    }
    Start-Sleep -Milliseconds 10
}
$sw.Stop()

if ($visibleMs -lt 0) { throw "窗口 8 秒内未出现（进程退出：$($p.HasExited)）" }

Start-Sleep -Milliseconds $HoldMs
$p.Refresh()
$dpi = -1
$null = [W]::GetProcessDpiAwareness($p.Handle, [ref]$dpi)
$r = New-Object W+RECT
$null = [W]::GetWindowRect($hwnd, [ref]$r)
$w = $r.R - $r.L
$h = $r.B - $r.T

$dirAfter = @(Get-ChildItem -LiteralPath $exeDir -Force | Select-Object -ExpandProperty Name)

# 截图
try {
    $null = [W]::SetForegroundWindow($hwnd)
    Start-Sleep -Milliseconds 400
    $bmp = New-Object System.Drawing.Bitmap($w, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.L, $r.T, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    $bmp.Save($Shot, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    $shotOk = Test-Path -LiteralPath $Shot
} catch { $shotOk = $false }

$cpu0 = $p.TotalProcessorTime.TotalMilliseconds
Start-Sleep -Milliseconds 1000
$p.Refresh()
$cpu1 = $p.TotalProcessorTime.TotalMilliseconds
$idleCpu = ($cpu1 - $cpu0)

$diag = Join-Path $env:LOCALAPPDATA 'REM-GUI\diag.log'
$diagTail = ''
if (Test-Path -LiteralPath $diag) {
    $diagTail = (Get-Content -LiteralPath $diag -Encoding UTF8 -Tail 8) -join "`n"
}
$guiLogs = Join-Path $env:LOCALAPPDATA 'REM-GUI\logs'

$result = [ordered]@{
    'exe'              = $Exe
    '窗口可见_ms'      = $visibleMs
    'WorkingSet_MB'    = [math]::Round($p.WorkingSet64 / 1MB, 1)
    'Private_MB'       = [math]::Round($p.PrivateMemorySize64 / 1MB, 1)
    'PeakWorkingSet_MB'= [math]::Round($p.PeakWorkingSet64 / 1MB, 1)
    '空闲CPU_ms每秒'   = [math]::Round($idleCpu, 1)
    'DPI感知取值'      = $dpi
    '窗口像素'         = "$w x $h"
    '控制台句柄'       = $hwnd.ToString()
    '截图'             = $shotOk
    'exe目录_前'       = ($before -join ', ')
    'exe目录_后'       = ($dirAfter -join ', ')
    '数据目录存在'     = (Test-Path -LiteralPath $guiLogs)
    'diag尾部'         = $diagTail
}

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
$result.GetEnumerator() | ForEach-Object { '{0,-18}{1}' -f ($_.Key + ' :'), $_.Value }
