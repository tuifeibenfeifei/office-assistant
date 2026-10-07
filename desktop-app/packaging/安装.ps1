<#
    安装 / 卸载脚本（便携版专用，不需要管理员权限）

    安装：把本目录整体作为安装目录，创建开始菜单与桌面快捷方式。
        pwsh -File .\安装.ps1                 # 默认安装到当前目录，创建快捷方式
        pwsh -File .\安装.ps1 -DesktopShortcut # 同时创建桌面快捷方式（默认已创建）
        pwsh -File .\安装.ps1 -Portable       # 不建快捷方式，只启用便携模式（数据写在程序目录旁）

    卸载：删除快捷方式与本地数据（不动磁盘上的任何用户文件）
        pwsh -File .\安装.ps1 -Uninstall
        pwsh -File .\安装.ps1 -Uninstall -KeepUserData

    注意：本程序不需要管理员权限；删除文件一律经回收站，可恢复。
#>

[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$KeepUserData,
    [switch]$Portable,
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$appDir = $PSScriptRoot
$exeName = '全能办公助手.exe'
$exePath = Join-Path $appDir $exeName
$shortcutName = '全能办公助手.lnk'
$dataDir = Join-Path $env:LOCALAPPDATA '全能办公助手'

function Test-DesktopRuntime {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
        return ($runtimes | Select-String -SimpleMatch 'Microsoft.WindowsDesktop.App 8.').Count -gt 0
    }
    catch {
        return $false
    }
}

if ($Uninstall) {
    Write-Host '==== 卸载全能办公助手 ====' -ForegroundColor Cyan

    $targets = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
        (Join-Path $env:USERPROFILE 'Desktop'),
        (Join-Path $env:PUBLIC 'Desktop')
    )
    foreach ($dir in $targets) {
        $link = Join-Path $dir $shortcutName
        if (Test-Path $link) {
            Remove-Item $link -Force
            Write-Host "  已删除快捷方式：$link"
        }
    }

    if (-not $KeepUserData -and (Test-Path $dataDir)) {
        Write-Host "  正在删除本地数据：$dataDir（仅本程序的索引、日志与设置，不含你的任何文件）"
        Remove-Item -Recurse -Force $dataDir -ErrorAction SilentlyContinue
    }
    elseif ($KeepUserData) {
        Write-Host "  已保留本地数据：$dataDir"
    }

    Write-Host ''
    Write-Host "卸载完成。程序目录仍在：$appDir（可直接删除整个目录）"
    return
}

Write-Host '==== 安装全能办公助手（便携版） ====' -ForegroundColor Cyan
Write-Host "  程序目录：$appDir"

if (-not (Test-Path $exePath)) {
    throw "未找到 $exeName，请确认本脚本与程序放在同一目录。"
}

if (-not (Test-DesktopRuntime)) {
    Write-Warning '未检测到 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.x）。'
    Write-Host '  请先安装后重试：https://dotnet.microsoft.com/download/dotnet/8.0 （选择「Desktop Runtime x64」）'
}

if ($Portable) {
    $flag = Join-Path $appDir 'portable.flag'
    if (-not (Test-Path $flag)) {
        New-Item -ItemType File -Path $flag -Force | Out-Null
    }
    Write-Host "  已启用便携模式：数据写入 $(Join-Path $appDir 'data')"
}
else {
    Write-Host "  数据目录：$dataDir"
}

if (-not $NoShortcut) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $linkPaths = @(
            (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
            (Join-Path $env:USERPROFILE 'Desktop')
        )
        foreach ($dir in $linkPaths) {
            if (-not (Test-Path $dir)) { continue }
            $linkPath = Join-Path $dir $shortcutName
            $link = $shell.CreateShortcut($linkPath)
            $link.TargetPath = $exePath
            $link.WorkingDirectory = $appDir
            $link.Description = '全能办公助手：网络优化 / 作业看板 / 磁盘清理 / 内存管家'
            $link.IconLocation = "$exePath,0"
            $link.Save()
            Write-Host "  已创建快捷方式：$linkPath"
        }
    }
    catch {
        Write-Warning "创建快捷方式失败（可手动创建）：$($_.Exception.Message)"
    }
}

Write-Host ''
Write-Host '安装完成。启动后程序常驻系统托盘：'
Write-Host '  · 关闭主窗口 = 最小化到托盘（可在「设置与合规」页改为直接退出）'
Write-Host '  · 托盘图标右键可立即整理内存、立即同步作业'
Write-Host '  · 首次使用请先到「磁盘管理与清理」页执行一次扫描，再生成一键清理清单'
