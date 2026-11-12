# =====================================================================
#  e2e_test.ps1 — 端到端冒烟：注入 m3u8 链接 → 点开始下载 → 采样进度 → 校验产物
#  日志渲染验证：RichEdit 不支持 GetWindowText 跨进程取文本（返回空），
#                改用 WM_GETTEXTLENGTH / EM_GETLINECOUNT 取值型消息，
#                结束时用 EM_SETSEL + WM_COPY 经剪贴板取全文（取后恢复剪贴板）。
#  进度口径断言：不允许出现「已完成 < 总数 却显示 100.0%」。
#  用法：powershell -ExecutionPolicy Bypass -File tools\e2e_test.ps1
#        默认测 ..\dist\REM-GUI.exe，产物落到 %TEMP%\rem_gui_e2e_out，不污染仓库
# =====================================================================
param(
    [string]$Exe = '',
    [string]$Url = 'https://test-streams.mux.dev/test_001/stream_110k_48k_416x234.m3u8',
    [string]$OutDir = '',
    [int]$TimeoutSec = 240
)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path -LiteralPath (Join-Path $root 'dist'))) { $root = Split-Path -Parent $root }
if (-not $Exe)    { $Exe    = Join-Path $root 'dist\REM-GUI.exe' }
if (-not $OutDir) { $OutDir = Join-Path $env:TEMP 'rem_gui_e2e_out' }

Add-Type @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class UI {
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc cb, IntPtr p);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, string l);
    [DllImport("user32.dll")] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr h, IntPtr p);

    public static List<IntPtr> Kids(IntPtr root) {
        List<IntPtr> list = new List<IntPtr>();
        EnumChildWindows(root, delegate(IntPtr h, IntPtr p) { list.Add(h); return true; }, IntPtr.Zero);
        return list;
    }
    public static string Text(IntPtr h) {
        StringBuilder sb = new StringBuilder(8192);
        GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }
    public static string Cls(IntPtr h) {
        StringBuilder sb = new StringBuilder(256);
        GetClassNameW(h, sb, sb.Capacity);
        return sb.ToString();
    }
    public static RECT Rect(IntPtr h) { RECT r; GetWindowRect(h, out r); return r; }
    public static void SetText(IntPtr h, string s) { SendMessageW(h, 0x000C, IntPtr.Zero, s); }
    public static void Click(IntPtr h) { SendMessageW(h, 0x00F5, IntPtr.Zero, IntPtr.Zero); }
    public static bool Visible(IntPtr h) { return IsWindowVisible(h); }

    // ---- 取值型消息（返回值，不涉及跨进程指针封送，所以对 RichEdit 有效） ----
    public static long Num(IntPtr h, uint msg) { return (long)SendMessageW(h, msg, IntPtr.Zero, IntPtr.Zero); }
    public static long TextLength(IntPtr h) { return Num(h, 0x000E); }   // WM_GETTEXTLENGTH
    public static long LineCount(IntPtr h) { return Num(h, 0x00BA); }    // EM_GETLINECOUNT
    public static long FirstVisibleLine(IntPtr h) { return Num(h, 0x00CE); } // EM_GETFIRSTVISIBLELINE
    public static void SelectAll(IntPtr h) { SendMessageW(h, 0x00B1, IntPtr.Zero, (IntPtr)(-1)); } // EM_SETSEL
    public static void CopyToClipboard(IntPtr h) { SendMessageW(h, 0x0301, IntPtr.Zero, IntPtr.Zero); } // WM_COPY
}
'@

