using System.Globalization;
using System.Net.Http;
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
        var (status, body) = await FetchRawAsync(
            http, url, authToken: token, authBearer: true, platformHeader: platformHeader)
            .ConfigureAwait(false);
        if (status is null or not (>= 200 and < 300) || body == null)
        {
            return null;
        }
        return body;
    }

    /// <summary>
    /// 低层 GET 请求：返回 (HTTP 状态码, 响应体)。不强制 Bearer、不 EnsureSuccessStatusCode。
    /// 失败模式：
    /// - 网络/解析异常：status=null, body=null
    /// - 4xx/5xx：status=实际值, body=响应体（让 Provider 自行决定如何解读）
    ///
    /// 参数：
    /// - authToken=null：不附加 Authorization 头
    /// - authBearer=true：拼 "Bearer &lt;token&gt;"；false：拼 "&lt;token&gt;"（ZhiPu 等不要求 Bearer 的供应商）
    /// - platformHeader=true：附加 x-app-version 头（DeepSeek platform 用）
    /// </summary>
    public static async Task<(int? StatusCode, string? Body)> FetchRawAsync(
        HttpClient http, string url,
        string? authToken = null,
        bool authBearer = true,
        bool platformHeader = false)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(authToken))
            {
                var raw = authBearer ? $"Bearer {authToken}" : authToken;
                request.Headers.TryAddWithoutValidation("Authorization", raw);
            }
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            if (platformHeader)
            {
                request.Headers.TryAddWithoutValidation("x-app-version", PlatformAppVersion);
            }
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return (status, body);
        }
        catch
        {
            return (null, null);
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