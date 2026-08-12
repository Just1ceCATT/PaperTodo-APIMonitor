# 测试 DeepSeek /v1/usage 接口（小时粒度用量），确认返回结构与字段名。
# 用法（三选一）:
#   powershell -File Test-DeepSeekUsageHourly.ps1 -ApiKey "sk-xxx"
#   $env:DEEPSEEK_API_KEY = "sk-xxx"; powershell -File Test-DeepSeekUsageHourly.ps1
#   或直接运行：自动从插件设置文件读取 apiKey

param(
    [string]$ApiKey,
    [string]$Url = "https://api.deepseek.com/v1/usage"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = $env:DEEPSEEK_API_KEY
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    # 尝试从插件设置文件读取 apiKey
    $settingsPath = Join-Path $PSScriptRoot "..\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\data\api.balance.monitor.json"
    if (Test-Path -LiteralPath $settingsPath) {
        try {
            $data = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
            $ApiKey = $data.settings.apiKey
        } catch {
            $ApiKey = ""
        }
    }
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Host "未找到 API Key：请用 -ApiKey 传参、设置环境变量 DEEPSEEK_API_KEY，或保证插件设置文件存在。" -ForegroundColor Yellow
    exit 1
}

$mask = if ($ApiKey.Length -gt 8) { $ApiKey.Substring(0, 8) + "..." } else { $ApiKey }
Write-Host "URL:   $Url" -ForegroundColor Cyan
Write-Host "Token: $mask" -ForegroundColor DarkGray
Write-Host ""

$headers = @{
    "Accept"        = "application/json"
    "Authorization" = "Bearer $ApiKey"
}

try {
    $response = Invoke-WebRequest -Uri $Url -Method GET -Headers $headers -TimeoutSec 15 -UseBasicParsing
    Write-Host "HTTP $($response.StatusCode) $($response.StatusDescription)" -ForegroundColor Green
    Write-Host ""
    $content = $response.Content

    Write-Host "Raw response (前 3000 字符):" -ForegroundColor Cyan
    if ($content.Length -gt 3000) { Write-Host $content.Substring(0, 3000) } else { Write-Host $content }
    Write-Host ""

    try {
        $json = $content | ConvertFrom-Json
        Write-Host "解析结果:" -ForegroundColor Cyan
        Write-Host ("  total_usage = {0}" -f $json.total_usage)
        Write-Host ("  start_date  = {0}" -f $json.start_date)
        Write-Host ("  end_date    = {0}" -f $json.end_date)
        if ($json.items) {
            $count = @($json.items).Count
            Write-Host ("  items 条数 = {0}" -f $count)
            if ($count -gt 0) {
                Write-Host "  items[0] 字段:" -ForegroundColor Cyan
                $json.items[0].PSObject.Properties | ForEach-Object {
                    Write-Host ("    {0} = {1}" -f $_.Name, $_.Value)
                }
            }
            if ($count -gt 1) {
                Write-Host "  items[1] 字段:" -ForegroundColor Cyan
                $json.items[1].PSObject.Properties | ForEach-Object {
                    Write-Host ("    {0} = {1}" -f $_.Name, $_.Value)
                }
            }
        } else {
            Write-Host "  (无 items 字段)" -ForegroundColor Yellow
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
