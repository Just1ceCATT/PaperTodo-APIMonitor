# 穷举常用端点路径，尝试找到 DeepSeek 用量的小时粒度接口。
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
$day = "{0:D2}" -f $now.Day
$today = "$year-$month-$day"
$yesterday = (Get-Date).AddDays(-1).ToString("yyyy-MM-dd")

$headers = @{
    "Accept"        = "*/*"
    "x-app-version" = "1.0.0"
    "Authorization" = "Bearer $usageToken"
}

$results = @()

function Invoke-Probe($label, $url) {
    $record = [PSCustomObject]@{
        Label = $label
        Url = $url
        Status = "?"
        Body = ""
    }
    try {
        $r = Invoke-WebRequest -Uri $url -Method GET -Headers $headers -TimeoutSec 12 -UseBasicParsing
        $body = $r.Content
        if ($body.Length -gt 220) { $body = $body.Substring(0, 220) }
        $record.Status = [int]$r.StatusCode
        $record.Body = $body
    } catch {
        $code = $null
        if ($_.Exception.Response) { $code = [int]$_.Exception.Response.StatusCode }
        $record.Status = $code
        $record.Body = $_.Exception.Message
    }
    $script:results += $record
}

# 候选主机
$hosts = @(
    "https://platform.deepseek.com",
    "https://api.deepseek.com"
)
# 路径模板（{host} {path} {query}）
$paths = @(
    "/api/v0/usage/amount?month=$month&year=$year&date=$today",
    "/api/v0/usage/amount?month=$month&year=$year&date=$today&hour=0",
    "/api/v0/usage/amount?month=$month&year=$year&date=$today&interval=hour",
    "/api/v0/usage/amount?month=$month&year=$year&interval=hour",
    "/api/v0/usage/amount/hour?month=$month&year=$year&date=$today",
    "/api/v0/usage/amount/days?month=$month&year=$year",
    "/api/v0/usage/details?month=$month&year=$year",
    "/api/v0/usage/detail?month=$month&year=$year&date=$today",
    "/api/v0/usage/list?month=$month&year=$year&date=$today",
    "/api/v0/usage/usage-list?month=$month&year=$year",
    "/api/v0/usage/hour?month=$month&year=$year&date=$today",
    "/api/v0/usage/log?month=$month&year=$year&date=$today",
    "/api/v0/usage/logs?month=$month&year=$year&date=$today",
    "/api/v0/usage/records?month=$month&year=$year&date=$today",
    "/api/v0/usage/breakdown?month=$month&year=$year&date=$today",
    "/api/v0/usage/bill?month=$month&year=$year&date=$today",
    "/api/v0/usage/bills?month=$month&year=$year",
    "/api/v0/usage/tokens?month=$month&year=$year&date=$today",
    "/api/v1/usage?date=$today",
    "/api/v1/usage/daily?date=$today",
    "/api/v1/usage/list?date=$today",
    "/api/v1/billing/usage?date=$today",
    "/api/v0/account/usage?date=$today",
    "/api/v0/account/billing?date=$today",
    "/api/v0/account/quota?date=$today",
    "/api/v0/billing/usage?month=$month&year=$year&date=$today",
    "/api/v0/billing/bills?month=$month&year=$year",
    "/api/v0/billing/hourly?month=$month&year=$year&date=$today"
)

foreach ($h in $hosts) {
    foreach ($p in $paths) {
        $url = "$h$p"
        $label = $h.Replace("https://", "") + " " + ($p.Split("?")[0])
        Invoke-Probe $label $url
    }
}

Write-Host "===== 穷举结果（HTTP 状态 + 响应片段）=====" -ForegroundColor Cyan
Write-Host ("{0,-40} {1,-6} {2}" -f "Path", "HTTP", "Body snippet")
Write-Host ("-" * 100)
foreach ($r in $results) {
    $color = "Red"
    if ($r.Status -eq 200) { $color = "Green" } elseif ($r.Status -eq 0) { $color = "Yellow" } elseif ($r.Status -in @(400,401,403,429)) { $color = "DarkYellow" }
    Write-Host ("{0,-40} {1,-6} {2}" -f $r.Label, $r.Status, $r.Body) -ForegroundColor $color
}
Write-Host ""

# 找出 200 响应中可能含小时数据的（出现 "hour" 或 "timestamp" 键）
Write-Host "===== 200 响应中可能含小时数据（关键词命中）=====" -ForegroundColor Cyan
foreach ($r in $results) {
    if ($r.Status -eq 200) {
        $hit = ($r.Body -match 'hour' -or $r.Body -match 'timestamp' -or $r.Body -match 'time_bucket')
        if ($hit) {
            Write-Host "  [$($r.Label)] 命中关键字" -ForegroundColor Green
            Write-Host "    $($r.Body)"
        }
    }
}