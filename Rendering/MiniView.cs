using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// 1.8 自定义边缘预览视图(由 IPaperMiniViewProvider 返回)。
/// partial 拆分为 3 文件:本文件是主体构造 + 主题 + Update 入口 + DTO record,
/// MiniMax 双模块构建在 MiniView.MaxModule.cs,DeepSeek 三列构建在 MiniView.DeepSeekModule.cs。
///
/// _owner 反向引用已解耦:字体覆盖通过构造参数 fontOverride 捕获,运行时变更通过
/// SetFontOverride(string) 推送;view 完全不知道 Session 的存在,易于测试。
/// </summary>
internal sealed partial class BalanceMiniView : Border
{
    private readonly Grid _root;
    private PaperBodyTheme _theme;
    private string _fontOverride;

    // FontFamily 缓存:theme.FontFamily 是 Source 字符串,按字符串相等判断避免重复构造。
    private FontFamily? _cachedFontFamily;
    private string? _cachedFontFamilySource;

    // 颜色缓存:ApplyTheme 重建后冻结,可跨线程共享。
    private Brush _textBrush = Brushes.Black;
    private Brush _weakBrush = Brushes.Gray;
    private Brush _accentBrush = Brushes.Blue;
    private Brush _barTrackBrush = Brushes.LightGray;

    // 进度条 fill 固定为分类色(5h=橙 #FF9800,周=蓝 #2196F3),冻结后跨线程共享。
    // 分类色不代表风险,与 RiskClassifier 无关;契约见 AGENTS.md "不引入冗余"段。
    private readonly Brush _hourlyFillBrush;
    private readonly Brush _weeklyFillBrush;
    // 记录最近 ratio:SizeChanged 事件按此值重算 fill.Width,与进度条 fill 颜色无关。
    private double _lastHourlyRatio = -1;
    private double _lastWeeklyRatio = -1;

    // MiniMax 模块控件引用(非 readonly:partial 拆分后字段赋值发生在 BuildMiniMaxModule(),
    // 该方法从构造函数调用,但 readonly 字段不允许在方法中赋值。
    // 实际仅在构造函数内调用一次,行为等价于 readonly。)
    internal TextBlock _hourlyLabel = null!;
    internal TextBlock _hourlyPercent = null!;
    internal Grid _hourlyBarGrid = null!;
    internal Rectangle _hourlyBarTrack = null!;
    internal Rectangle _hourlyFill = null!;
    internal TextBlock _hourlyReset = null!;
    internal Border _divider = null!;
    internal TextBlock _weeklyLabel = null!;
    internal TextBlock _weeklyPercent = null!;
    internal Grid _weeklyBarGrid = null!;
    internal Rectangle _weeklyBarTrack = null!;
    internal Rectangle _weeklyFill = null!;
    internal TextBlock _weeklyReset = null!;
    internal TextBlock _footer = null!;

    // 倒计时/footer 行容器:左图标 + 右文字,Grid 复合行。
    internal Grid _hourlyResetRow = null!;
    internal Grid _weeklyResetRow = null!;
    internal Grid _footerRow = null!;

    // 时钟 / 刷新图标(Path + StreamGeometry 自绘,跟随主题 _weakBrush)。
    internal Path _hourlyClockGlyph = null!;
    internal Path _weeklyClockGlyph = null!;
    internal Path _refreshGlyph = null!;

    // MiniMax 双模块容器:_maxRootGrid(MiniMax 模式显示)。
    internal StackPanel _hourlyStack = null!;
    internal StackPanel _weeklyStack = null!;
    internal Grid _maxRootGrid = null!;

    // DeepSeek 子树容器:_dsRootGrid(DeepSeek 模式显示)。
    internal Grid _dsRootGrid = null!;
    internal Grid _dsCol1 = null!;
    internal Border _dsCol1Divider = null!;
    internal TextBlock _dsCol1Label = null!;
    internal TextBlock _dsCol1Value = null!;
    internal TextBlock _dsCol1Foot = null!;

    internal Grid _dsCol2 = null!;
    internal Border _dsCol2Divider = null!;
    internal TextBlock _dsCol2Label = null!;
    internal TextBlock _dsCol2Value = null!;
    internal TextBlock _dsCol2Foot = null!;