function Get-LogFullText([IntPtr]$h) {
    $old = $null
    try { $old = Get-Clipboard -Raw -ErrorAction Stop } catch { }
    Set-Clipboard -Value ' ' -ErrorAction SilentlyContinue
    [UI]::SelectAll($h)
    Start-Sleep -Milliseconds 80
    [UI]::CopyToClipboard($h)
    Start-Sleep -Milliseconds 150
    $t = ''
    try { $t = Get-Clipboard -Raw -ErrorAction SilentlyContinue } catch { }
    try {
        if ($null -ne $old -and $old.Length -gt 0) { Set-Clipboard -Value $old -ErrorAction SilentlyContinue }
        else { Set-Clipboard -Value ' ' -ErrorAction SilentlyContinue }
    } catch { }
    if ($null -eq $t) { return '' }
    return $t
}

Get-Process -Name 'REM-GUI' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# --- 配置：下载目录固定到测试目录，避免污染用户目录 ---
$cfgDir = Join-Path $env:LOCALAPPDATA 'REM-GUI'
if (-not (Test-Path -LiteralPath $cfgDir)) { New-Item -ItemType Directory -Path $cfgDir -Force | Out-Null }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }
$cfgPath = Join-Path $cfgDir 'config.json'
$json = '{"saveDir":"' + $OutDir.Replace('\', '\\') + '","autoSelect":true}'
[IO.File]::WriteAllText($cfgPath, $json, (New-Object Text.UTF8Encoding($false)))

$p = Start-Process -FilePath $Exe -PassThru
$hwnd = [IntPtr]::Zero
$sw = [Diagnostics.Stopwatch]::StartNew()
while ($sw.ElapsedMilliseconds -lt 8000) {
    $p.Refresh()
    if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $p.MainWindowHandle; break }
    Start-Sleep -Milliseconds 10
}
if ($hwnd -eq [IntPtr]::Zero) { throw "主窗口未出现" }
Start-Sleep -Milliseconds 1200

$kids = [UI]::Kids($hwnd)
"控件数：$($kids.Count)"
$edit = [IntPtr]::Zero
$btn = [IntPtr]::Zero
$logHwnd = [IntPtr]::Zero
foreach ($h in $kids) {
    $c = [UI]::Cls($h)
    $t = [UI]::Text($h)
    if ($edit -eq [IntPtr]::Zero -and $c -like '*EDIT*' -and $c -notlike '*RICHEDIT*') {
        $r = [UI]::Rect($h)
        if (($r.R - $r.L) -gt 300) { $edit = $h }
    }
    if ($btn -eq [IntPtr]::Zero -and $c -like '*BUTTON*' -and $t -eq '开始下载') { $btn = $h }
    if ($logHwnd -eq [IntPtr]::Zero -and ($c -like '*RICHEDIT*' -or $c -like '*RichEdit*')) { $logHwnd = $h }
}
"URL 输入框：$edit   开始按钮：$btn   日志控件：$logHwnd"
if ($edit -eq [IntPtr]::Zero -or $btn -eq [IntPtr]::Zero) { throw "找不到输入框或开始按钮" }
if ($logHwnd -eq [IntPtr]::Zero) { throw "找不到日志 RichEdit 控件" }

# --- 启动前基线：日志控件应已有启动日志 ---
$baseLines = [UI]::LineCount($logHwnd)
$baseChars = [UI]::TextLength($logHwnd)
"日志基线：行=$baseLines  字符=$baseChars"

[UI]::SetText($edit, $Url)
Start-Sleep -Milliseconds 250
"注入后输入框内容：" + [UI]::Text($edit)
[UI]::Click($btn)
Start-Sleep -Milliseconds 400

