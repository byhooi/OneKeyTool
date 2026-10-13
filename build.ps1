# build.ps1 — 本地一键编译并产出发布文件
#
# 用法：
#   .\build.ps1                 编译，版本号取 v0.0.0-dev
#   .\build.ps1 -Version v1.0.0 指定版本号
#
# 产物：dist\OneKeyTool-<版本>-win-x64.exe（上传到 Release 的唯一文件）
#
# 说明：本脚本与 GitHub Actions 的构建步骤等价，用于本地验证。
#       -Version 是版本号的唯一来源：它会写进 exe 的文件属性、
#       设置窗口标题与托盘提示，同时也用于发布文件名。

param(
    [string]$Version = 'v0.0.0-dev'
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

# 版本号只允许字母、数字、点、连字符和加号，避免拼进 C# 字符串字面量时被破坏
if ($Version -notmatch '^[A-Za-z0-9.\-+]+$') {
    throw "版本号含非法字符: $Version（只允许字母、数字、. - +）"
}

# 文件属性需要四段数字版本（如 1.2.3.4）。tag 形如 v1.2.3 时取其数字部分，
# 非数字部分（如 -dev）与缺失的段补 0。
$numeric = ($Version -replace '^[vV]', '') -replace '[^0-9.].*$', ''
$parts = @($numeric -split '\.' | Where-Object { $_ -ne '' })
while ($parts.Count -lt 4) { $parts += '0' }
$fileVersion = ($parts[0..3] -join '.')

Write-Host "=== OneKeyTool 本地构建 ===" -ForegroundColor Cyan
Write-Host "版本: $Version（文件版本 $fileVersion）"

# ── 1. 找编译器 ──
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "找不到 C# 编译器（需要 .NET Framework 4.0 以上）" }
Write-Host "编译器: $csc"

# ── 2. 准备构建目录（只覆盖本脚本管理的文件）──
New-Item -ItemType Directory -Force -Path 'dist' | Out-Null
if (Test-Path 'dist\OneKeyTool.exe') { Remove-Item 'dist\OneKeyTool.exe' -Force }
if (Test-Path 'dist\_tools') { Remove-Item 'dist\_tools' -Recurse -Force }
if (Test-Path 'dist\_gen') { Remove-Item 'dist\_gen' -Recurse -Force }
New-Item -ItemType Directory -Force -Path 'dist\_gen' | Out-Null

# ── 3. 生成版本信息源文件 ──
# 主程序源码清单里不含 src\BuildInfo.cs：这里用生成版覆盖它，
# 这样 -Version 一处生效，exe 属性与界面显示不会和发布文件名不一致。
$buildInfo = @"
// 本文件由 build.ps1 生成，请勿手工修改或提交。
using System.Reflection;

[assembly: AssemblyVersion("$fileVersion")]
[assembly: AssemblyFileVersion("$fileVersion")]
[assembly: AssemblyInformationalVersion("$Version")]

namespace OneKeyTool
{
    internal static class BuildInfo
    {
        public const string Version = "$Version";
        public const string DisplayName = "键盘自定义键工具 " + Version;
    }
}
"@
Set-Content -LiteralPath 'dist\_gen\BuildInfo.cs' -Value $buildInfo -Encoding UTF8

# ── 4. 编译主程序（步骤 1/3）──
# 主程序源文件清单只此一处：测试编译复用同一份，避免两处清单漂移。
$src = @(
    'dist\_gen\BuildInfo.cs',
    'src\Program.cs', 'src\Native.cs', 'src\Config.cs', 'src\InputSender.cs',
    'src\MacroEngine.cs', 'src\Detector.cs', 'src\ActionRunner.cs',
    'src\AutoStart.cs', 'src\Updater.cs', 'src\MainForm.cs'
)

Write-Host "`n[1/3] 编译主程序..." -ForegroundColor Yellow
foreach ($f in $src) { if (-not (Test-Path $f)) { throw "缺少源文件: $f" } }

$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
    '/codepage:65001', '/utf8output', '/out:dist\OneKeyTool.exe',
    '/reference:System.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
)
if (Test-Path 'src\app.ico') {
    $cscArgs += '/win32icon:src\app.ico'
}
$cscArgs += $src
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
Write-Host "  OK" -ForegroundColor Green

# ── 5. 编译诊断与辅助工具（仅验证，步骤 2/3）──
Write-Host "[2/3] 编译诊断与辅助工具..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path 'dist\_tools' | Out-Null
& $csc /nologo /platform:x64 /optimize+ /codepage:65001 /utf8output `
    /out:dist\_tools\KeySpy.exe 'tools\KeySpy\KeySpy.cs'
if ($LASTEXITCODE -ne 0) { throw "KeySpy 编译失败" }
& $csc /nologo /platform:x64 /optimize+ /codepage:65001 /utf8output `
    /out:dist\_tools\MacroTest.exe 'tools\MacroTest\MacroTest.cs'
if ($LASTEXITCODE -ne 0) { throw "MacroTest 编译失败" }
& $csc /nologo /platform:x64 /optimize+ /codepage:65001 /utf8output /r:System.Drawing.dll `
    /out:dist\_tools\GenerateIcon.exe 'tools\GenerateIcon\GenerateIcon.cs'
if ($LASTEXITCODE -ne 0) { throw "GenerateIcon 编译失败" }

Write-Host "运行状态机回归测试..." -ForegroundColor Yellow
& $csc /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /utf8output `
    /main:OneKeyTool.Tests.TestRunner /reference:System.dll /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll /out:dist\_tools\StateMachineTests.exe `
    $src 'tests\StateMachineTests.cs'
if ($LASTEXITCODE -ne 0) { throw "状态机测试编译失败" }
& 'dist\_tools\StateMachineTests.exe'
if ($LASTEXITCODE -ne 0) { throw "状态机测试失败" }
Remove-Item 'dist\_tools' -Recurse -Force
Write-Host "  OK" -ForegroundColor Green

# ── 6. 产出发布用的 exe（步骤 3/3）──
# Release 只挂这一个文件：用户下载后双击即可运行，首次启动会自动生成 config.ini。
# 自动更新也从这里取：程序用 GitHub API 返回的 asset.digest 校验下载，
# 因此不需要额外上传校验清单。
Write-Host "[3/3] 生成发布文件..." -ForegroundColor Yellow
$publishExe = "dist\OneKeyTool-$Version-win-x64.exe"
Copy-Item 'dist\OneKeyTool.exe' $publishExe -Force

$hash = (Get-FileHash $publishExe -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "`n=== 构建完成 ===" -ForegroundColor Cyan
Write-Host "发布文件: $publishExe（$([math]::Round((Get-Item $publishExe).Length/1KB,1)) KB）" -ForegroundColor Green
Write-Host "SHA256:   $hash"
Write-Host "`n把这个 exe 上传到 Release 即可（GitHub 会自动计算 digest 供程序校验）。"
