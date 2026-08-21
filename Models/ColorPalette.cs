namespace PaperTodo.Plugin.ApiBalanceMonitor.Models;

/// <summary>
/// 通用调色板：货币符号 → DeepSeek balance_in[*[*].currency 字段代码。
/// 找不到映射时返回 null，BalanceSession.ParseResponse 会回退到首个非零余额。
///
/// 风险色（RiskGreen/Yellow/Orange/Red/Gray）和风险档位阈值（Warming/Danger/Overrun）
/// 在 Step 3 抽离到 Services/RiskClassifier.cs 中，与 ClassifyRisk / RingColor / RiskColor 一起管理，
/// 避免"阈值常量与色板"被拆散到两个文件后改一边漏一边。
/// </summary>
internal static class ColorPalette
{
    /// <summary>把设置里的货币符号映射为 DeepSeek balance_in[*[*].currency 的币种代码。</summary>
    public static string? MapCurrencySymbolToCode(string symbol) =>
        symbol switch
        {
            "¥" => "CNY",
            "$" => "USD",
            "€" => "EUR",
            "£" => "GBP",
            "₩" => "KRW",
            _ => null,
        };
}