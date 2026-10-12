# build.ps1 — 本地一键编译并打包
#
# 用法：
#   .\build.ps1                 编译并打包，版本号取 v0.0.0-dev
#   .\build.ps1 -Version v1.0.0 指定版本号
#
# 产物：dist\OneKeyTool-<版本>-win-x64.zip
#
# 说明：本脚本与 GitHub Actions 的构建步骤等价，用于本地验证。

param(
    [string]$Version = 'v0.0.0-dev'
)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

Write-Host "=== OneKeyTool 本地构建 ===" -ForegroundColor Cyan
Write-Host "版本: $Version"

# ── 1. 找编译器 ──
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "找不到 C# 编译器（需要 .NET Framework 4.0 以上）" }
Write-Host "编译器: $csc"

# ── 2. 清理 ──
if (Test-Path 'dist') { Remove-Item 'dist' -Recurse -Force }

# ── 3. 编译主程序 ──
Write-Host "`n[1/3] 编译主程序..." -ForegroundColor Yellow
$src = @(
    'src\Program.cs', 'src\Native.cs', 'src\Config.cs', 'src\InputSender.cs',
    'src\Detector.cs', 'src\ActionRunner.cs', 'src\MainForm.cs'
)
foreach ($f in $src) { if (-not (Test-Path $f)) { throw "缺少源文件: $f" } }

New-Item -ItemType Directory -Force -Path 'dist' | Out-Null
$cscArgs = @(
    '/nologo', '/target:winexe', '/platform:x64', '/optimize+',
    '/codepage:65001', '/utf8output', '/out:dist\OneKeyTool.exe',
    '/reference:System.dll', '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll'
) + $src
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "编译失败（退出码 $LASTEXITCODE）" }
Write-Host "  OK" -ForegroundColor Green

# ── 4. 编译诊断工具（仅验证） ──
Write-Host "[2/3] 编译诊断工具..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path 'dist\_tools' | Out-Null
& $csc /nologo /platform:x64 /optimize+ /codepage:65001 /utf8output `
    /out:dist\_tools\KeySpy.exe 'tools\KeySpy\KeySpy.cs'
if ($LASTEXITCODE -ne 0) { throw "KeySpy 编译失败" }
& $csc /nologo /platform:x64 /optimize+ /codepage:65001 /utf8output `
    /out:dist\_tools\MacroTest.exe 'tools\MacroTest\MacroTest.cs'
if ($LASTEXITCODE -ne 0) { throw "MacroTest 编译失败" }
Remove-Item 'dist\_tools' -Recurse -Force
Write-Host "  OK" -ForegroundColor Green

# ── 5. 打包 ──
Write-Host "[3/3] 打包..." -ForegroundColor Yellow
$pkg = "dist\OneKeyTool-$Version"
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item 'dist\OneKeyTool.exe' $pkg
Copy-Item 'config.ini'          $pkg
Copy-Item 'README.md'           $pkg
if (Test-Path 'LICENSE') { Copy-Item 'LICENSE' $pkg }

$zip = "dist\OneKeyTool-$Version-win-x64.zip"
Compress-Archive -Path "$pkg\*" -DestinationPath $zip -Force

Write-Host "`n=== 构建完成 ===" -ForegroundColor Cyan
Write-Host "产物: $zip（$([math]::Round((Get-Item $zip).Length/1KB,1)) KB）" -ForegroundColor Green
Write-Host "`n包内文件："
Get-ChildItem $pkg | Select-Object Name, @{n='大小';e={"{0:N0} B" -f $_.Length}} | Format-Table -AutoSize
