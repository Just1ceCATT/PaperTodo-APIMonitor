# 探测 platform.deepseek.com 用量接口是否提供小时粒度，找出可用的端点/参数。
# 自动从插件设置文件读取 usageToken。运行后把输出贴给助手分析。

$ErrorActionPreference = "Stop"

$settingsPath = Join-Path $PSScriptRoot "..\PaperTodo\输出\PaperTodo-v4.0.0-preview\plugins\data\api.balance.monitor.json"
$usageToken = ""
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $data = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
        $usageToken = $data.settings.usageToken
    } catch { $usageToken = "" }
}
if ([string]::IsNullOrWhiteSpace($usageToken)) {
    Write-Host "未找到 usageToken：请先在插件设置页填写用量 Token。" -ForegroundColor Yellow
    exit 1
}

$now = Get-Date
$month = "{0:D2}" -f $now.Month
$year = $now.Year

$headers = @{
    "Accept"        = "*/*"
    "x-app-version" = "1.0.0"
    "Authorization" = "Bearer $usageToken"
}

function Invoke-Probe($label, $url) {
    try {
        $r = Invoke-WebRequest -Uri $url -Method GET -Headers $headers -TimeoutSec 15 -UseBasicParsing
        $body = $r.Content
        $first = if ($body.Length -gt 300) { $body.Substring(0, 300) } else { $body }
        Write-Host "[$label] HTTP $($r.StatusCode)" -ForegroundColor Green
        Write-Host "  URL: $url"
        Write-Host "  BODY: $first"
        Write-Host ""
    } catch {
        $code = $null
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        Write-Host "[$label] 失败 HTTP $code" -ForegroundColor Red
        Write-Host "  URL: $url"
        Write-Host "  ERR: $($_.Exception.Message)"
        Write-Host ""
    }
}

$base = "https://platform.deepseek.com/api/v0/usage/amount?month=$month&year=$year"

Write-Host "===== 基准响应完整结构（前 3000 字符）=====" -ForegroundColor Cyan
Invoke-Probe "base(amount)" $base
Write-Host "===== 基准响应完整内容 =====" -ForegroundColor Cyan
try {
    $r = Invoke-WebRequest -Uri $base -Method GET -Headers $headers -TimeoutSec 15 -UseBasicParsing
    $body = $r.Content
    if ($body.Length -gt 3000) { Write-Host $body.Substring(0, 3000) } else { Write-Host $body }
    Write-Host ""
    try {
        $json = $body | ConvertFrom-Json
        Write-Host "字段概览:" -ForegroundColor Cyan
        Write-Host ("  data.biz_data 的键: " + (($json.data.biz_data.PSObject.Properties.Name) -join ", "))
        if ($json.data.biz_data.days) {
            $days = @($json.data.biz_data.days)
            Write-Host ("  days 数量: {0}" -f $days.Count)
            if ($days.Count -gt 0) {
                Write-Host ("  days[0] 的键: " + (($days[0].PSObject.Properties.Name) -join ", "))
                Write-Host ("  days[0] 的 date: {0}" -f $days[0].date)
            }
        }
        if ($json.data.biz_data.hours) {
            $hours = @($json.data.biz_data.hours)
            Write-Host ("  ★ 发现 hours 字段! 数量: {0}" -f $hours.Count)
            if ($hours.Count -gt 0) {
                Write-Host ("  hours[0] 的键: " + (($hours[0].PSObject.Properties.Name) -join ", "))
            }
        } else {
            Write-Host "  (无 hours 字段)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "JSON 解析失败: $($_.Exception.Message)" -ForegroundColor Yellow
    }
} catch {
    Write-Host "基准请求失败" -ForegroundColor Red
}
Write-Host ""

Write-Host "===== 候选端点 / 参数探测 =====" -ForegroundColor Cyan
Invoke-Probe "amount+hour"        "$base&hour=0"
Invoke-Probe "amount+granularity" "$base&granularity=hourly"
Invoke-Probe "amount+type"        "$base&type=hourly"
Invoke-Probe "hourly"             "https://platform.deepseek.com/api/v0/usage/hourly?month=$month&year=$year"
Invoke-Probe "usage/days"         "https://platform.deepseek.com/api/v0/usage/days?month=$month&year=$year"
Invoke-Probe "amount/hourly"      "https://platform.deepseek.com/api/v0/usage/amount/hourly?month=$month&year=$year"
Invoke-Probe "amount/days"        "https://platform.deepseek.com/api/v0/usage/amount/days?month=$month&year=$year"
Invoke-Probe "usage/hour"         "https://platform.deepseek.com/api/v0/usage/hour?month=$month&year=$year"
