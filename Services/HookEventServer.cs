using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// Claude Code hook HTTP 接收端。
///
/// 数据流：用户 ~/.claude/settings.json 配置的 hook 脚本从 stdin 读 Claude Code JSON 后，
/// 立即 POST 到 http://127.0.0.1:{port}/hook；本服务接收 → 解析 → 触发 HookReceived 事件。
///
/// 时延目标：loopback HTTP + 解析 + Dispatcher.Invoke < 50ms（典型 5-15ms）。
///
/// 端口冲突：默认 17890；占用时尝试 +1 共 5 次。失败则不启动（不影响余额主功能）。
///
/// 鉴权：仅监听 127.0.0.1，不暴露外部网络。请求体必须含 hook_event_name 字段才被识别。
/// </summary>
internal sealed class HookEventServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private int _port;
    private bool _running;
    private bool _disposed;

    /// <summary>实际监听端口（启动后才知道）。</summary>
    public int ActualPort => _port;

    /// <summary>接收到的 hook 事件：调用方在 BalanceSession 内订阅并 marshal 到 UI 线程。</summary>
    public event Action<HookEventPayload>? HookReceived;

    /// <summary>
    /// 启动服务器。port=0 时由 OS 分配；否则使用给定端口并处理冲突。
    /// 返回 true=启动成功，false=所有候选端口都被占用（调用方应降级为非 hook 模式）。
    /// </summary>
    public bool Start(int preferredPort = 17890)
    {
        if (_running) return true;

        // 最多尝试 5 个端口
        foreach (var offset in EnumeratePortCandidates(preferredPort))
        {
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add($"http://127.0.0.1:{offset}/hook/");
                _listener.Start();
                _port = offset;
                _running = true;
                _ = Task.Run(ListenLoopAsync);
                return true;
            }
            catch (HttpListenerException)
            {
                // 端口被占用，继续尝试下一个
                continue;
            }
            catch
            {
                // 其他异常（权限等）直接放弃
                return false;
            }
        }
        return false;
    }

    /// <summary>优先端口 +1 ~ +4 范围探测。</summary>
    private static IEnumerable<int> EnumeratePortCandidates(int preferred)
    {
        yield return preferred;
        for (var i = 1; i <= 4; i++)
        {
            yield return preferred + i;
        }
    }

    private async Task ListenLoopAsync()
    {
        while (_running && !_disposed)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                // 服务器被 Stop / Dispose 中断 → 退出循环
                return;
            }

            // 后台处理请求，立即释放主循环去接下一个
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (string.Equals(ctx.Request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
            {
                // GET /hook/ → 健康检查端点，便于用户/脚本确认服务可达
                WriteJson(ctx, 200, new { ok = true, port = _port });
                return;
            }

            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                WriteJson(ctx, 405, new { error = "method_not_allowed" });
                return;
            }

            var payload = ParseHookPayload(body);
            if (payload is null)
            {
                WriteJson(ctx, 400, new { error = "invalid_payload" });
                return;
            }

            // 立刻回 200，避免 hook 脚本超时等待
            WriteJson(ctx, 200, new { ok = true });

            // 触发事件，让调用方（BalanceSession）marshal 到 UI 线程
            try { HookReceived?.Invoke(payload); }
            catch { /* 订阅者异常吞掉，避免污染服务器循环 */ }
        }
        catch
        {
            try { WriteJson(ctx, 500, new { error = "internal" }); } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    /// <summary>
    /// 解析 hook POST body：从 Claude Code hook 脚本转发的 JSON 中提取事件摘要。
    /// 输入格式：用户脚本自定义。最少需要 hook_event_name 字段。
    /// 推荐字段：tool_name、summary（脚本自合成）。
    /// </summary>
    private static HookEventPayload? ParseHookPayload(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("hook_event_name", out var nameEl) ||
                nameEl.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var eventName = nameEl.GetString() ?? "";
            string? toolName = null;
            if (root.TryGetProperty("tool_name", out var toolEl) &&
                toolEl.ValueKind == JsonValueKind.String)
            {
                toolName = toolEl.GetString();
            }
            var summary = root.TryGetProperty("summary", out var sumEl) &&
                          sumEl.ValueKind == JsonValueKind.String
                ? sumEl.GetString() ?? ""
                : BuildDefaultSummary(eventName, toolName);
            return new HookEventPayload(
                EventName: eventName,
                ToolName: toolName,
                Summary: summary,
                ReceivedAt: DateTime.Now);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>未提供 summary 时按 event/tool 拼一句简短中文描述，供胶囊 ToolTip 使用。</summary>
    private static string BuildDefaultSummary(string eventName, string? toolName) =>
        (eventName, toolName) switch
        {
            ("PostToolUse", var t) => $"Claude: {t ?? "Tool"} 调用完成",
            ("PreToolUse", var t) => $"Claude: 即将调用 {t ?? "Tool"}",
            ("UserPromptSubmit", _) => "Claude: 收到用户提示",
            ("Stop", _) => "Claude: 已停止响应",
            ("StopFailure", _) => "Claude: 响应异常中止",
            ("Notification", _) => "Claude: 需要注意",
            ("SessionStart", _) => "Claude: 会话启动",
            ("SessionEnd", _) => "Claude: 会话结束",
            _ => $"Claude: {eventName}"
        };

    private static void WriteJson(HttpListenerContext ctx, int status, object body)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        var json = JsonSerializer.Serialize(body);
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>HTTP 服务器与订阅者之间的载荷 DTO，独立于 Models.HookEvent（避免循环引用）。</summary>
internal sealed record HookEventPayload(
    string EventName,
    string? ToolName,
    string Summary,
    DateTime ReceivedAt);
