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

Get-ChildItem -LiteralPath $PSScriptRoot -File | ForEach-Object {
    if ($_.Extension -ieq ".dll") {
        # dll 由编译产物提供；不要从仓库根覆盖。
        return
    }
    $target = Join-Path $targetDir $_.Name
    try {
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        Write-Host "[ok]   $pluginId/$($_.Name)"
    } catch {
        Write-Host "[fail] $pluginId/$($_.Name) - $($_.Exception.Message)"
    }
}

# 一并复制编译产物（dll 与 deps.json），存在时才复制。
$binDir = Join-Path $PSScriptRoot "bin\Release\net10.0-windows"
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
