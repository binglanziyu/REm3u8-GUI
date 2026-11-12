# =====================================================================
#  build_csharp.ps1 — 编译 C# 版 REM-GUI（.NET Framework 4.8 / csc.exe）
#  产出：dist\REM-GUI.exe（内嵌 N_m3u8DL-RE.exe 与 ffmpeg.exe 作为自愈源）
#  注意：in-box csc.exe 只支持 C# 5，源码禁止用 ?. / $"" / nameof / out var 等语法
#  用法：
#    powershell -ExecutionPolicy Bypass -File build_csharp.ps1
#    powershell -ExecutionPolicy Bypass -File build_csharp.ps1 -CheckOnly
#    powershell -ExecutionPolicy Bypass -File build_csharp.ps1 -Run
#    powershell -ExecutionPolicy Bypass -File build_csharp.ps1 -Test        # 跑 Core 断言测试
#    powershell -ExecutionPolicy Bypass -File build_csharp.ps1 -CheckRefs   # 校验 AppConfig 字段可达性
# =====================================================================
[CmdletBinding()]
param(
    [switch]$CheckOnly,
    [switch]$Run,
    [switch]$Test,
    [switch]$CheckRefs,
    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$srcRoot = $PSScriptRoot
$prjRoot = Split-Path -Parent $srcRoot
if (-not $OutDir) { $OutDir = Join-Path $prjRoot 'dist' }

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw "找不到 csc.exe（需要 .NET Framework 4.x）" }

$csDir = Join-Path $srcRoot 'csharp'
if (-not (Test-Path -LiteralPath $csDir)) { throw "找不到源码目录 $csDir" }

$files = @(Get-ChildItem -LiteralPath $csDir -Recurse -Filter *.cs | Sort-Object FullName)
if ($files.Count -eq 0) { throw "源码目录里没有 .cs 文件" }

$manifest = Join-Path $csDir 'app.manifest'
if (-not (Test-Path -LiteralPath $manifest)) { throw "缺少 app.manifest（DPI 声明）" }

$ico = Join-Path $srcRoot 'app.ico'

# =====================================================================
#  版本号单源同步：Program.cs 的 Version 常量是唯一真源
#  每次构建前按它回写 AssemblyInfo.cs / app.manifest / update.json /
#  UpdateChecker.DefaultJson。四处不再手写，杜绝"版本互相不一致"。
#  值相同就不动文件，保证脚本幂等。
# =====================================================================
function Write-TextIfChanged([string]$path, [string]$text) {
    if (-not (Test-Path -LiteralPath $path)) { throw "版本同步目标不存在：$path" }
    $old = [System.IO.File]::ReadAllText($path)
    if ($old -ceq $text) { return $false }
    # 沿用原编码：原文件带 BOM 就带 BOM 写回，避免中文注释被写成乱码
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
    return $true
}

$progPath = Join-Path $csDir 'Program.cs'
$vm = [regex]::Match([System.IO.File]::ReadAllText($progPath), 'public\s+const\s+string\s+Version\s*=\s*"([^"]+)"')
if (-not $vm.Success) { throw "无法从 Program.cs 解析 public const string Version（版本真源缺失）" }
$appVer = $vm.Groups[1].Value.Trim()
if ($appVer -notmatch '^\d+(\.\d+){1,3}$') { throw "Program.Version 不是合法版本号：$appVer" }
# 程序集 / 清单要求四段式 a.b.c.d
$verParts = @($appVer.Split('.'))
while ($verParts.Count -lt 4) { $verParts += '0' }
$ver4 = ($verParts[0..3] -join '.')

$verChanges = @()

# 1) AssemblyInfo.cs：AssemblyVersion / AssemblyFileVersion
$aiPath = Join-Path $csDir 'AssemblyInfo.cs'
$aiText = [System.IO.File]::ReadAllText($aiPath)
$aiNew = [regex]::Replace($aiText, 'AssemblyVersion\("[^"]*"\)', ('AssemblyVersion("' + $ver4 + '")'))
$aiNew = [regex]::Replace($aiNew, 'AssemblyFileVersion\("[^"]*"\)', ('AssemblyFileVersion("' + $ver4 + '")'))
if (Write-TextIfChanged $aiPath $aiNew) { $verChanges += "AssemblyInfo.cs → $ver4" }

# 2) app.manifest：只改第一条 assemblyIdentity 的 version
$mfPath = Join-Path $csDir 'app.manifest'
$mfText = [System.IO.File]::ReadAllText($mfPath)
$mfRx = [regex]'(<assemblyIdentity\s+version=")[^"]*(")'
$mfNew = $mfRx.Replace($mfText, ('${1}' + $ver4 + '${2}'), 1)
if (Write-TextIfChanged $mfPath $mfNew) { $verChanges += "app.manifest → $ver4" }

