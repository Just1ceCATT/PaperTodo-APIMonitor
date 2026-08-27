using PaperTodo.Plugin.ApiBalanceMonitor.Services;
using Xunit;

namespace PaperTodo.Plugin.ApiBalanceMonitor.LoadTests;

/// <summary>
/// HookTextResolver 的最小单元测试,覆盖 12 个内置工具 + MCP 命名解析 + 大小写不敏感 + 兜底文案。
/// 测试不构造 BalanceSession(STA + WPF 依赖),只测纯静态查表逻辑。
/// </summary>
public sealed class HookTextResolverTests
{
    [Theory]
    [InlineData("Bash", "正在执行命令")]
    [InlineData("Read", "正在读取文件")]
    [InlineData("Write", "正在写入文件")]
    [InlineData("Edit", "正在修改文件")]
    [InlineData("Glob", "正在搜索文件")]
    [InlineData("Grep", "正在搜索代码")]
    [InlineData("WebSearch", "正在搜索网络")]
    [InlineData("WebFetch", "正在获取网页")]
    [InlineData("Task", "正在处理任务")]
    [InlineData("Agent", "正在处理任务")]
    [InlineData("TodoWrite", "正在更新任务")]
    public void ResolvePre_KnownToolName_ReturnsExpectedText(string toolName, string expected)
    {
        Assert.Equal(expected, HookTextResolver.ResolvePre(toolName));
    }

    [Theory]
    [InlineData("Bash", "命令执行完成")]
    [InlineData("Read", "文件读取完成")]
    [InlineData("Write", "文件写入完成")]
    [InlineData("Edit", "文件修改完成")]
    [InlineData("Glob", "文件搜索完成")]
    [InlineData("Grep", "代码搜索完成")]
    [InlineData("WebSearch", "网络搜索完成")]
    [InlineData("WebFetch", "网页获取完成")]
    [InlineData("Task", "任务处理完成")]
    [InlineData("Agent", "任务处理完成")]
    [InlineData("TodoWrite", "任务列表已更新")]
    public void ResolvePost_KnownToolName_ReturnsExpectedText(string toolName, string expected)
    {
        Assert.Equal(expected, HookTextResolver.ResolvePost(toolName));
    }

    [Theory]
    [InlineData(null, "正在使用工具")]
    [InlineData("", "正在使用工具")]
    public void ResolvePre_NullOrEmpty_ReturnsFallbackPre(string? toolName, string expected)
    {
        Assert.Equal(expected, HookTextResolver.ResolvePre(toolName));
    }

    [Theory]
    [InlineData(null, "工具使用完成")]
    [InlineData("", "工具使用完成")]
    public void ResolvePost_NullOrEmpty_ReturnsFallbackPost(string? toolName, string expected)
    {
        Assert.Equal(expected, HookTextResolver.ResolvePost(toolName));
    }

    [Theory]
    [InlineData("RandomTool")]
    [InlineData("NotebookEdit")]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("TodoRead")]
    public void ResolvePre_UnknownToolName_ReturnsFallbackPre(string toolName)
    {
        Assert.Equal("正在使用工具", HookTextResolver.ResolvePre(toolName));
    }

    [Theory]
    [InlineData("RandomTool")]
    [InlineData("NotebookEdit")]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("TodoRead")]
    public void ResolvePost_UnknownToolName_ReturnsFallbackPost(string toolName)
    {
        Assert.Equal("工具使用完成", HookTextResolver.ResolvePost(toolName));
    }

    [Fact]
    public void ResolvePre_McpStandardToolName_ExtractsLastSegment()
    {
        // 标准 mcp__<server>__<tool> 形态:剥离 server 段,只显示 tool。
        Assert.Equal("正在调用 create_entities",
            HookTextResolver.ResolvePre("mcp__memory__create_entities"));
    }

    [Fact]
    public void ResolvePost_McpStandardToolName_ExtractsLastSegment()
    {
        Assert.Equal("create_entities 调用完成",
            HookTextResolver.ResolvePost("mcp__memory__create_entities"));
    }

    [Fact]
    public void ResolvePre_McpPluginScopeToolName_ExtractsLastSegment()
    {
        // 插件作用域 mcp__plugin_<plugin>_<server>__<tool>,按最后一个 "__" 取工具名。
        Assert.Equal("正在调用 query",
            HookTextResolver.ResolvePre("mcp__plugin_my-plugin_db__query"));
    }

    [Fact]
    public void ResolvePre_McpNoDoubleUnderscore_StripsMcpPrefix()
    {
        // mcp__foo 有 "mcp__" 前缀但缺第二个 "__"——IsMcpToolName 仍判定为 MCP,
        // ExtractMcpToolName 用 LastIndexOf("__") 切到 "foo"(idx=3, substring(5)="foo")。
        // 这是设计上"识别 mcp 前缀就尝试提取"的副作用,实际用户 hook 总是带双下划线。
        Assert.Equal("正在调用 foo",
            HookTextResolver.ResolvePre("mcp__foo"));
    }

    [Theory]
    [InlineData("bash", "正在执行命令")]   // 小写
    [InlineData("BASH", "正在执行命令")]   // 大写
    [InlineData("Read", "正在读取文件")]
    [InlineData("READ", "正在读取文件")]
    public void ResolvePre_IsCaseInsensitive(string toolName, string expected)
    {
        // Claude Code 偶尔传小写形态,PreTexts / PostTexts 用 OrdinalIgnoreCase 查表。
        Assert.Equal(expected, HookTextResolver.ResolvePre(toolName));
    }

    [Theory]
    [InlineData("bash", "命令执行完成")]
    [InlineData("EDIT", "文件修改完成")]
    public void ResolvePost_IsCaseInsensitive(string toolName, string expected)
    {
        Assert.Equal(expected, HookTextResolver.ResolvePost(toolName));
    }
}