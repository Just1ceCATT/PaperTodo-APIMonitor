namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// 工具名 → 胶囊 overlay 文案 的集中映射。
///
/// 把"准备调用工具 / 文件编辑完成"两个写死文案,改成按 tool_name 派生的动词短句,
/// 让用户在胶囊上一眼看出 Claude 当前在做什么工具操作。
///
/// 使用方:
///   - <see cref="HookEventServer.BuildDefaultSummary"/>:ToolTip 第二行 tool-aware 摘要
///   - <see cref="BalanceSession.ApplyHookOverlayToCapsules"/>:spinner 胶囊 PlainText
///   - <see cref="BalanceSession.ApplyPendingOverlayToView"/>:view 补发 spinner 文本
///
/// 大小写不敏感查表(Claude Code 偶尔传 "read" 小写形态)。
/// MCP 工具(`mcp__<server>__<tool>`)按最后一个 "__" 切分取工具名部分。
/// </summary>
internal static class HookTextResolver
{
    // PreToolUse: 触发瞬间,持续型 spinner。统一 "正在 X" 形态。
    private static readonly IReadOnlyDictionary<string, string> PreTexts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bash"] = "正在执行命令",
            ["Read"] = "正在读取文件",
            ["Write"] = "正在写入文件",
            ["Edit"] = "正在修改文件",
            ["Glob"] = "正在搜索文件",
            ["Grep"] = "正在搜索代码",
            ["WebSearch"] = "正在搜索网络",
            ["WebFetch"] = "正在获取网页",
            // Claude Code 把 subagent 派发叫 Task,我们叫 Agent。共享同一文案。
            ["Task"] = "正在处理任务",
            ["Agent"] = "正在处理任务",
            ["TodoWrite"] = "正在更新任务",
        };

    // PostToolUse: 完成瞬间,持续型 spinner。统一 "X 完成" 过去式形态。
    private static readonly IReadOnlyDictionary<string, string> PostTexts =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Bash"] = "命令执行完成",
            ["Read"] = "文件读取完成",
            ["Write"] = "文件写入完成",
            ["Edit"] = "文件修改完成",
            ["Glob"] = "文件搜索完成",
            ["Grep"] = "代码搜索完成",
            ["WebSearch"] = "网络搜索完成",
            ["WebFetch"] = "网页获取完成",
            ["Task"] = "任务处理完成",
            ["Agent"] = "任务处理完成",
            ["TodoWrite"] = "任务列表已更新",
        };

    // 兜底文案。toolName 为 null/空/未知/非 MCP 命名时统一返回。
    private const string FallbackPreText = "正在使用工具";
    private const string FallbackPostText = "工具使用完成";

    /// <summary>PreToolUse 触发时返回 "正在 X" 形态。toolName=null/空/未知时返回 "正在使用工具"。</summary>
    public static string ResolvePre(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return FallbackPreText;
        if (IsMcpToolName(toolName))
        {
            return "正在调用 " + ExtractMcpToolName(toolName);
        }
        return PreTexts.TryGetValue(toolName, out var text) ? text : FallbackPreText;
    }

    /// <summary>PostToolUse 触发时返回 "X 完成" 过去式形态。</summary>
    public static string ResolvePost(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName)) return FallbackPostText;
        if (IsMcpToolName(toolName))
        {
            return ExtractMcpToolName(toolName) + " 调用完成";
        }
        return PostTexts.TryGetValue(toolName, out var text) ? text : FallbackPostText;
    }

    /// <summary>
    /// MCP 工具命名形态 `mcp__&lt;server&gt;__&lt;tool&gt;`(插件作用域可能有更多段,如
    /// `mcp__plugin_xxx_yyy__tool`)。规则:取最后一个 "__" 之后的内容。
    /// 非 MCP 命名(无 "__" 或 shape 不对)回退原 toolName。
    /// </summary>
    private static string ExtractMcpToolName(string toolName)
    {
        var idx = toolName.LastIndexOf("__", StringComparison.Ordinal);
        if (idx < 0 || idx + 2 >= toolName.Length)
        {
            return toolName;
        }
        return toolName.Substring(idx + 2);
    }

    private static bool IsMcpToolName(string toolName) =>
        toolName.StartsWith("mcp__", StringComparison.Ordinal);
}