# 3) update.json：发布清单版本
$ujPath = Join-Path $srcRoot 'update.json'
if (-not (Test-Path -LiteralPath $ujPath)) { throw "缺少 update.json（发布清单）" }
$ujText = [System.IO.File]::ReadAllText($ujPath)
$ujRx = [regex]'("version"\s*:\s*")[^"]*(")'
$ujNew = $ujRx.Replace($ujText, ('${1}' + $appVer + '${2}'), 1)
if (Write-TextIfChanged $ujPath $ujNew) { $verChanges += "update.json → $appVer" }

# 4) UpdateChecker.DefaultJson：C# 转义形式的 \"version\":\"x.y.z\"
$ucPath = Join-Path $csDir 'Services\UpdateChecker.cs'
$ucText = [System.IO.File]::ReadAllText($ucPath)
$ucRx = [regex]'(\\"version\\":\\")[^"\\]*(\\")'
$ucNew = $ucRx.Replace($ucText, ('${1}' + $appVer + '${2}'), 1)
if (Write-TextIfChanged $ucPath $ucNew) { $verChanges += "UpdateChecker.DefaultJson → $appVer" }

if ($verChanges.Count -gt 0) {
    Write-Host ("== 版本同步（真源 Program.Version = {0}）==" -f $appVer) -ForegroundColor Yellow
    $verChanges | ForEach-Object { Write-Host ("   已回写 " + $_) }
}
else {
    Write-Host ("== 版本一致 {0}：AssemblyInfo / manifest / update.json / DefaultJson 均已对齐 ==" -f $appVer) -ForegroundColor DarkGray
}

Write-Host "== 源码 $($files.Count) 个 ==" -ForegroundColor Cyan
$files | ForEach-Object { Write-Host ("   {0,-42} {1,8} B" -f $_.Name, $_.Length) }

$refs = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Security.dll',
    '/r:System.IO.Compression.dll',
    '/r:System.IO.Compression.FileSystem.dll'
)
$srcArgs = $files | ForEach-Object { '"' + $_.FullName + '"' }

