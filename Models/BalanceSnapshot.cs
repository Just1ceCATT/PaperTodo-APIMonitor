namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 余额快照：Remaining=NaN 表示无数据，HasRemaining 单独显式区分"未拉取"与"拉取成功但余额为 0"。
/// StatusText 用于胶囊 ToolTip 第二行 / WebView2 payload.status 字段；为空表示无错误。
/// </summary>
internal sealed record BalanceSnapshot(
    double Remaining,
    bool HasRemaining,
    string StatusText)
{
    /// <summary>未拉取/无 Key 等"还没数据"状态。</summary>
    public static BalanceSnapshot Empty(string status) =>
        new(double.NaN, false, status);

    /// <summary>已尝试拉取但失败（网络/解析/缺字段）；status 自动自动加 "错误：" 前缀。</summary>
    public static BalanceSnapshot Error(string status) =>
        new(double.NaN, false, "错误：" + status);

    /// <summary>拉取成功，HasRemaining 由 Remaining 是否为 NaN 推导。</summary>
    public static BalanceSnapshot Ok(double remaining) =>
        new(remaining, !double.IsNaN(remaining), string.Empty);
}