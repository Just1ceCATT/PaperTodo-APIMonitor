using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Services;

/// <summary>
/// 一次 hook overlay 完整显示方案——不可变 record,所有 view 从同一 plan 渲染,
/// 避免 view 之间互相猜对方状态。
///
/// 由 <see cref="HookOverlayController.BuildPlan"/> 派生,view 不修改。
/// </summary>
internal sealed record HookOverlayPlan(
    HookOverlayKind Kind,            // spinner vs color + 具体类型
    string Text,                      // 胶囊 PlainText(已含 tool-aware 文案或 Color overlay 固定文案)
    string ToolTip,                   // ToolTip(已含余额 + status + hook summary)
    string? RingColorHex,             // Color overlay 圆环前景色;null = spinner(用 SpinnerBadgeBrush)
    HookGlyphKind Glyph,              // Phase C 末显示的对勾 / 问号
    int? DurationSeconds,             // Color overlay 倒计时;null = spinner 持续型
    double PreferredWidth);           // host PlainText 冻结宽度,避免 overlay 切换时胶囊宽度抖动

/// <summary>
/// Phase C 末显示的视觉符号。原本在 Ring / Dot 视图各有一个独立的 private enum,
/// 合并到此处统一,让 Controller / view 共用同一种"对勾 vs 问号"分类。
/// </summary>
internal enum HookGlyphKind
{
    None,
    Check,        // 绿对勾(StopImage / FailureImage)
    Question,     // 黄色问号(PermissionImage)
}