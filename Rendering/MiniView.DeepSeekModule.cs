using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// BalanceMiniView 的 DeepSeek 三行卡片 partial:今日消费 / 近 7 日 / 今日消耗。
/// 字段定义在 MiniView.cs,本文件负责 BuildDeepSeekModule + BuildMetricRow + 辅助构造方法。
/// 3 列 → 3 行重构后进一步微调:字号差异化、徽章胶囊化、footer 改 Grid、加分割线、Viewbox 仅包数字。
/// </summary>
internal sealed partial class BalanceMiniView
{
    /// <summary>构建 DeepSeek 三行卡片子树:_dsRootGrid(默认 Collapsed,MiniMax 时不显示)。</summary>
    private void BuildDeepSeekModule()
    {
        // 整体 3 行 StackPanel(垂直),各行自然撑高。
        _dsRootGrid = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 2, 0, 2)
            // 注:StackPanel 不支持 Padding 属性,底部呼吸空间改由 footer 底部 margin 提供(0,4,0,6)。
        };

        // === Row 1:今日消费金额 + 高峰期徽章(pill)+ 涨跌指示 + 主值 ===
        _dsRow1HeaderLeft = MakeLabel("今日消费金额");
        _dsRow1Badge = MakeBadge(out _dsBadgeDot, out _dsBadgeText);
        _dsRow1Badge.Visibility = Visibility.Collapsed; // 默认隐藏,Update 按 IsPeakHour 决定
        var row1Left = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        row1Left.Children.Add(_dsRow1HeaderLeft);
        row1Left.Children.Add(_dsRow1Badge);
        _dsRow1HeaderRight = new TextBlock
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };
        var row1Header = new Grid { IsHitTestVisible = false };
        row1Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row1Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(row1Left, 0);
        Grid.SetColumn(_dsRow1HeaderRight, 1);
        row1Header.Children.Add(row1Left);
        row1Header.Children.Add(_dsRow1HeaderRight);

        _dsRow1Value = MakeLargeValue("—", 22);

        _dsRootGrid.Children.Add(BuildMetricRow(row1Header, _dsRow1Value));

        // 行 1 与行 2 之间的 1px 弱色分割线(类 MiniMax _divider)
        _dsDivider1 = new Border
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _dsRootGrid.Children.Add(_dsDivider1);

        // === Row 2:近 7 日消费 + 日均 + 主值 + sparkline ===
        _dsRow2HeaderLeft = MakeLabel("近 7 日消费");
        _dsRow2HeaderRight = MakeLabel("", align: HorizontalAlignment.Right);
        var row2Header = new Grid { IsHitTestVisible = false };
        row2Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2Header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_dsRow2HeaderLeft, 0);
        Grid.SetColumn(_dsRow2HeaderRight, 1);
        row2Header.Children.Add(_dsRow2HeaderLeft);
        row2Header.Children.Add(_dsRow2HeaderRight);

        _dsRow2Value = MakeLargeValue("—", 14);
        // sparkline 直接放 Grid(Path 自身 Stretch=Uniform 自适应,无需外层 Viewbox)
        _dsSparkline = new Path
        {
            Width = 56,
            Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Right,
            // 底边对齐:与 _dsRow2Value(字号 14,渲染高约 18)底边对齐,
            // 避免 Center 对齐带来的几像素基线偏差。
            VerticalAlignment = VerticalAlignment.Bottom,
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        var row2Value = new Grid { IsHitTestVisible = false };
        row2Value.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2Value.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // _dsRow2Value 也改 Bottom 对齐,确保 14px 文字基线与 sparkline 底边在同一水平线。
        _dsRow2Value.VerticalAlignment = VerticalAlignment.Bottom;
        Grid.SetColumn(_dsRow2Value, 0);
        Grid.SetColumn(_dsSparkline, 1);
        row2Value.Children.Add(_dsRow2Value);
        row2Value.Children.Add(_dsSparkline);

        _dsRootGrid.Children.Add(BuildMetricRow(row2Header, row2Value));

        // 行 2 与行 3 之间的 1px 弱色分割线
        _dsDivider2 = new Border
        {
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _dsRootGrid.Children.Add(_dsDivider2);

        // === Row 3:今日消耗 + 主值(数字 + tokens 后缀) ===
        _dsRow3HeaderLeft = MakeLabel("今日消耗");
        var row3Header = new Grid { IsHitTestVisible = false };
        row3Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row3Header.Children.Add(_dsRow3HeaderLeft);

        _dsRow3ValueNumber = MakeLargeValue("—", 22);
        // "Tokens" 后缀:T 首大写。Margin left=2 与数字保持视觉间隙;
        // top=6 让 11pt 后缀顶部下沉 6px,使后缀 baseline 与 22pt 数字 baseline 重合;
        // (22pt 字高约 28-30px, baseline 距顶部 ~21px; 11pt 字高约 14-16px, baseline 距顶部 ~11px;
        //  差值 10px,但 baseline 下沉视觉上对称约 6-8px 比较和谐,此处取 6)
        _dsRow3ValueSuffix = MakeLabel("Tokens", fontSize: 11, margin: new Thickness(2, 6, 0, 0));
        // "≈X.XX万/亿" 换算:紧贴 Tokens 后方,同 left=2 间隙、同 top=6 baseline 对齐
        _dsRow3ValueEstimate = MakeLabel("", fontSize: 11, margin: new Thickness(2, 6, 0, 0));
        // Viewbox 仅包裹数字(避免后缀长度变化牵连数字缩放)
        _dsRow3NumberBox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Child = _dsRow3ValueNumber
        };
        var row3ValueInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // Bottom 对齐:后缀靠 Margin top 推 baseline,数字按 Viewbox 自然底部贴底
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        row3ValueInner.Children.Add(_dsRow3NumberBox);
        row3ValueInner.Children.Add(_dsRow3ValueSuffix);
        row3ValueInner.Children.Add(_dsRow3ValueEstimate);

        _dsRootGrid.Children.Add(BuildMetricRow(row3Header, row3ValueInner));

        // === Row 3 footer:与 MiniMax _footerRow 一致的"右下角"布局 ===
        // 左下角:缓存命中指示器 _dsCacheRate("缓存命中: x,xxx,xxx Tokens · xx%",Left 对齐)
        // 右下角:刷新图标 _dsCacheIcon + 更新时间指示器 _dsCacheText("更新于 xx:xx",Right 对齐)
        // 整个 footer 的 VerticalAlignment=Bottom、Margin bottom=2,与 MiniMax _footerRow 一致
        _dsCacheIcon = BuildRefreshGlyph();
        _dsCacheText = MakeLabel("更新于 --:--:--");
        var footRight = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            // 右下角:水平右对齐,垂直贴底
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        footRight.Children.Add(_dsCacheIcon);
        footRight.Children.Add(_dsCacheText);
        _dsCacheRate = MakeLabel("", align: HorizontalAlignment.Left);
        _dsRow3FootRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            // 与 MiniMax _footerRow 一致:VerticalAlignment=Bottom
            VerticalAlignment = VerticalAlignment.Bottom,
            // 顶部 4px 间距(让 Row 3 value 与 footer 分开),底部 2px(与 MiniMax _footerRow 一致)
            Margin = new Thickness(0, 4, 0, 2),
            IsHitTestVisible = false
        };
        _dsRow3FootRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _dsRow3FootRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // 左下角:缓存命中指示器;右下角:刷新图标 + 更新时间指示器
        Grid.SetColumn(_dsCacheRate, 0);
        Grid.SetColumn(footRight, 1);
        _dsRow3FootRow.Children.Add(_dsCacheRate);
        _dsRow3FootRow.Children.Add(footRight);
        _dsRootGrid.Children.Add(_dsRow3FootRow);

        // _dsRootGrid 由构造函数统一加入 _root(与 _maxRootGrid 平级,互斥显示),
        // 此处不再重复添加,避免 "Specified element is already the logical child of another element" 异常。
    }

    /// <summary>单行容器:header 行 + value 行,StackPanel 垂直堆叠;底部 4px 间距。</summary>
    private static StackPanel BuildMetricRow(FrameworkElement header, FrameworkElement value)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
            IsHitTestVisible = false
        };
        row.Children.Add(header);
        row.Children.Add(value);
        return row;
    }

    /// <summary>
    /// 通用标签 TextBlock。默认无 margin(消除行尾 6px 死白边);
    /// TextWrapping=NoWrap 防止内容超宽时被自动换行截断;CharacterEllipsis 在
    /// 容器空间不足时尾部补 "…" 而不是直接切掉字符。
    /// </summary>
    private static TextBlock MakeLabel(
        string text,
        HorizontalAlignment align = HorizontalAlignment.Left,
        double fontSize = 12,
        Thickness? margin = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = margin ?? new Thickness(0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
    }

    /// <summary>主值 TextBlock:主文字、SemiBold、字号由调用方指定;
    /// NoWrap + CharacterEllipsis:不被自动换行,空间不足时尾部补 "…" 而非直接切字符。</summary>
    private static TextBlock MakeLargeValue(string initial, double fontSize = 20)
    {
        return new TextBlock
        {
            Text = initial,
            FontSize = fontSize,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
    }

    /// <summary>
    /// 高峰期徽章:pill 包裹(圆角 Border + StackPanel 内容),含 5×5 圆点 + "高峰期" 文字。
    /// Background 由 ApplyTheme 注入 _dsBadgeBackgroundBrush(橙调半透明)。
    /// CornerRadius=4 而非更大值:在小尺寸(约 50×15)Border 上,过大的圆角会让圆角区
    /// 与文字高度比例失衡,造成背景渲染异常(视觉上像被裁掉)。
    /// </summary>
    private static Border MakeBadge(out Ellipse dot, out TextBlock text)
    {
        dot = new Ellipse
        {
            Width = 5,
            Height = 5,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 4, 0),
            IsHitTestVisible = false
        };
        text = new TextBlock
        {
            Text = "高峰期",
            FontSize = 10,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Children = { dot, text }
        };
        return new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 0, 0),
            IsHitTestVisible = false
            // Background 由 ApplyTheme 注入 _dsBadgeBackgroundBrush
        };
    }
}