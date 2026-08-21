using PaperTodo.Plugin;
using PaperTodo.Plugin.ApiBalanceMonitor.Session;

namespace PaperTodo.Plugin.ApiBalanceMonitor;

/// <summary>
/// 余额监测插件：拉取 DeepSeek /user/balance 接口，
/// 在胶囊中显示「绿/黄/红圆环 + 货币 + 余额 + 可选百分比」。
///
/// 协议 1.7 自渲染胶囊视图（IPaperCapsuleViewProvider），1.8 自渲染 MiniView
/// （IPaperMiniViewProvider）；设置页由宿主绘制，鉴权 Key 明文写入插件数据文件。
///
/// 所有业务逻辑在 Session/BalanceSession.cs;其余按职责拆到 Models / Services /
/// Payload / Rendering / WebPanel 子命名空间。
/// </summary>
public sealed class ApiBalanceMonitorPlugin : IPaperBodyPlugin
{
    public string Id => "api.balance.monitor";
    public string DisplayName => "API 余额监测";
    public string Description =>
        "通过 DeepSeek /user/balance 接口拉取余额，按余额提醒阈值显示不同颜色的圆环。" +
        "模型供应商在每张纸的监视面板顶部切换；各供应商 Key 独立存储于全局设置。";
    public Version Version => new(1, 2, 0);
    public string ApiVersion => "1.8";
    public int StateVersion => 2;
    public PaperBodyCapabilities Capabilities => PaperBodyCapabilities.None;
    public PaperBodyRuntimeRequirements RuntimeRequirements =>
        PaperBodyRuntimeRequirements.BackgroundUpdates;

    public IPaperBodySession Create(PaperBodyContext context) =>
        new BalanceSession(context);

    /// <summary>
    /// 旧 v1 没有 state 字段；升级后清空回退到默认 deepseek，让用户在监视面板中按需切换。
    /// </summary>
    public string MigrateState(string stateJson, int fromVersion) => "{}";
}