    internal Grid _dsCol3 = null!;
    internal Border _dsCol3Divider = null!;
    internal TextBlock _dsCol3Label = null!;
    internal TextBlock _dsCol3ValueNumber = null!;
    internal TextBlock _dsCol3ValueSuffix = null!;
    internal TextBlock _dsCol3Foot1 = null!;   // ≈ 5.0万
    internal TextBlock _dsCol3Foot2 = null!;   // 缓存命中: ...
    internal TextBlock _dsCol3Foot3 = null!;   // 缓存命中率 ...

    /// <param name="fontOverride">字体覆盖源,来自插件设置 miniViewFontFamily,空字符串表示跟随主题。</param>
    public BalanceMiniView(string fontOverride, PaperMiniViewContext context)
    {
        _fontOverride = fontOverride;
        _theme = context.Theme;

        // 自身作为圆角容器:暗色下 12% 黑、浅色下 6% 黑,与胶囊外壳视觉分离。
        CornerRadius = new CornerRadius(10);
        Margin = new Thickness(4);
        // Padding 14/8/14/8 平衡圆角呼吸与内容空间:5h/周两个模块垂直内容在 scale=1.3 时
        // 自然高度约 79px,row 可用高度约 85px,留 6px 余量避免上下两部分溢出重叠。
        Padding = new Thickness(14, 8, 14, 8);
        Background = BuildContainerBackground(_theme.IsDark);
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 进度条 fill 固定分类色:5h 橙 #FF9800、周 蓝 #2196F3,冻结后跨线程共享。
        var hourlyFill = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        hourlyFill.Freeze();
        var weeklyFill = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
        weeklyFill.Freeze();
        _hourlyFillBrush = hourlyFill;
        _weeklyFillBrush = weeklyFill;

        // 内部 1 行 Grid:同时容纳 MiniMax 双模块(_maxRootGrid)与 DeepSeek 三列(_dsRootGrid)。
        // 两个子树互斥:provider 是 MiniMax 时 _maxRootGrid 可见 _dsRootGrid 隐藏;DeepSeek 反之。
        // 不再用跨行 SetRowSpan(3),避免 Collapsed 状态下 Grid layout 引擎在 hourlyStack/divider/weeklyStack 同 Grid 下产生
        // row 分配冲突,导致 MiniMax 模式视觉错乱。
        _root = new Grid();
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        BuildMiniMaxModule();
        BuildDeepSeekModule();

        // 把 _maxRootGrid 与 _dsRootGrid 加入 _root(MiniMax 模式默认显示,_dsRootGrid 默认 Collapsed)。
        Grid.SetRow(_maxRootGrid, 0);
        Grid.SetRow(_dsRootGrid, 0);
        _root.Children.Add(_maxRootGrid);
        _root.Children.Add(_dsRootGrid);

        Child = _root;
    }

    /// <summary>
    /// Session 在 OnSettingsChanged 中调用,推送最新的字体覆盖源。
    /// 留空表示跟随主题,非空时优先使用。
    /// </summary>
    public void SetFontOverride(string fontOverride)
    {
        _fontOverride = fontOverride;
        ApplyTheme(_theme);
    }

