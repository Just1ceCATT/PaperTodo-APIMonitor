# 同步本插件仓库的部署清单（plugin.json 等非 dll 文件）到 PaperTodo 输出目录。
# dll 请先编译（dotnet build -c Release），再用本脚本一并复制，或手动复制。
# 用法:
#   powershell -File Sync-PluginsToOutput.ps1
#   或:  powershell -File Sync-PluginsToOutput.ps1 -OutputDir "D:\path\to\PaperTodo-v4.0.0-preview"

param(
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"
$pluginId = "api.balance.monitor"
# 默认指向宿主仓库的输出目录；可通过 -OutputDir 覆盖。
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $hostRepo = Resolve-Path (Join-Path $PSScriptRoot "..\PaperTodo")
    $OutputDir = Join-Path $hostRepo "输出\PaperTodo-v4.0.0-preview"
}
$targetDir = Join-Path $OutputDir "plugins\$pluginId"

if (-not (Test-Path -LiteralPath $targetDir -PathType Container)) {
    throw "Target not found: $targetDir（先运行宿主构建生成插件目录，或检查 -OutputDir）"
}

# 只部署部署清单 plugin.json（仓库根其它源码/脚本不进入插件目录）。
$manifest = Join-Path $PSScriptRoot "plugin.json"
if (Test-Path -LiteralPath $manifest) {
    Copy-Item -LiteralPath $manifest -Destination (Join-Path $targetDir "plugin.json") -Force
    Write-Host "[ok]   $pluginId/plugin.json"
}

# 复制 web/ 资源（HTML 监视面板），存在时才复制。
$webDir = Join-Path $PSScriptRoot "web"
if (Test-Path -LiteralPath $webDir -PathType Container) {
    $targetWeb = Join-Path $targetDir "web"
    New-Item -ItemType Directory -Force -Path $targetWeb | Out-Null
    Get-ChildItem -LiteralPath $webDir -File -Recurse | ForEach-Object {
        $rel = $_.FullName.Substring($webDir.Length + 1)
        $target = Join-Path $targetWeb $rel
        $targetParent = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $targetParent)) {
            New-Item -ItemType Directory -Force -Path $targetParent | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        Write-Host "[ok]   $pluginId/web/$rel"
    }
}

# 一并复制编译产物（dll 与 deps.json），存在时才复制。
$binDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows10.0.17763.0\win-x64"
if (-not (Test-Path -LiteralPath $binDir -PathType Container)) {
    $binDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows10.0.17763.0"
}
if (Test-Path -LiteralPath $binDir -PathType Container) {
    Get-ChildItem -LiteralPath $binDir -File | Where-Object {
        $_.Name -ieq "PaperTodo.Plugin.ApiBalanceMonitor.dll" -or
        $_.Name -ieq "PaperTodo.Plugin.ApiBalanceMonitor.deps.json"
    } | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $targetDir -Force
        Write-Host "[ok]   $pluginId/$($_.Name)（编译产物）"
    }
}

Write-Host "Done. Restart PaperTodo to pick up changes."
