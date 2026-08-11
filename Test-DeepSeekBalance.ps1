# Tests the DeepSeek /user/balance endpoint.
# Usage:
#   powershell -File Test-DeepSeekBalance.ps1 -ApiKey "sk-xxx"
#   or:  $env:DEEPSEEK_API_KEY = "sk-xxx"; powershell -File Test-DeepSeekBalance.ps1

param(
    [string]$ApiKey,
    [string]$Url = "https://api.deepseek.com/user/balance"
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    $ApiKey = $env:DEEPSEEK_API_KEY
}
if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    Write-Host "Pass the token via -ApiKey or env DEEPSEEK_API_KEY." -ForegroundColor Yellow
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
    $response = Invoke-WebRequest `
        -Uri $Url `
        -Method GET `
        -Headers $headers `
        -TimeoutSec 15 `
        -UseBasicParsing
    Write-Host "HTTP $($response.StatusCode) $($response.StatusDescription)" -ForegroundColor Green
    Write-Host ""
    Write-Host "Raw response:" -ForegroundColor Cyan
    Write-Host $response.Content
    Write-Host ""

    try {
        $json = $response.Content | ConvertFrom-Json
        Write-Host "Parsed:" -ForegroundColor Cyan
        Write-Host ("  is_sufficient = {0}" -f $json.is_sufficient)
        if ($json.balance_infos) {
            foreach ($info in $json.balance_infos) {
                Write-Host ("  {0}: total_balance = {1} (granted={2}, topped_up={3})" -f `
                    $info.currency, $info.total_balance, $info.granted_balance, $info.topped_up_balance)
            }
        } else {
            Write-Host "  (no balance_infos field)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "JSON parse failed: $($_.Exception.Message)" -ForegroundColor Yellow
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
    Write-Host "Request failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($statusCode) { Write-Host "HTTP $statusCode" -ForegroundColor Red }
    if ($body) { Write-Host $body }
    exit 1
}
