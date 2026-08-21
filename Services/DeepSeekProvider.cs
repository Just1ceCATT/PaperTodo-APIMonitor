using System.Net.Http;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// DeepSeek /user/balance 接口客户端：拉余额 + 解析响应。
/// 注入 HttpClient 以保持超时 / 连接 共享。
/// </summary>
internal sealed class DeepSeekProvider
{
    private const string DeepSeekBalanceUrl = "https://api.deepseek.com/user/balance";

    private readonly HttpClient _http;

    public DeepSeekProvider(HttpClient http)
    {
        _http = http;
    }

    /// <summary>拉取余额。失败返回 BalanceSnapshot.Error。</summary>
    public async Task<BalanceSnapshot> FetchBalanceAsync(string apiKey, string currencySymbol)
    {
        var body = await HttpFetcher.FetchJsonAsync(_http, DeepSeekBalanceUrl, apiKey);
        return body == null ? BalanceSnapshot.Error("请求失败") : ParseResponse(body, currencySymbol);
    }

    /// <summary>
    /// 解析 DeepSeek /user/balance 响应。
    ///   { "is_sufficient": true, "balance_infos": [
    ///     { "currency": "CNY", "total_balance": "84.00",
    ///       "granted_balance": "0.00", "topped_up_balance": "84.00" },
    ///     { "currency": "USD", ... } ] }
    /// total_balance 是字符串数字；按 currencySymbol 选币种（¥→CNY，$→USD），
    /// 找不到匹配时回退到第一个非零余额；不计算百分比。
    /// </summary>
    private static BalanceSnapshot ParseResponse(string body, string currencySymbol)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return BalanceSnapshot.Error("响应不是合法 JSON");
        }

        if (!root.TryGetProperty("balance_infos", out var infos) ||
            infos.ValueKind != JsonValueKind.Array)
        {
            return BalanceSnapshot.Error("缺少 balance_infos");
        }
        var targetCurrency = ColorPalette.MapCurrencySymbolToCode(currencySymbol);
        double? picked = null;
        double? firstNonZero = null;
        foreach (var info in infos.EnumerateArray())
        {
            if (info.ValueKind != JsonValueKind.Object) continue;
            var amount = HttpFetcher.TryReadNumber(info, "total_balance");
            if (!amount.HasValue) continue;
            var code = info.TryGetProperty("currency", out var cv) &&
                cv.ValueKind == JsonValueKind.String
                    ? cv.GetString()
                    : null;
            if (targetCurrency != null &&
                string.Equals(code, targetCurrency, StringComparison.OrdinalIgnoreCase))
            {
                picked = amount;
                break;
            }
            if (!firstNonZero.HasValue && amount.Value > 0)
            {
                firstNonZero = amount;
            }
        }
        if (!picked.HasValue) picked = firstNonZero;
        if (picked.HasValue)
        {
            return BalanceSnapshot.Ok(picked.Value);
        }
        return BalanceSnapshot.Error("未找到 total_balance");
    }
}