# 构建与打包脚本
#
# 用法（在仓库根目录执行）：
#   pwsh -File .\build.ps1              # 构建 + 运行全部自测 + UI 自检 + 产出便携版
#   pwsh -File .\build.ps1 -SkipTests   # 只构建与打包
#   pwsh -File .\build.ps1 -NoPublish   # 只构建与自测
#
# 说明：
#   · 本项目零第三方依赖、不访问 NuGet，离线即可完成构建与打包。
#   · 产出为「框架依赖」便携版（需要目标机器已装 .NET 8 桌面运行时）。
#     自包含发布需要联网下载 Microsoft.NETCore.App.Runtime.win-x64 运行时包，
#     在离线环境下无法完成，因此脚本不再提供自包含选项。

[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$NoPublish
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$appProject = Join-Path $root 'src\OfficeAssistant.App\OfficeAssistant.App.csproj'
$coreProject = Join-Path $root 'src\OfficeAssistant.Core\OfficeAssistant.Core.csproj'
$testProject = Join-Path $root 'src\OfficeAssistant.Tests\OfficeAssistant.Tests.csproj'
$distRoot = Join-Path $root 'dist'

function Write-Step([string]$text) {
    Write-Host ''
    Write-Host "==== $text ====" -ForegroundColor Cyan
}

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') 失败（退出码 $LASTEXITCODE）" }
}

Write-Step "构建（Release）"
# 注意：解决方案级 dotnet build 在本机 SDK 上会因缺少 workload locator 目录而报 MSB4276，
# 因此这里逐个项目构建，效果等价且更稳定。
Invoke-Dotnet @('build', $coreProject, '-c', 'Release', '--nologo', '-v', 'q')
Invoke-Dotnet @('build', $appProject, '-c', 'Release', '--nologo', '-v', 'q')
Invoke-Dotnet @('build', $testProject, '-c', 'Release', '--nologo', '-v', 'q')

if (-not $SkipTests) {
    Write-Step "运行引擎自测（13 项：误删防护 / 扫描完整性 / 识别覆盖 / 调度 / 合规红线）"
    $testExe = Join-Path $root 'src\OfficeAssistant.Tests\bin\Release\net8.0-windows\OfficeAssistant.Tests.exe'
    & $testExe
    if ($LASTEXITCODE -ne 0) { throw "引擎自测未通过（退出码 $LASTEXITCODE）" }
}

if (-not $NoPublish) {
    Write-Step "打包便携版（框架依赖，需要目标机器已安装 .NET 8 桌面运行时）"
    $portable = Join-Path $distRoot '全能办公助手-便携版'
    if (Test-Path $portable) { Remove-Item -Recurse -Force $portable }
    Invoke-Dotnet @('publish', $appProject, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false',
        '-o', $portable, '--nologo', '-v', 'q')

    # 安装/卸载脚本随后复制
    $packaging = Join-Path $root 'packaging'
    if (Test-Path $packaging) {
        Copy-Item (Join-Path $packaging '*') $portable -Recurse -Force
    }

    $runtimeOk = (& dotnet --list-runtimes 2>$null | Select-String -SimpleMatch 'Microsoft.WindowsDesktop.App 8.').Count -gt 0
    if (-not $runtimeOk) {
        Write-Warning '本机未检测到 .NET 8 桌面运行时；产出的程序需要先安装该运行时才能启动。'
    }

    Write-Step "UI 自检（真实 WPF 栈上加载全部 XAML 并检查数据绑定）"
    $smokeData = Join-Path $env:TEMP ('oa-smoke-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    $env:OFFICEASSISTANT_DATA_DIR = $smokeData
    try {
        $exe = Join-Path $portable '全能办公助手.exe'
        & $exe '--smoke-test'
        if ($LASTEXITCODE -ne 0) { throw "UI 自检未通过（退出码 $LASTEXITCODE）" }
    }
    finally {
        Remove-Item Env:\OFFICEASSISTANT_DATA_DIR -ErrorAction SilentlyContinue
        if (Test-Path $smokeData) { Remove-Item -Recurse -Force $smokeData -ErrorAction SilentlyContinue }
    }
}

Write-Step "完成"
Get-ChildItem $distRoot -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum
    Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($size / 1MB))
}
Write-Host ''
Write-Host '下一步：把「全能办公助手-便携版」整个目录拷到目标机器，运行其中的「安装.ps1」创建快捷方式；'
Write-Host '目标机器若没有 .NET 8 桌面运行时，请先安装：https://dotnet.microsoft.com/download/dotnet/8.0'
Write-Host '数据默认写入 %LOCALAPPDATA%\全能办公助手；安装时加 -Portable 可切换为便携模式（数据写在程序目录旁的 data 子目录）。'