    /// <summary>主题切换:重建 Brush 缓存、字号、字体、容器与进度条配色。</summary>
    public void ApplyTheme(PaperBodyTheme theme)
    {
        _theme = theme;
        Background = BuildContainerBackground(theme.IsDark);
        _textBrush = ToBrush(theme.TextColor, "#202020");
        _weakBrush = ToBrush(theme.WeakTextColor, "#707070");
        _accentBrush = ToBrush(theme.AccentColor, "#B07A31");
        _barTrackBrush = ToBrush(theme.IsDark ? "#28FFFFFF" : "#22000000", "#22000000");

        // 字体源:插件设置 miniViewFontFamily 非空时覆盖主题字体,留空跟随主题。
        var fontSource = !string.IsNullOrEmpty(_fontOverride)
            ? _fontOverride
            : theme.FontFamily;
        var font = ResolveFontFamily(fontSource);
        var scale = Math.Clamp(theme.FontScale, 0.85, 1.3);

        // 头部标签:弱文字、字号 12(主动放弃 × scale,小尺寸浮窗内字号偏小更精致)。
        _hourlyLabel.FontFamily = font;
        _hourlyLabel.FontSize = 12;
        _hourlyLabel.FontWeight = FontWeights.Medium;
        _hourlyLabel.Foreground = _weakBrush;
        _weeklyLabel.FontFamily = font;
        _weeklyLabel.FontSize = 12;
        _weeklyLabel.FontWeight = FontWeights.Medium;
        _weeklyLabel.Foreground = _weakBrush;

        // 百分比:主文字、字号 13、Medium 加粗突出(数字部分保留斜体)。
        _hourlyPercent.FontFamily = font;
        _hourlyPercent.FontSize = 13;
        _hourlyPercent.FontStyle = FontStyles.Italic;
        _hourlyPercent.FontWeight = FontWeights.Medium;
        _hourlyPercent.Foreground = _textBrush;
        _weeklyPercent.FontFamily = font;
        _weeklyPercent.FontSize = 13;
        _weeklyPercent.FontStyle = FontStyles.Italic;
        _weeklyPercent.FontWeight = FontWeights.Medium;
        _weeklyPercent.Foreground = _textBrush;

        // 进度条 track 底色随主题;fill 固定分类色(5h 橙 / 周 蓝),与主题无关,
        // 在 ApplyTheme 中一次性绑定避免每次 Update 都重写。
        _hourlyBarTrack.Fill = _barTrackBrush;
        _weeklyBarTrack.Fill = _barTrackBrush;
        _hourlyFill.Fill = _hourlyFillBrush;
        _weeklyFill.Fill = _weeklyFillBrush;

        // 时钟 / 刷新图标 stroke 跟随弱文字色,粗细 1.1 保证清晰但不喧宾夺主。
        _hourlyClockGlyph.Stroke = _weakBrush;
        _hourlyClockGlyph.StrokeThickness = 1.1;
        _weeklyClockGlyph.Stroke = _weakBrush;
        _weeklyClockGlyph.StrokeThickness = 1.1;
        _refreshGlyph.Stroke = _weakBrush;
        _refreshGlyph.StrokeThickness = 1.1;

        // 5h / 周分割线颜色:复用进度条底色,弱视觉分组。
        _divider.Background = _barTrackBrush;

        // 倒计时:弱文字、字号 11(最弱,与小浮窗协调);margin 由 hourlyResetRow 容器统一管理。
        _hourlyReset.FontFamily = font;
        _hourlyReset.FontSize = 11;
        _hourlyReset.FontStyle = FontStyles.Italic;
        _hourlyReset.Foreground = _weakBrush;
        _weeklyReset.FontFamily = font;
        _weeklyReset.FontSize = 11;
        _weeklyReset.FontStyle = FontStyles.Italic;
        _weeklyReset.Foreground = _weakBrush;

        // 底部 footer:弱文字、字号 11(时间戳装饰);数字斜体。
        _footer.FontFamily = font;
        _footer.FontSize = 11;
        _footer.FontStyle = FontStyles.Italic;
        _footer.Foreground = _weakBrush;

        // === DeepSeek 三列卡片字号设置 ===
        // Label(列标题):弱文字 11×scale Normal,让"今日消费金额"等副标轻盈。
        var dsLabelSize = 11 * scale;
        _dsCol1Label.FontFamily = font; _dsCol1Label.FontSize = dsLabelSize;
        _dsCol1Label.FontWeight = FontWeights.Normal; _dsCol1Label.Foreground = _weakBrush;
        _dsCol2Label.FontFamily = font; _dsCol2Label.FontSize = dsLabelSize;
        _dsCol2Label.FontWeight = FontWeights.Normal; _dsCol2Label.Foreground = _weakBrush;
        _dsCol3Label.FontFamily = font; _dsCol3Label.FontSize = dsLabelSize;
        _dsCol3Label.FontWeight = FontWeights.Normal; _dsCol3Label.Foreground = _weakBrush;

        // Value(主值):主文字 22×scale SemiBold,数字斜体加半粗
        var dsValueSize = 22 * scale;
        _dsCol1Value.FontFamily = font; _dsCol1Value.FontSize = dsValueSize;
        _dsCol1Value.FontWeight = FontWeights.SemiBold; _dsCol1Value.Foreground = _textBrush;
        _dsCol2Value.FontFamily = font; _dsCol2Value.FontSize = dsValueSize;
        _dsCol2Value.FontWeight = FontWeights.SemiBold; _dsCol2Value.Foreground = _textBrush;
        // 列3 valueNumber 单独 20×scale:列3 内容多一行(Tokens 后缀+三脚注),
        // 主数字缩小避免 "数字+Tokens" 横向溢出导致 TextTrimming 截断 Tokens。
        _dsCol3ValueNumber.FontFamily = font; _dsCol3ValueNumber.FontSize = 20 * scale;
        _dsCol3ValueNumber.FontWeight = FontWeights.SemiBold; _dsCol3ValueNumber.Foreground = _textBrush;
        _dsCol3ValueSuffix.FontFamily = font; _dsCol3ValueSuffix.FontSize = 10 * scale;
        _dsCol3ValueSuffix.FontWeight = FontWeights.Normal; _dsCol3ValueSuffix.Foreground = _weakBrush;

        // Foot(弱文字):极弱文字 10.5×scale
        var dsFootSize = 10.5 * scale;
        _dsCol1Foot.FontFamily = font; _dsCol1Foot.FontSize = dsFootSize;
        _dsCol1Foot.Foreground = _weakBrush;
        _dsCol2Foot.FontFamily = font; _dsCol2Foot.FontSize = dsFootSize;
        _dsCol2Foot.Foreground = _weakBrush;
        _dsCol3Foot1.FontFamily = font; _dsCol3Foot1.FontSize = dsFootSize;
        _dsCol3Foot1.Foreground = _weakBrush;
        _dsCol3Foot2.FontFamily = font; _dsCol3Foot2.FontSize = dsFootSize;
        _dsCol3Foot2.Foreground = _weakBrush;
        _dsCol3Foot3.FontFamily = font; _dsCol3Foot3.FontSize = dsFootSize;
        _dsCol3Foot3.Foreground = _weakBrush;

        // 列分隔线颜色:复用 _barTrackBrush(三列卡片统一节奏)
        _dsCol1Divider.Background = _barTrackBrush;
        _dsCol2Divider.Background = _barTrackBrush;
        _dsCol3Divider.Background = _barTrackBrush;
    }

