# 测试 MiniMax Coding Plan 用量接口。
# Key 从插件设置文件（settings.minimaxApiKey）或环境变量 MINIMAX_API_KEY 读取，
# 不会出现在命令行参数里（安全约束）。
# 用法:
#   powershell -File Test-MiniMaxCodingPlan.ps1

$ErrorActionPreference = "Stop"
$url = "https://www.minimaxi.com/v1/api/openplatform/coding_plan/remains"

$apiKey = $env:MINIMAX_API_KEY
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    $settingsPath = Join-Path $PSScriptRoot "..\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\data\api.balance.monitor.json"
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $data = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
            $apiKey = $data.settings.minimaxApiKey
        } catch { $apiKey = "" }
    }
}
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Write-Host "未找到 MiniMax Key：请在插件设置页填写「MiniMax API Key」并保存，或设置环境变量 MINIMAX_API_KEY。" -ForegroundColor Yellow
    exit 1
}

$mask = if ($apiKey.Length -gt 8) { $apiKey.Substring(0, 8) + "..." } else { $apiKey }
Write-Host "URL:   $url" -ForegroundColor Cyan
Write-Host "Key:   $mask" -ForegroundColor DarkGray
Write-Host ""

$headers = @{
    "Authorization" = "Bearer $apiKey"
    "Content-Type"  = "application/json"
    "Accept"        = "application/json"
}

try {
    $response = Invoke-WebRequest -Uri $url -Method GET -Headers $headers -TimeoutSec 15 -UseBasicParsing
    Write-Host "HTTP $($response.StatusCode) $($response.StatusDescription)" -ForegroundColor Green
    Write-Host ""
    $content = $response.Content
    Write-Host "Raw response:" -ForegroundColor Cyan
    Write-Host $content
    Write-Host ""
    try {
        $json = $content | ConvertFrom-Json
        Write-Host "字段概览:" -ForegroundColor Cyan
        $json.PSObject.Properties | ForEach-Object {
            Write-Host ("  {0} = {1}" -f $_.Name, $_.Value)
        }
    } catch {
        Write-Host "JSON 解析失败: $($_.Exception.Message)" -ForegroundColor Yellow
    }
} catch {
    $statusCode = $null
    $body = $null
    if ($_.Exception.Response) {
        $statusCode = [int]$_.Exception.Response.StatusCode
        try {
            $stream = $_.Exception.Response.GetResponseStream()
            $reader = New-Object System.IO.StreamReader($stream)
            $body = $reader.ReadToEnd()
        } catch {}
    }
    Write-Host "请求失败: $($_.Exception.Message)" -ForegroundColor Red
    if ($statusCode) { Write-Host "HTTP $statusCode" -ForegroundColor Red }
    if ($body) { Write-Host $body }
    exit 1
}
