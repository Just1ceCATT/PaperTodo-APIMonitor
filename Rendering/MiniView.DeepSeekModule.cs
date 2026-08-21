using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// BalanceMiniView 的 DeepSeek 三行卡片 partial:今日消费 / 近 7 日 / 今日消耗。
/// 字段定义在 MiniView.cs,本文件负责 BuildDeepSeekModule + BuildMetricRow + BuildSparklineGeometry。
/// 3 列 → 3 行重构:每行 header + value,Row 2 含 sparkline,Row 3 含缓存命中脚注。
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
        };

        // === Row 1:今日消费金额 + 高峰期徽章 + 涨跌指示 + 主值 ===
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

        _dsRow1Value = MakeLargeValue("—");

        _dsRootGrid.Children.Add(BuildMetricRow(row1Header, _dsRow1Value));

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

        _dsRow2Value = MakeLargeValue("—");
        // sparkline:Path + Viewbox 包裹,容器宽度自适应
        _dsSparkline = new Path
        {
            Width = 56,
            Height = 22,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            StrokeThickness = 1.4,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false
        };
        var sparkWrap = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        sparkWrap.Child = _dsSparkline;
        var row2Value = new Grid { IsHitTestVisible = false, Margin = new Thickness(0, 0, 0, 0) };
        row2Value.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row2Value.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_dsRow2Value, 0);
        Grid.SetColumn(sparkWrap, 1);
        row2Value.Children.Add(_dsRow2Value);
        row2Value.Children.Add(sparkWrap);

        _dsRootGrid.Children.Add(BuildMetricRow(row2Header, row2Value));

        // === Row 3:今日消耗 + 主值(数字 + tokens 后缀) ===
        _dsRow3HeaderLeft = MakeLabel("今日消耗");
        var row3Header = new Grid { IsHitTestVisible = false };
        row3Header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row3Header.Children.Add(_dsRow3HeaderLeft);

        _dsRow3ValueNumber = MakeLargeValue("—");
        _dsRow3ValueSuffix = MakeLabel("tokens");
        var row3ValueInner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false
        };
        row3ValueInner.Children.Add(_dsRow3ValueNumber);
        row3ValueInner.Children.Add(_dsRow3ValueSuffix);
        _dsRow3ValueRow = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _dsRow3ValueRow.Child = row3ValueInner;

        _dsRootGrid.Children.Add(BuildMetricRow(row3Header, _dsRow3ValueRow));

        // === Row 3 footer:缓存命中中 + tokens · rate% ===
        _dsCacheIcon = BuildRefreshGlyph();
        _dsCacheText = MakeLabel("缓存命中中");
        var footLeft = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        footLeft.Children.Add(_dsCacheIcon);
        footLeft.Children.Add(_dsCacheText);
        _dsCacheRate = MakeLabel("", align: HorizontalAlignment.Right);
        _dsRow3FootRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
            IsHitTestVisible = false
        };
        _dsRow3FootRow.Children.Add(footLeft);
        // 占位 Spacer 把 _dsCacheRate 推到右侧
        var spacer = new TextBlock { Text = " ", Width = 4, IsHitTestVisible = false };
        _dsRow3FootRow.Children.Add(spacer);
        _dsRow3FootRow.Children.Add(_dsCacheRate);
        _dsRootGrid.Children.Add(_dsRow3FootRow);

        // _dsRootGrid 由构造函数统一加入 _root(与 _maxRootGrid 平级,互斥显示),
        // 此处不再重复添加,避免 "Specified element is already the logical child of another element" 异常。
    }

    /// <summary>单行容器:header 行 + value 行,StackPanel 垂直堆叠。</summary>
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

    /// <summary>通用标签 TextBlock:弱文字、字号由 ApplyTheme 注入、Normal 字重。</summary>
    private static TextBlock MakeLabel(string text, HorizontalAlignment align = HorizontalAlignment.Left)
    {
        return new TextBlock
        {
            Text = text,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false
        };
    }

    /// <summary>主值 TextBlock:主文字、SemiBold、占位"—"。</summary>
    private static TextBlock MakeLargeValue(string initial)
    {
        return new TextBlock
        {
            Text = initial,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.None,
            IsHitTestVisible = false
        };
    }

    /// <summary>高峰期徽章:8×8 圆点 + 文字"高峰期",StackPanel 横向布局。</summary>
    private static StackPanel MakeBadge(out Ellipse dot, out TextBlock text)
    {
        dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 6, 0),
            IsHitTestVisible = false
        };
        text = new TextBlock
        {
            Text = "高峰期",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 0),
            IsHitTestVisible = false,
            Children = { dot, text }
        };
    }
}