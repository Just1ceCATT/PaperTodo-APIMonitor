using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// BalanceMiniView 的 DeepSeek 三列卡片 partial:今日消费金额 / 近 7 日 / 今日消耗。
/// 字段定义在 MiniView.cs,本文件负责 BuildDeepSeekModule / BuildDeepSeekColumn /
/// BuildDeepSeekColumnWithTokens。BuildDeepSeekColumnWithTokens 第三列含 Viewbox 缩放 +
/// 三行脚注,代码量较大,因此本 partial 与 MaxModule 体积接近。
/// </summary>
internal sealed partial class BalanceMiniView
{
    /// <summary>构建 DeepSeek 三列卡片子树:_dsRootGrid(默认 Collapsed,MiniMax 时不显示)。</summary>
    private void BuildDeepSeekModule()
    {
        // 整体横跨 _root 的 3 行,垂直居中。
        _dsRootGrid = new Grid();
        _dsRootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _dsRootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _dsRootGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _dsRootGrid.VerticalAlignment = VerticalAlignment.Center;
        _dsRootGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
        _dsRootGrid.IsHitTestVisible = false;
        _dsRootGrid.Visibility = Visibility.Collapsed;

        BuildDeepSeekColumn(
            out _dsCol1, out _dsCol1Divider, out _dsCol1Label, out _dsCol1Value, out _dsCol1Foot,
            primaryInitial: "—", subText: "今日消费金额", footInitial: "",
            showDivider: false);
        BuildDeepSeekColumn(
            out _dsCol2, out _dsCol2Divider, out _dsCol2Label, out _dsCol2Value, out _dsCol2Foot,
            primaryInitial: "—", subText: "近 7 日", footInitial: "—",
            showDivider: true);
        BuildDeepSeekColumnWithTokens(
            out _dsCol3, out _dsCol3Divider, out _dsCol3Label, out _dsCol3ValueNumber,
            out _dsCol3ValueSuffix, out _dsCol3Foot1, out _dsCol3Foot2, out _dsCol3Foot3);

        Grid.SetColumn(_dsCol1, 0); _dsRootGrid.Children.Add(_dsCol1);
        Grid.SetColumn(_dsCol2, 1); _dsRootGrid.Children.Add(_dsCol2);
        Grid.SetColumn(_dsCol3, 2); _dsRootGrid.Children.Add(_dsCol3);

        // _dsRootGrid 是 _root 的直接子节点(与 _maxRootGrid 平级),不需要 SetRowSpan(3),
        // 因为 _root 现在是 1 行 Grid,_maxRootGrid / _dsRootGrid 互斥显示。
    }

    /// <summary>
    /// 构建 DeepSeek 单列:[1px 左竖线 | StackPanel],showDivider 控制左竖线可见性。
    /// </summary>
    private void BuildDeepSeekColumn(
        out Grid column,
        out Border divider,
        out TextBlock label,
        out TextBlock value,
        out TextBlock foot,
        string primaryInitial,
        string subText,
        string footInitial,
        bool showDivider)
    {
        column = new Grid();
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Pixel) });
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        divider = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = showDivider ? Visibility.Visible : Visibility.Collapsed
        };
        Grid.SetColumn(divider, 0);
        column.Children.Add(divider);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(14, 0, 14, 0)
        };

        label = new TextBlock
        {
            Text = subText,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        value = new TextBlock
        {
            Text = primaryInitial,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        foot = new TextBlock
        {
            Text = footInitial,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        stack.Children.Add(label);
        stack.Children.Add(value);
        stack.Children.Add(foot);

        Grid.SetColumn(stack, 1);
        column.Children.Add(stack);
    }

    /// <summary>
    /// 构建 DeepSeek 第三列:StackPanel 内容依次为 Label / Value(Number + Suffix) / Foot1 / Foot2 / Foot3。
    /// </summary>
    private void BuildDeepSeekColumnWithTokens(
        out Grid column,
        out Border divider,
        out TextBlock label,
        out TextBlock valueNumber,
        out TextBlock valueSuffix,
        out TextBlock foot1,
        out TextBlock foot2,
        out TextBlock foot3)
    {
        // 列结构与 BuildDeepSeekColumn 对齐:[1px 左侧分割线 | 内容 stack],
        // 让"今日消耗"列与"近 7 日"列之间有视觉分隔(三列卡片统一节奏)。
        column = new Grid();
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Pixel) });
        column.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        divider = new Border
        {
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 4, 0, 4),
            Visibility = Visibility.Visible
        };
        Grid.SetColumn(divider, 0);
        column.Children.Add(divider);

        // 左右 margin 由 10 改 4:列3 内容 5 行(数字+Tokens+三脚注)横向更紧,
        // 释放 ~12px 让 "25,325 Tokens" 完整显示而不再被 TextTrimming 截断。
        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(4, 0, 4, 0)
        };

        label = new TextBlock
        {
            Text = "今日消耗",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        valueNumber = new TextBlock
        {
            Text = "—",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.None
        };
        valueSuffix = new TextBlock
        {
            Text = " Tokens",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            // Margin left 由 4 改 2,节省数字与 Tokens 间距,给列3 横向多挤出 2px。
            Margin = new Thickness(2, 0, 0, 4),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // valueRow 改为 Grid 两列:[数字 Viewbox(可缩放) | Tokens(suffix)]。
        // Viewbox 让 9 位数(如 "123,456,789")在窄列内自动缩小字号,
        // 同时保证 " Tokens" 后缀永远完整不被 TextTrimming 截断。
        var valueNumberViewbox = new Viewbox
        {
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        valueNumberViewbox.Child = valueNumber;

        var valueRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center
        };
        valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(valueNumberViewbox, 0);
        Grid.SetColumn(valueSuffix, 1);
        valueRow.Children.Add(valueNumberViewbox);
        valueRow.Children.Add(valueSuffix);

        foot1 = new TextBlock
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        foot2 = new TextBlock
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };
        foot3 = new TextBlock
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 1, 0, 0)
        };

        stack.Children.Add(label);
        stack.Children.Add(valueRow);
        stack.Children.Add(foot1);
        stack.Children.Add(foot2);
        stack.Children.Add(foot3);

        Grid.SetColumn(stack, 1);
        column.Children.Add(stack);
    }
}