    /// <summary>
    /// 刷新 MiniView 全部显示。按 snapshot.Provider 分发到 MiniMax 双模块或 DeepSeek 三列。
    /// MiniMax:Percent 已经是 0-100 的剩余比例,进度条 fill 固定为灰色,按 ratio 收窄宽度;
    /// 5h 倒计时用 "x 时 y 分",周倒计时用 "x 天 x 时 x 分"。
    /// DeepSeek:所有文本由调用方格式化好,直接显示;hasTokens=false 时 Token 后缀与三行 foot 隐藏。
    /// </summary>
    public void Update(MiniViewSnapshot snapshot, string statusText)
    {
        if (string.Equals(snapshot.Provider, PaperState.MiniMax, StringComparison.Ordinal))
        {
            // 整个 _maxRootGrid 显示,_dsRootGrid 隐藏,互斥且不互相影响 layout。
            _maxRootGrid.Visibility = Visibility.Visible;
            _dsRootGrid.Visibility = Visibility.Collapsed;

            if (snapshot.MaxData is { } max)
            {
                _hourlyPercent.Text = FormatPercent(max.Percent);
                var hourlyRatio = Math.Clamp(max.Percent / 100.0, 0, 1);
                _lastHourlyRatio = hourlyRatio;
                _hourlyFill.Width = Math.Max(0, _hourlyBarGrid.ActualWidth * hourlyRatio);
                _hourlyReset.Text = string.IsNullOrEmpty(statusText)
                    ? FormatRemaining(max.RemainingHours, includeDays: false)
                    : statusText;

                _weeklyPercent.Text = FormatPercent(max.WeeklyPercent);
                var weeklyRatio = Math.Clamp(max.WeeklyPercent / 100.0, 0, 1);
                _lastWeeklyRatio = weeklyRatio;
                _weeklyFill.Width = Math.Max(0, _weeklyBarGrid.ActualWidth * weeklyRatio);
                _weeklyReset.Text = FormatRemaining(max.WeeklyHours, includeDays: true);

                _footer.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
            }
        }
        else if (string.Equals(snapshot.Provider, PaperState.DeepSeek, StringComparison.Ordinal))
        {
            _maxRootGrid.Visibility = Visibility.Collapsed;
            _dsRootGrid.Visibility = Visibility.Visible;

            if (snapshot.DeepSeekData is { } ds)
            {
                _dsCol1Value.Text = string.IsNullOrEmpty(ds.TodayCostText) ? "—" : ds.TodayCostText;
                _dsCol1Foot.Text = string.IsNullOrEmpty(ds.CostTodayFoot) ? "" : ds.CostTodayFoot;

                _dsCol2Value.Text = string.IsNullOrEmpty(ds.Cost7dText) ? "—" : ds.Cost7dText;
                _dsCol2Foot.Text = string.IsNullOrEmpty(ds.Cost7dFoot) ? "—" : ds.Cost7dFoot;

                var hasTokens = ds.TodayTokensText != "—";
                _dsCol3ValueNumber.Text = ds.TodayTokensText;
                _dsCol3ValueSuffix.Visibility = hasTokens ? Visibility.Visible : Visibility.Collapsed;
                _dsCol3Foot1.Text = hasTokens ? ds.TodayTokensWan : "";
                _dsCol3Foot1.Visibility = hasTokens ? Visibility.Visible : Visibility.Collapsed;
                _dsCol3Foot2.Text = hasTokens ? ds.TodayHitText : "";
                _dsCol3Foot2.Visibility = hasTokens ? Visibility.Visible : Visibility.Collapsed;
                _dsCol3Foot3.Text = ds.TodayCacheRate ?? "";
                _dsCol3Foot3.Visibility = !string.IsNullOrEmpty(ds.TodayCacheRate) ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private FontFamily ResolveFontFamily(string source)
    {
        if (_cachedFontFamily != null && string.Equals(_cachedFontFamilySource, source, StringComparison.Ordinal))
        {
            return _cachedFontFamily;
        }
        _cachedFontFamily = new FontFamily(source);
        _cachedFontFamilySource = source;
        return _cachedFontFamily;
    }

    private static SolidColorBrush ToBrush(string value, string fallback)
    {
        SolidColorBrush brush;
        try
        {
            brush = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(value)!);
        }
        catch
        {
            try
            {
                brush = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(fallback)!);
            }
            catch
            {
                brush = new SolidColorBrush(Colors.Gray);
            }
        }
        // 冻结 brush:让 WPF 渲染系统走快路径,并允许跨线程共享。
        brush.Freeze();
        return brush;
    }

    /// <summary>1.8 边缘预览视图统一快照:按 Provider 路由到 MiniMax 双进度条或 DeepSeek 三列卡片。</summary>
    internal readonly record struct MiniViewSnapshot(
        string Provider,
        MiniMaxQuota? MaxData,
        DeepSeekMetrics? DeepSeekData);

    /// <summary>MiniMax 双模块数据(每五小时 + 周额度)。</summary>
    internal readonly record struct MiniMaxQuota(
        double Percent,
        double RemainingHours,
        double WeeklyPercent,
        double WeeklyHours);

    /// <summary>DeepSeek 三列卡片数据:所有文本已格式化,view 不再做除法。</summary>
    internal readonly record struct DeepSeekMetrics(
        string TodayCostText,     // "¥0.08" 或 "—"
        string CostTodayFoot,     // "相较昨日 ↑12.0%" 或 ""
        string Cost7dText,        // "¥12.35" 或 "—"
        string Cost7dFoot,        // "日均 ¥1.76"
        string TodayTokensText,   // "50,336" 或 "—"
        string TodayTokensWan,    // "≈ 5.0万"
        string TodayHitText,      // "缓存命中: 25,088 Tokens"
        string? TodayCacheRate);  // "50.10%" 或 null(隐藏该行)
}