using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;
using PaperTodo.Plugin.ApiBalanceMonitor.Payload;

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

    // DeepSeek 模块专用 brush 缓存:sparkline / 涨跌指示 / 高峰徽章。
    private Brush _dsAccentBrush = Brushes.Orange;
    private Brush _dsSafeBrush = Brushes.LimeGreen;
    private Brush _dsDangerBrush = Brushes.OrangeRed;
    private Brush _dsBadgeBackgroundBrush = Brushes.LightSalmon;

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

    // DeepSeek 子树容器:_dsRootGrid(DeepSeek 模式显示),3 行 StackPanel。
    internal StackPanel _dsRootGrid = null!;

    // Row 1:今日消费金额 + 高峰期徽章(pill)+ 涨跌指示 + 主值
    internal TextBlock _dsRow1HeaderLeft = null!;
    internal Border _dsRow1Badge = null!;
    internal Ellipse _dsBadgeDot = null!;
    internal TextBlock _dsBadgeText = null!;
    internal TextBlock _dsRow1HeaderRight = null!;
    internal TextBlock _dsRow1Value = null!;

    // 行间分割线(类 MiniMax _divider,1px 弱色)
    internal Border _dsDivider1 = null!;
    internal Border _dsDivider2 = null!;

    // Row 2:近 7 日消费 + 日均 + 主值 + sparkline
    internal TextBlock _dsRow2HeaderLeft = null!;
    internal TextBlock _dsRow2HeaderRight = null!;
    internal TextBlock _dsRow2Value = null!;
    internal Path _dsSparkline = null!;
    internal double _dsSparklineWidth;
    internal double _dsSparklineHeight;

    // Row 3:今日消耗 + tokens + 缓存命中脚注
    internal TextBlock _dsRow3HeaderLeft = null!;
    internal Viewbox _dsRow3NumberBox = null!;
    internal TextBlock _dsRow3ValueNumber = null!;
    internal TextBlock _dsRow3ValueSuffix = null!;
    internal Grid _dsRow3FootRow = null!;
    internal Path _dsCacheIcon = null!;
    internal TextBlock _dsCacheText = null!;
    internal TextBlock _dsCacheRate = null!;
    // Row 3 主值尾部的"≈X.XX万/亿"换算文本,与 Tokens 后缀同一字号和 baseline 对齐。
    internal TextBlock _dsRow3ValueEstimate = null!;

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

        // 百分比:主文字、字号 13、Medium 加粗突出(取消斜体,数字部分用正体)。
        _hourlyPercent.FontFamily = font;
        _hourlyPercent.FontSize = 13;
        _hourlyPercent.FontWeight = FontWeights.Medium;
        _hourlyPercent.Foreground = _textBrush;
        _weeklyPercent.FontFamily = font;
        _weeklyPercent.FontSize = 13;
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
        // 纤细风:从 1.1 降到 1.0,跟新双侧开口弧配合让图标更轻盈。
        _refreshGlyph.StrokeThickness = 1.0;

        // 5h / 周分割线颜色:复用进度条底色,弱视觉分组。
        _divider.Background = _barTrackBrush;

        // 倒计时:弱文字、字号 11(最弱,与小浮窗协调);margin 由 hourlyResetRow 容器统一管理。
        _hourlyReset.FontFamily = font;
        _hourlyReset.FontSize = 11;
        _hourlyReset.Foreground = _weakBrush;
        _weeklyReset.FontFamily = font;
        _weeklyReset.FontSize = 11;
        _weeklyReset.Foreground = _weakBrush;

        // 底部 footer:弱文字,字号 10(比 11 更小,作为时间戳装饰进一步弱化);正体。
        _footer.FontFamily = font;
        _footer.FontSize = 10;
        _footer.Foreground = _weakBrush;

        // === DeepSeek 三行卡片字号设置 ===
        // DeepSeek 模块专用 brush 缓存:sparkline 用主题 accent(暖色);涨跌指示用固定 safe/danger 色。
        _dsAccentBrush = ToBrush(theme.AccentColor, "#FF9800");
        _dsSafeBrush = ToBrush(theme.IsDark ? "#78d47d" : "#4CAF50", "#4CAF50");
        _dsDangerBrush = ToBrush(theme.IsDark ? "#e28787" : "#F44336", "#F44336");
        // Badge pill 背景:深 25% / 浅 15% 橙调半透明,与 5h 橙(#FF9800)视觉同源。
        _dsBadgeBackgroundBrush = ToBrush(theme.IsDark ? "#40FF9800" : "#26FF9800", "#26FF9800");
        _dsRow1Badge.Background = _dsBadgeBackgroundBrush;

        // 行间分割线:复用 _barTrackBrush,与 MiniMax _divider 同源。
        _dsDivider1.Background = _barTrackBrush;
        _dsDivider2.Background = _barTrackBrush;

        // 标签(行 1 / 2 / 3 头部左 + 右):弱文字 12,Normal
        var dsLabelSize = 12;
        _dsRow1HeaderLeft.FontFamily = font; _dsRow1HeaderLeft.FontSize = dsLabelSize;
        _dsRow1HeaderLeft.FontWeight = FontWeights.Normal; _dsRow1HeaderLeft.Foreground = _weakBrush;
        _dsRow2HeaderLeft.FontFamily = font; _dsRow2HeaderLeft.FontSize = dsLabelSize;
        _dsRow2HeaderLeft.FontWeight = FontWeights.Normal; _dsRow2HeaderLeft.Foreground = _weakBrush;
        _dsRow2HeaderRight.FontFamily = font; _dsRow2HeaderRight.FontSize = dsLabelSize;
        _dsRow2HeaderRight.FontWeight = FontWeights.Normal; _dsRow2HeaderRight.Foreground = _weakBrush;
        _dsRow3HeaderLeft.FontFamily = font; _dsRow3HeaderLeft.FontSize = dsLabelSize;
        _dsRow3HeaderLeft.FontWeight = FontWeights.Normal; _dsRow3HeaderLeft.Foreground = _weakBrush;

        // 涨跌指示(_dsRow1HeaderRight):字号 12、SemiBold;颜色由 Update 按方向重写
        _dsRow1HeaderRight.FontFamily = font; _dsRow1HeaderRight.FontSize = dsLabelSize;
        _dsRow1HeaderRight.FontWeight = FontWeights.SemiBold;

        // 高峰期徽章:pill 背景由 _dsBadgeBackgroundBrush 注入;圆点 5px 用主题 accent;文字"高峰期" 10
        _dsBadgeDot.Fill = _dsAccentBrush;
        _dsBadgeText.FontFamily = font; _dsBadgeText.FontSize = 10;
        _dsBadgeText.FontWeight = FontWeights.Medium; _dsBadgeText.Foreground = _dsAccentBrush;

        // 主值差异化:Row1=22 给"今日消费金额"加视觉权重;Row2=20 让位 sparkline;
        // Row3=22 突出 tokens 数字。SemiBold,主文字色。
        _dsRow1Value.FontFamily = font; _dsRow1Value.FontSize = 22;
        _dsRow1Value.FontWeight = FontWeights.SemiBold; _dsRow1Value.Foreground = _textBrush;
        _dsRow2Value.FontFamily = font; _dsRow2Value.FontSize = 14;
        _dsRow2Value.FontWeight = FontWeights.SemiBold; _dsRow2Value.Foreground = _textBrush;
        _dsRow3ValueNumber.FontFamily = font; _dsRow3ValueNumber.FontSize = 18;
        _dsRow3ValueNumber.FontWeight = FontWeights.SemiBold; _dsRow3ValueNumber.Foreground = _textBrush;
        // tokens 后缀:字号 11,Normal,弱色
        _dsRow3ValueSuffix.FontFamily = font; _dsRow3ValueSuffix.FontSize = 11;
        _dsRow3ValueSuffix.FontWeight = FontWeights.Normal; _dsRow3ValueSuffix.Foreground = _weakBrush;
        // "≈X.XX万/亿" 换算:与 Tokens 后缀同字号同色,共享 baseline
        _dsRow3ValueEstimate.FontFamily = font; _dsRow3ValueEstimate.FontSize = 11;
        _dsRow3ValueEstimate.FontWeight = FontWeights.Normal; _dsRow3ValueEstimate.Foreground = _weakBrush;

        // 缓存命中脚注:字号 11,Normal,弱色
        var dsFootSize = 11;
        _dsCacheText.FontFamily = font; _dsCacheText.FontSize = dsFootSize;
        _dsCacheText.FontWeight = FontWeights.Normal; _dsCacheText.Foreground = _weakBrush;
        _dsCacheRate.FontFamily = font; _dsCacheRate.FontSize = dsFootSize;
        _dsCacheRate.FontWeight = FontWeights.Normal; _dsCacheRate.Foreground = _weakBrush;
        // 缓存命中图标 stroke 跟随弱色
        _dsCacheIcon.Stroke = _weakBrush;
        _dsCacheIcon.StrokeThickness = 1.1;

        // Sparkline 设计尺寸(实际由 Path 的 StreamGeometry 坐标决定,这里记录用于 Update 重算)
        _dsSparklineWidth = 56;
        _dsSparklineHeight = 22;
        _dsSparkline.Stroke = _dsAccentBrush;
        _dsSparkline.StrokeThickness = 1.4;
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
                // Row 1:今日消费金额 + 高峰期徽章 + 涨跌指示 + 主值
                _dsRow1Value.Text = string.IsNullOrEmpty(ds.TodayCostText) ? "—" : ds.TodayCostText;
                _dsRow1Badge.Visibility = ds.IsPeakHour ? Visibility.Visible : Visibility.Collapsed;
                if (ds.ChangeDirection is null)
                {
                    _dsRow1HeaderRight.Visibility = Visibility.Collapsed;
                }
                else
                {
                    _dsRow1HeaderRight.Visibility = Visibility.Visible;
                    var arrow = ds.ChangeDirection == "up" ? "↑"
                        : (ds.ChangeDirection == "down" ? "↓" : "→");
                    _dsRow1HeaderRight.Text = arrow + ds.ChangePercent.ToString("0.0", CultureInfo.CurrentCulture) + "%";
                    _dsRow1HeaderRight.Foreground = ds.ChangeDirection == "up" ? _dsDangerBrush
                        : (ds.ChangeDirection == "down" ? _dsSafeBrush : _weakBrush);
                }

                // Row 2:近 7 日消费 + 日均 + 主值 + sparkline
                _dsRow2Value.Text = string.IsNullOrEmpty(ds.Cost7dText) ? "—" : ds.Cost7dText;
                _dsRow2HeaderRight.Text = ds.Cost7dFoot ?? "";
                _dsSparkline.Data = BuildSparklineGeometry(ds.Sparkline, _dsSparklineWidth, _dsSparklineHeight);

                // Row 3 footer(与 MiniMax _footerRow 同款右下角布局):
                // 右下角 = 刷新图标(_dsCacheIcon)+ 更新时间指示器(_dsCacheText,"更新于 xx:xx")
                // 左下角 = 缓存命中指示器(_dsCacheRate,"缓存命中: x,xxx,xxx Tokens · xx%")
                var hasTokens = !string.IsNullOrEmpty(ds.TodayTokensText) && ds.TodayTokensText != "—";
                _dsRow3ValueNumber.Text = string.IsNullOrEmpty(ds.TodayTokensText) ? "—" : ds.TodayTokensText;
                _dsRow3ValueSuffix.Visibility = hasTokens ? Visibility.Visible : Visibility.Collapsed;
                // "≈X.XX万/亿" 换算:与 Tokens 后缀同生命周期,有 tokens 时显示,无则隐藏
                var estimate = Format.FormatEstimate(ds.TodayTokensText);
                if (hasTokens && !string.IsNullOrEmpty(estimate))
                {
                    _dsRow3ValueEstimate.Text = " " + estimate;  // 前导空格与 Tokens 隔开
                    _dsRow3ValueEstimate.Visibility = Visibility.Visible;
                }
                else
                {
                    _dsRow3ValueEstimate.Text = "";
                    _dsRow3ValueEstimate.Visibility = Visibility.Collapsed;
                }
                // 右下角:更新时间指示器,与 MiniMax _footerRow 的 _footer 行为一致,始终显示时间戳
                _dsCacheText.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                // 左下角:缓存命中率指示器,只显示命中率本身,不再展示具体命中 token 数(避免重复 + Row 3 已显 Tokens)
                _dsCacheRate.Text = hasTokens && ds.CacheRateText != null
                    ? "缓存命中 " + ds.CacheRateText
                    : "";
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

    /// <summary>
    /// 由 7 个日消费额构造 sparkline 折线的 StreamGeometry,坐标按 (width, height) 归一化。
    /// 值序列长度不足 2 或全 0 时返回空几何(冻结),Path 控件自然不绘制。
    /// </summary>
    private static StreamGeometry BuildSparklineGeometry(double[] values, double width, double height)
    {
        var sg = new StreamGeometry();
        if (values == null || values.Length < 2 || width <= 0 || height <= 0)
        {
            sg.Freeze();
            return sg;
        }
        var max = 0.0;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] > max)
            {
                max = values[i];
            }
        }
        if (max <= 0)
        {
            sg.Freeze();
            return sg;
        }
        var stepX = width / (values.Length - 1);
        using (var ctx = sg.Open())
        {
            var firstY = height - (values[0] / max) * height;
            ctx.BeginFigure(new Point(0, firstY), false, false);
            for (var i = 1; i < values.Length; i++)
            {
                var p = new Point(i * stepX, height - (values[i] / max) * height);
                ctx.LineTo(p, true, false);
            }
        }
        sg.Freeze();
        return sg;
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

    /// <summary>DeepSeek 三行卡片数据:所有文本已格式化,view 不再做除法。</summary>
    internal readonly record struct DeepSeekMetrics(
        string TodayCostText,         // "¥0.08" 或 ""
        string? ChangeDirection,      // "up" | "down" | "flat" | null
        double ChangePercent,        // 3.1(无方向时为 0)
        string Cost7dText,            // "¥8.42" 或 ""
        string Cost7dFoot,            // "日均 ¥1.20" 或 ""
        string TodayTokensText,       // "25,325" 或 ""
        string TodayHitText,          // "25,088"(不带"缓存命中:"前缀)
        string? CacheRateText,        // "0.00%" 或 null
        double[] Sparkline,           // 7 个日消费额,缺日补 0
        bool IsPeakHour);
}