if ($CheckOnly) {
    Write-Host "`n== 仅语法/语义检查（/target:library，不出 exe）==" -ForegroundColor Yellow
    # 产物必须显式 /out 到临时目录：不给 /out 时 csc 会把 AssemblyInfo.dll 吐在当前工作目录，
    # 历史上就是因此在源码目录留下 _check.dll / AssemblyInfo.dll 等编译残留。结束后无条件清理。
    $checkDir = Join-Path ([System.IO.Path]::GetTempPath()) ('rem_gui_check_' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $checkDir -Force | Out-Null
    $checkOut = Join-Path $checkDir 'CheckOnly.dll'
    try {
        $checkArgs = @('/nologo', '/target:library', '/codepage:65001', ('/out:"' + $checkOut + '"')) + $refs + $srcArgs
        $check = & $csc @checkArgs 2>&1 | Out-String
        $checkCode = $LASTEXITCODE
        if ($check.Trim()) { Write-Host $check.Trim() -ForegroundColor Yellow }
        if ($checkCode -ne 0) { throw "编译检查失败" }
        Write-Host ("检查通过（产物已写入临时目录 {0}，随后清理）" -f $checkDir) -ForegroundColor Green
    }
    finally {
        # 临时目录内只含本次检查产物，直接删；失败也不影响检查结论
        try { [System.IO.Directory]::Delete($checkDir, $true) } catch { }
        if (Test-Path -LiteralPath $checkDir) { Write-Warning "临时目录未清理干净：$checkDir" }
    }
    return
}

# ============ P20：AppConfig 字段引用校验（防“写入从不读取”的死配置） ============
if ($CheckRefs) {
    Write-Host "`n== AppConfig 字段引用校验 ==" -ForegroundColor Yellow
    $cfgPath = Join-Path $csDir 'Services\Config.cs'
    $cfgText = [System.IO.File]::ReadAllText($cfgPath)
    $fieldPat = [regex]'(?m)^\s*public\s+(?:string|int|bool|long|double)\s+(\w+)\s*='
    $names = @()
    foreach ($m in $fieldPat.Matches($cfgText)) {
        $n = $m.Groups[1].Value
        if ($n -notin $names) { $names += $n }
    }
    # 汇总全部源码文本用于统计（含 Config.cs 自身的读写）
    $all = ''
    foreach ($f in $files) { $all += [System.IO.File]::ReadAllText($f.FullName) }
    $dead = @()
    foreach ($n in $names) {
        $c = ([regex]::Matches($all, ('\b' + [regex]::Escape($n) + '\b'))).Count
        if ($c -le 1) { $dead += ('{0}（出现 {1} 次，疑似死字段）' -f $n, $c) }
    }
    Write-Host ("字段总数 {0}" -f $names.Count)
    if ($dead.Count -gt 0) {
        $dead | ForEach-Object { Write-Host ("  [DEAD] " + $_) -ForegroundColor Red }
        throw "存在无人引用的配置字段"
    }
    Write-Host "全部字段均有引用" -ForegroundColor Green
    return
}

# ============ P19：Core 断言测试 ============
if ($Test) {
    $testDir = Join-Path $srcRoot 'tests'
    $testFile = Join-Path $testDir 'CoreTests.cs'
    if (-not (Test-Path -LiteralPath $testFile)) { throw "找不到 $testFile" }
    $testOut = Join-Path ([System.IO.Path]::GetTempPath()) ('rem_gui_tests_' + [Guid]::NewGuid().ToString('N') + '.exe')
    Write-Host "`n== 编译测试（/target:exe /main:Rem.Tests.Entry）==" -ForegroundColor Cyan
    $testArgs = @('/nologo', '/target:exe', '/platform:anycpu', '/codepage:65001', '/main:Rem.Tests.Entry', ('/out:"' + $testOut + '"')) + $refs + $srcArgs + @('"' + $testFile + '"')
    $tout = & $csc @testArgs 2>&1 | Out-String
    if ($tout.Trim()) { Write-Host $tout.Trim() }
    if ($LASTEXITCODE -ne 0) { throw "测试编译失败" }
    Write-Host "== 运行 ==" -ForegroundColor Cyan
    & $testOut
    $code = $LASTEXITCODE
    Remove-Item -LiteralPath $testOut -Force -ErrorAction SilentlyContinue
    if ($code -ne 0) { throw "测试失败（退出码 $code）" }
    Write-Host "测试全部通过" -ForegroundColor Green
    return
}

$outExe = Join-Path $OutDir 'REM-GUI.exe'
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

# =====================================================================
#  自愈源工具查找：只在"软件所在文件夹"（交付目录 dist）顶层找
#  严禁递归扫描子目录、严禁全盘搜索 —— 递归扫描会撞上 engine\<版本>\ 里的旧引擎、
#  tools\ 里的残留副本，把错误版本内嵌进 exe，出问题时极难排查。
#  缺失即 throw：旧逻辑只 Write-Warning 静默降级，结果重建的 exe 悄悄失去自愈能力。
# =====================================================================
$distDir = Join-Path $prjRoot 'dist'
$reExe = Join-Path $distDir 'N_m3u8DL-RE.exe'
$ffExe = Join-Path $distDir 'ffmpeg.exe'
$missing = @()
foreach ($tool in @($reExe, $ffExe)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { $missing += (Split-Path -Leaf $tool) }
}
if ($missing.Count -gt 0) {
    throw ("缺少内嵌工具：{0}。请放到 {1} 顶层（构建脚本只查顶层，不递归、不全盘搜索）。缺任一工具都会让新 exe 失去自愈能力，故直接终止。" -f ($missing -join ' / '), $distDir)
}

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:anycpu',
    '/optimize+',
    '/codepage:65001',
    '/warn:4',
    ('/out:"' + $outExe + '"'),
    ('/win32manifest:"' + $manifest + '"')
) + $refs
if (Test-Path -LiteralPath $ico) {
    $cscArgs += '/win32icon:"' + $ico + '"'
    # 同时把 ICO 作为清单资源嵌入：窗口 Icon 直接由资源加载，
    # 不再依赖 Windows 对 exe 图标的缓存（缓存住了改图标也不生效）
    $cscArgs += '/resource:"' + $ico + '",app.ico'
}
else { Write-Warning "缺少 app.ico，跳过图标（不影响自愈源）" }
# 两个工具已在上方校验存在，此处无条件内嵌，不再有"静默不内嵌"的分支
$cscArgs += '/resource:"' + $reExe + '",N_m3u8DL-RE.exe'
$cscArgs += '/resource:"' + $ffExe + '",ffmpeg.exe'
# 更新清单以 /resource 内嵌（资源名固定 update.json，与 UpdateChecker.EmbeddedResourceName 对齐）：
# 交付只发一个 REM-GUI.exe 时，运行时靠这份内置清单检查更新，不再依赖外部 update.json
$cscArgs += '/resource:"' + $ujPath + '",update.json'
Write-Host ("== 内嵌自愈源 == N_m3u8DL-RE.exe {0:N2} MB / ffmpeg.exe {1:N2} MB（来自 {2} 顶层）" -f ((Get-Item -LiteralPath $reExe).Length / 1MB), ((Get-Item -LiteralPath $ffExe).Length / 1MB), $distDir)
Write-Host ("== 内嵌清单 == update.json {0} B（version {1}）" -f (Get-Item -LiteralPath $ujPath).Length, $appVer)
$cscArgs += $srcArgs

