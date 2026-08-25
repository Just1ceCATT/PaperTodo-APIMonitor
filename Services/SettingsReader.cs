using System.Globalization;
using System.Text.Json;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// JSON helpers 与 per-paper state 解析。所有方法 pure，无外部依赖，可单元测试。
/// 默认值与读取顺序与原 BalanceSession.ReadSettings / ReadState / SerializeState 1:1 对应。
/// </summary>
internal static class SettingsReader
{
    // ---------------- 设置解析 ----------------

    /// <summary>
    /// 解析全局设置 JSON，按 provider 选对应 Key；旧版单一 apiKey 字段作为 DeepSeek 的兼容迁移来源。
    /// 任何字段缺失或解析异常都回退到默认值，不抛异常。
    /// </summary>
    public static BalanceSettings ReadSettings(string? json, string provider)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            var root = doc.RootElement;
            var deepseekKey = ReadString(root, "deepseekApiKey", "");
            if (string.IsNullOrEmpty(deepseekKey))
            {
                deepseekKey = ReadString(root, "apiKey", "");
            }
            var minimaxKey = ReadString(root, "minimaxApiKey", "");
            var opencodeKey = ReadString(root, "opencodeApiKey", "");
            var zhipuKey = ReadString(root, "zhipuApiKey", "");
            var mimoKey = ReadString(root, "mimoApiKey", "");
            var codexKey = ReadString(root, "codexApiKey", "");
            var kimiKey = ReadString(root, "kimiApiKey", "");
            var apiKey = provider switch
            {
                PaperState.MiniMax => minimaxKey,
                PaperState.OpenCode => opencodeKey,
                PaperState.ZhiPu => zhipuKey,
                PaperState.MiMo => mimoKey,
                PaperState.CodeX => codexKey,
                PaperState.Kimi => kimiKey,
                _ => deepseekKey
            };
            return new BalanceSettings(
                apiKey,
                ReadString(root, "usageToken", ""),
                ReadInt(root, "pollSeconds", 60),
                ReadString(root, "currencySymbol", "¥"),
                ReadDouble(root, "balanceThreshold", 20.0),
                ReadBool(root, "showPercentage", true),
                ReadString(root, "miniViewFontFamily", ""),
                ReadBool(root, "disableRing", false),
                zhipuKey,
                mimoKey,
                codexKey,
                ReadBool(root, "disableDotBreath", false),
                ReadString(root, "kimiApiKey", ""),
                NormalizeZhiPuRegion(ReadString(root, "zhipuRegion", "global")),
                NormalizeZhiPuPlanType(ReadString(root, "zhipuPlanType", "personal")),
                NormalizeHooksPort(ReadInt(root, "hooksPort", 17890)),
                ReadBool(root, "notifyOnStop", true),
                ReadBool(root, "notifyOnPreToolUse", true),
                ReadBool(root, "notifyOnPostToolUse", true),
                ReadBool(root, "notifyOnPermissionRequest", true),
                ReadBool(root, "notifyOnPostToolUseFailure", true),
                NormalizeOverlaySeconds(ReadInt(root, "hookOverlayDurationSeconds", 3)));
        }
        catch
        {
            // 解析失败时 22 个字段全部回退到默认值,保证 BalanceSettings 字段顺序与 record 一致。
            return new BalanceSettings(
                "",         // ApiKey
                "",         // UsageToken
                60,         // PollSeconds
                "¥",        // CurrencySymbol
                20.0,       // BalanceThreshold
                true,       // ShowPercentage
                "",         // MiniViewFontFamily
                false,      // DisableRing
                "",         // ZhiPuApiKey
                "",         // MiMoApiKey
                "",         // CodeXApiKey
                false,      // DisableDotBreath
                "",         // KimiApiKey
                "global",   // ZhiPuRegion
                "personal", // ZhiPuPlanType
                17890,      // HooksPort
                true,       // NotifyOnStop
                true,       // NotifyOnPreToolUse
                true,       // NotifyOnPostToolUse
                true,       // NotifyOnPermissionRequest
                true,       // NotifyOnPostToolUseFailure
                3);         // HookOverlayDurationSeconds
        }
    }

    /// <summary>HookOverlayDurationSeconds 合法值兜底：范围 [1, 30]，非法或缺失一律 3。</summary>
    private static int NormalizeOverlaySeconds(int raw) =>
        raw is >= 1 and <= 30 ? raw : 3;

    /// <summary>HooksPort 合法值兜底：合法范围 [1024, 65535]，非法或缺失一律 17890。</summary>
    private static int NormalizeHooksPort(int raw) =>
        raw is >= 1024 and <= 65535 ? raw : 17890;

    /// <summary>ZhiPuRegion 合法值兜底：非法或缺失一律 global。</summary>
    private static string NormalizeZhiPuRegion(string raw) =>
        string.Equals(raw, "china", StringComparison.OrdinalIgnoreCase) ? "china" : "global";

    /// <summary>ZhiPuPlanType 合法值兜底：非法或缺失一律 personal。</summary>
    private static string NormalizeZhiPuPlanType(string raw) =>
        string.Equals(raw, "team", StringComparison.OrdinalIgnoreCase) ? "team" : "personal";

    public static string ReadString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    public static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetInt32(out var n))
        {
            return n;
        }
        return fallback;
    }

    public static double ReadDouble(JsonElement root, string name, double fallback)
    {
        if (root.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) &&
                double.IsFinite(n))
            {
                return n;
            }
            if (v.ValueKind == JsonValueKind.String &&
                double.TryParse(v.GetString(), NumberStyles.Any,
                    CultureInfo.InvariantCulture, out var s) &&
                double.IsFinite(s))
            {
                return s;
            }
        }
        return fallback;
    }

    public static bool ReadBool(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    // ---------------- Per-paper state 解析/序列化 ----------------

    /// <summary>
    /// 读取 per-paper state：当前选哪个供应商。非法值回退到默认。
    /// </summary>
    public static PaperState ReadState(string? json)
    {
        var fallback = new PaperState(PaperState.DefaultProvider);
        if (string.IsNullOrWhiteSpace(json))
        {
            return fallback;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var p = ReadString(doc.RootElement, "provider", PaperState.DefaultProvider);
            return new PaperState(IsValidProvider(p) ? p : PaperState.DefaultProvider);
        }
        catch
        {
            return fallback;
        }
    }

    public static string SerializeState(PaperState state) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["provider"] = state.Provider });

    /// <summary>Provider 白名单，用于 WebView2 switchProvider 消息校验。</summary>
    public static bool IsValidProvider(string p) =>
        p == PaperState.DeepSeek || p == PaperState.MiniMax || p == PaperState.OpenCode ||
        p == PaperState.ZhiPu || p == PaperState.Kimi || p == PaperState.MiMo || p == PaperState.CodeX;
}