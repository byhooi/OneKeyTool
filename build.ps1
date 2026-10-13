# build.ps1 — 本地一键编译并产出发布文件
#
# 用法：
#   .\build.ps1                 编译，版本号取 v0.0.0-dev
#   .\build.ps1 -Version v1.0.0 指定版本号
#
# 产物：dist\OneKeyTool-<版本>-win-x64.zip（上传到 Release 的唯一文件）
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
        public const string ReleasesPage = "https://github.com/byhooi/OneKeyTool/releases/latest";
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
    'src\AutoStart.cs', 'src\MainForm.cs'
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

# ── 6. 打包（步骤 3/3）──
# Release 只挂这一个 zip：解压即用，无需安装。
# 用 zip 而非裸 exe 分发：浏览器与 SmartScreen 对压缩包不做「未知发布者」拦截，
# 用户拿到的是「下载 → 解压 → 双击」的常规流程，不会被安全提示打断。
Write-Host "[3/3] 打包..." -ForegroundColor Yellow
$pkg = "dist\OneKeyTool-$Version"
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item 'dist\OneKeyTool.exe' $pkg
Copy-Item 'config.ini'          $pkg
Copy-Item 'README.md'           $pkg
if (Test-Path 'LICENSE') { Copy-Item 'LICENSE' $pkg }

$zip = "dist\OneKeyTool-$Version-win-x64.zip"
Compress-Archive -Path "$pkg\*" -DestinationPath $zip -Force

Write-Host "`n=== 构建完成 ===" -ForegroundColor Cyan
Write-Host "发布文件: $zip（$([math]::Round((Get-Item $zip).Length/1KB,1)) KB）" -ForegroundColor Green
Write-Host "`n包内文件："
Get-ChildItem $pkg | Select-Object Name, @{n='大小';e={"{0:N0} B" -f $_.Length}} | Format-Table -AutoSize
Write-Host "把这个 zip 上传到 Release 即可。"