Write-Host "`n== 编译 ==" -ForegroundColor Cyan
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$out = & $csc @cscArgs 2>&1 | Out-String
$sw.Stop()
$code = $LASTEXITCODE
if ($out.Trim()) { Write-Host $out.Trim() }

if ($code -ne 0 -or -not (Test-Path -LiteralPath $outExe)) { throw "编译失败（退出码 $code）" }

$f = Get-Item -LiteralPath $outExe
Write-Host ("`n== 完成 ==  {0}  {1:N2} MB  用时 {2:N1} s" -f $f.FullName, ($f.Length / 1MB), $sw.Elapsed.TotalSeconds) -ForegroundColor Green

# ============ 交付形态：一个 exe 即全部（update.json 已内嵌，不再下发） ============
# 历史版本会把 update.json 拷到输出目录；现在不拷了，运行时读取顺序是
# 外部 update.json → 内嵌资源 → DefaultJson，所以残留的旧文件反而会覆盖内置清单。
$ujOut = Join-Path $OutDir 'update.json'
if (Test-Path -LiteralPath $ujOut) {
    Write-Warning ("{0} 是旧版遗留文件，会覆盖 exe 内嵌清单；确认是调试用的手动覆盖，否则请删除" -f $ujOut)
}
else {
    Write-Host ("== 交付形态 == 单文件：{0}（更新清单已内嵌，version {1}）==" -f (Split-Path -Leaf $outExe), $appVer) -ForegroundColor Green
}

# ============ 产物自检：两个自愈源 + 更新清单必须真的在 exe 里 ============
try {
    $asm = [System.Reflection.Assembly]::LoadFile($outExe)
    $names = @($asm.GetManifestResourceNames())
    $need = @('N_m3u8DL-RE.exe', 'ffmpeg.exe', 'update.json')
    $lost = @()
    foreach ($n in $need) { if ($names -notcontains $n) { $lost += $n } }
    if ($lost.Count -gt 0) {
        throw ("产物缺少内嵌资源：{0}（实际资源：{1}）" -f ($lost -join ' / '), ($names -join ', '))
    }
    # 内嵌清单可读性校验：单文件交付时这就是唯一的清单来源，读不出来等于自更新检查瘸腿
    $rs = $asm.GetManifestResourceStream('update.json')
    $jr = New-Object System.IO.StreamReader($rs, [System.Text.Encoding]::UTF8)
    $jtxt = $jr.ReadToEnd()
    $jr.Close()
    $rs.Close()
    if ($jtxt -notmatch '"engine"') { throw "内嵌 update.json 缺少 engine 段" }
    if ($jtxt -notmatch '"self"') { throw "内嵌 update.json 缺少 self 段（主程序自更新无法工作）" }
    Write-Host ("== 产物自检 == 内嵌资源齐全：{0} + app.ico；清单含 engine/self，version {1}" -f ($need -join ' / '), $appVer) -ForegroundColor Green
}
catch [System.Management.Automation.RuntimeException] { throw }
catch {
    # 反射加载失败（依赖缺失等）不算构建失败，退化为体积校验
    $minBytes = (Get-Item -LiteralPath $reExe).Length + (Get-Item -LiteralPath $ffExe).Length
    if ($f.Length -lt $minBytes) {
        throw ("产物体积 {0:N2} MB 小于两个工具之和 {1:N2} MB，几乎可以确定没内嵌成功" -f ($f.Length / 1MB), ($minBytes / 1MB))
    }
    Write-Host ("== 产物自检 == 反射枚举失败（{0}），已按体积校验通过：{1:N2} MB ≥ 两个工具之和" -f $_.Exception.Message, ($f.Length / 1MB)) -ForegroundColor Yellow
}

if ($Run) {
    Write-Host "== 启动 ==" -ForegroundColor Cyan
    Start-Process -FilePath $outExe
}