$sw2 = [Diagnostics.Stopwatch]::StartNew()
$lastStatus = ''
$samples = @()
$progSeen = New-Object System.Collections.ArrayList
$done = $false
$logPeakLines = 0
$logPeakChars = 0
while ($sw2.Elapsed.TotalSeconds -lt $TimeoutSec) {
    Start-Sleep -Milliseconds 1500
    $p.Refresh()
    if ($p.HasExited) { "!! 进程意外退出"; break }
    $logLines = [UI]::LineCount($logHwnd)
    $logChars = [UI]::TextLength($logHwnd)
    if ($logLines -gt $logPeakLines) { $logPeakLines = $logLines }
    if ($logChars -gt $logPeakChars) { $logPeakChars = $logChars }
    $statusText = ''
    $progText = ''
    foreach ($h in $kids) {
        $c = [UI]::Cls($h)
        if ($c -like '*STATIC*') {
            $t = [UI]::Text($h)
            if ($t -match '下载中|完成|失败|就绪|停止|预检|解析|合并|已取消|错误') { $statusText = $t }
            if ($t -match '^\s*\d+\s*/\s*\d+' -or $t -match '^\s*\d+\s*/\s*\?') { $progText = $t }
        }
    }
    if ($progText.Length -gt 0 -and -not $progSeen.Contains($progText)) { [void]$progSeen.Add($progText) }
    $re = @(Get-Process -Name 'N_m3u8DL-RE' -ErrorAction SilentlyContinue)
    $line = "{0,5:0.0}s  状态[{1}]  日志行={2} 字符={3}  RE进程={4}  进度[{5}]" -f $sw2.Elapsed.TotalSeconds, $statusText, $logLines, $logChars, $re.Count, $progText
    if ($line -ne $lastStatus) { $samples += $line; $lastStatus = $line }
    if ($statusText -match '完成|失败|已取消' -and $re.Count -eq 0) { Start-Sleep -Milliseconds 1500; $done = $true; break }
}
$samples | ForEach-Object { $_ }

Write-Output '--- 最终界面文本 ---'
foreach ($h in $kids) {
    $t = [UI]::Text($h)
    if ($t.Length -gt 0 -and $t.Length -lt 300) {
        $c = [UI]::Cls($h)
        if ($c -like '*STATIC*' -or $c -like '*BUTTON*') { "  [$c] $t" }
    }
}

Write-Output '--- 日志渲染验证 ---'
$endLines = [UI]::LineCount($logHwnd)
$endChars = [UI]::TextLength($logHwnd)
$firstVis = [UI]::FirstVisibleLine($logHwnd)
"  行数: 基线 $baseLines → 峰值 $logPeakLines → 结束 $endLines"
"  字符: 基线 $baseChars → 峰值 $logPeakChars → 结束 $endChars"
"  首可见行: $firstVis （大于 0 说明已自动滚到底部）"
$fullText = Get-LogFullText($logHwnd)
"  剪贴板通道取回全文长度: $($fullText.Length)"
if ($fullText.Length -gt 0) {
    Write-Output '  --- 日志末尾 2000 字 ---'
    $tail = if ($fullText.Length -gt 2000) { $fullText.Substring($fullText.Length - 2000) } else { $fullText }
    $tail
}

Write-Output '--- 进度口径一致性 ---'
"  出现的进度读数（共 $($progSeen.Count) 种）："
$progSeen | Select-Object -Last 12 | ForEach-Object { "    $_" }
$bad = @()
foreach ($s in $progSeen) {
    if ($s -match '^\s*(\d+)\s*/\s*(\d+)\s+([0-9.]+)%') {
        $d = [int]$Matches[1]; $a = [int]$Matches[2]; $pc = [double]$Matches[3]
        if ($d -lt $a -and $pc -ge 99.95) { $bad += $s }
    }
}
if ($bad.Count -eq 0) { "  [PASS] 无「已完成<总数 却 100%」的矛盾读数" }
else { "  [FAIL] 矛盾读数 $($bad.Count) 条：" ; $bad | ForEach-Object { "    $_" } }

Write-Output '--- 下载目录 ---'
Get-ChildItem -LiteralPath $OutDir -Recurse -Force | ForEach-Object {
    '  ' + $_.FullName.Replace($OutDir, '') + '  ' + $(if ($_.PSIsContainer) { '<dir>' } else { [math]::Round($_.Length / 1KB, 1).ToString() + ' KB' })
}

Write-Output '--- 完成标记 ---'
"done=$done  logRendered=$($endLines -gt $baseLines -and $endChars -gt $baseChars)"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
