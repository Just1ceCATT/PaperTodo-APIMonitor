using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// HTTP + JSON 通用助手。所有方法 pure 或依赖注入的 HttpClient，与具体业务无关，所有 provider 复用。
/// </summary>
internal static class HttpFetcher
{
    /// <summary>UA 头，所有 DeepSeek / MiniMax 请求都带。</summary>
    public const string UserAgent = "PaperTodo.Plugin.ApiBalanceMonitor/1.0";

    /// <summary>platform.deepseek.com 接口要求的 x-app-version 头。</summary>
    public const string PlatformAppVersion = "1.0.0";

    /// <summary>
    /// 通用 GET + Bearer 请求：成功返回响应体，请求/网络异常返回 null。
    /// platformHeader=true 时附加 x-app-version 头。
    /// </summary>
    public static async Task<string?> FetchJsonAsync(
        HttpClient http, string url, string token, bool platformHeader = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (platformHeader)
            {
                request.Headers.TryAddWithoutValidation("x-app-version", PlatformAppVersion);
            }
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>通用 JsonElement 数字抽取：支持数字与字符串数字。</summary>
    public static double? TryReadNumber(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            return null;
        }
        return ExtractNumber(value);
    }

    public static double? ExtractNumber(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number when value.TryGetDouble(out var n) => n,
        JsonValueKind.String when double.TryParse(
            value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var s) => s,
        _ => null
    };

    /// <summary>是否为计入用量/消费的 token 类型（输入 / 缓存命中 / 缓存未命中 / 输出）。</summary>
    public static bool IsTokenType(string? type) =>
        type is "PROMPT_TOKEN" or "PROMPT_CACHE_HIT_TOKEN" or "PROMPT_CACHE_MISS_TOKEN" or "RESPONSE_TOKEN";
}