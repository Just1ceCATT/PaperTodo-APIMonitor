using PaperTodo.Plugin;
using PaperTodo.Plugin.ApiBalanceMonitor.Session;

namespace PaperTodo.Plugin.ApiBalanceMonitor;

/// <summary>
/// 余额监测插件：拉取 DeepSeek /user/balance 接口，
/// 在胶囊中显示「绿/黄/红圆环 + 货币 + 余额 + 可选百分比」。
///
/// 协议 1.7 自渲染胶囊视图（IPaperCapsuleViewProvider），1.8 自渲染 MiniView
/// （IPaperMiniViewProvider）；2.0 启用 full settings page（advancedSettings +
/// settingCategories 分组标题）。设置页由宿主绘制，鉴权 Key 明文写入插件数据文件。
///
/// 所有业务逻辑在 Session/BalanceSession.cs;其余按职责拆到 Models / Services /
/// Payload / Rendering / WebPanel 子命名空间。
///
/// 元数据(版本号 / 协议号 / capabilities / requires 等)从 plugin.json 读取,
/// 不再以 C# 属性形式定义(适配 plugin protocol 2.1)。
/// </summary>
public sealed class ApiBalanceMonitorPlugin : IPaperBodyPlugin
{
    public IPaperBodySession Create(PaperBodyContext context) =>
        new BalanceSession(context);

    /// <summary>
    /// 旧 v1 没有 state 字段；升级后清空回退到默认 deepseek，让用户在监视面板中按需切换。
    /// </summary>
    public string MigrateState(string stateJson, int fromVersion) => "{}";
}