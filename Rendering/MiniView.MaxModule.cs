using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// BalanceMiniView 的 MiniMax 双模块 partial:5h + 周额度两根进度条 + 倒计时 + 底部 footer。
/// 字段定义在 MiniView.cs,本文件负责 BuildMiniMaxModule / BuildContainerBackground /
/// BuildHeadRow / BuildStyledProgressBar / OnHourlyBarSizeChanged / OnWeeklyBarSizeChanged。
/// </summary>
internal sealed partial class BalanceMiniView
{
    /// <summary>构建 MiniMax 双模块子树:_maxRootGrid(3 行:5h / 1px 分割线 / 周)。</summary>
    private void BuildMiniMaxModule()
    {
        // MiniMax 双模块内部 3 行 Grid:5h / 1px 分割线 / 周。
        // row 1 高度 4px、分割线高度 1px 贴 row 1 底部,让 5h 模块底部与分割线之间多 3px 留白,
        // 周模块紧贴分割线下方,整体视觉空间更宽松。
        _maxRootGrid = new Grid();
        _maxRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _maxRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10, GridUnitType.Pixel) });
        _maxRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _maxRootGrid.IsHitTestVisible = false;

        // === 5 小时模块(占据上半部 50%) ===
        _hourlyLabel = new TextBlock
        {
            Text = "每五小时额度",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _hourlyPercent = new TextBlock
        {
            Text = "—",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var hourlyHead = new Grid();
        hourlyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hourlyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_hourlyLabel, 0);
        Grid.SetColumn(_hourlyPercent, 1);
        hourlyHead.Children.Add(_hourlyLabel);
        hourlyHead.Children.Add(_hourlyPercent);

        (_hourlyBarGrid, _hourlyBarTrack, _hourlyFill) = BuildStyledProgressBar();
        _hourlyBarGrid.SizeChanged += OnHourlyBarSizeChanged;

        _hourlyReset = new TextBlock
        {
            Text = "尚未拉取",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var hourlyStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };
        hourlyStack.Children.Add(hourlyHead);
        hourlyStack.Children.Add(_hourlyBarGrid);
        hourlyStack.Children.Add(_hourlyReset);
        Grid.SetRow(hourlyStack, 0);
        _hourlyStack = hourlyStack;
        _maxRootGrid.Children.Add(_hourlyStack);

        // 5h / 周模块之间的 1px 分割线,弱色填充。
        // VerticalAlignment=Bottom 让它贴在 row 1 底部(row 1 高 2px),上方留 1px 空白归属 5h 模块底部,
        // 视觉上分割线相对 5h 模块下移 1px,周模块与分割线之间无额外间隙(保持紧凑)。
        _divider = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom
        };
        Grid.SetRow(_divider, 1);
        _maxRootGrid.Children.Add(_divider);

        // === 周额度模块(占据下半部 50%) ===
        _weeklyLabel = new TextBlock
        {
            Text = "周额度",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _weeklyPercent = new TextBlock
        {
            Text = "—",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        var weeklyHead = new Grid();
        weeklyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        weeklyHead.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_weeklyLabel, 0);
        Grid.SetColumn(_weeklyPercent, 1);
        weeklyHead.Children.Add(_weeklyLabel);
        weeklyHead.Children.Add(_weeklyPercent);

        (_weeklyBarGrid, _weeklyBarTrack, _weeklyFill) = BuildStyledProgressBar();
        _weeklyBarGrid.SizeChanged += OnWeeklyBarSizeChanged;

        _weeklyReset = new TextBlock
        {
            Text = "尚未拉取",
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // 底部 footer:更新于 HH:mm:ss,弱文字小字号,右对齐嵌在周模块底部。
        _footer = new TextBlock
        {
            Text = "尚未拉取",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 4, 0, 0)
        };

        // weeklyStack 不再含 _footer(避免自然高度超 row 高度导致上下溢出,
        // divider 1px 被内容覆盖)。_footer 改放到 _maxRootGrid Row 2 内右下角独立显示。
        var weeklyStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };
        weeklyStack.Children.Add(weeklyHead);
        weeklyStack.Children.Add(_weeklyBarGrid);
        weeklyStack.Children.Add(_weeklyReset);
        Grid.SetRow(weeklyStack, 2);
        _weeklyStack = weeklyStack;
        _maxRootGrid.Children.Add(weeklyStack);

        // _footer 在 _maxRootGrid Row 2 内右下角:StackPanel 居左,_footer 靠右下,
        // 不撑高 weeklyStack 自然高度。
        Grid.SetRow(_footer, 2);
        Grid.SetColumn(_footer, 0);
        _footer.HorizontalAlignment = HorizontalAlignment.Right;
        _footer.VerticalAlignment = VerticalAlignment.Bottom;
        _footer.Margin = new Thickness(0, 0, 0, 2);
        _maxRootGrid.Children.Add(_footer);
    }

    /// <summary>
    /// 容器背景:暗色 12% 黑 / 浅色 6% 黑,冻结后跨线程共享。
    /// </summary>
    private static SolidColorBrush BuildContainerBackground(bool isDark)
    {
        var brush = isDark
            ? new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x00, 0x00))
            : new SolidColorBrush(Color.FromArgb(0x10, 0x00, 0x00, 0x00));
        brush.Freeze();
        return brush;
    }

    /// <summary>圆角进度条:track 铺底色,fill 按 ratio 收窄;高 8 DIP、半径 4。</summary>
    private static (Grid grid, Rectangle track, Rectangle fill) BuildStyledProgressBar()
    {
        var grid = new Grid
        {
            Height = 8,
            // top margin 由 6 改 4:减小头部与进度条垂直间距,缓解 5h/周两个模块在
            // scale=1.3 时 row 自然高度溢出造成的内容重叠。
            Margin = new Thickness(0, 4, 24, 0)
        };
        var track = new Rectangle
        {
            RadiusX = 4,
            RadiusY = 4,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var fill = new Rectangle
        {
            RadiusX = 4,
            RadiusY = 4,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
        grid.Children.Add(track);
        grid.Children.Add(fill);
        return (grid, track, fill);
    }

    /// <summary>进度条容器尺寸变化时按当前 ratio 重算 fill.Width。</summary>
    private void OnHourlyBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _hourlyFill.Width = Math.Max(0, _hourlyBarGrid.ActualWidth * _lastHourlyRatio);
    }

    private void OnWeeklyBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _weeklyFill.Width = Math.Max(0, _weeklyBarGrid.ActualWidth * _lastWeeklyRatio);
    }

    private static string FormatPercent(double percent)
    {
        if (double.IsNaN(percent))
        {
            return "—";
        }
        var rounded = (int)Math.Round(percent, MidpointRounding.AwayFromZero);
        return rounded.ToString(CultureInfo.CurrentCulture) + "%";
    }

    /// <summary>
    /// 把小时数格式化为剩余时长。
    /// 5h(includeDays=false):"x 时 y 分",小时为 0 时简化为 "x 分"。
    /// 周(includeDays=true):"x 天 x 时 x 分",三段都显示不省略。
    /// NaN / 0 / 负数视为未拉取。
    /// </summary>
    private static string FormatRemaining(string prefix, double hours, bool includeDays)
    {
        if (double.IsNaN(hours) || hours <= 0)
        {
            return "尚未拉取";
        }
        var totalHours = (int)Math.Floor(hours);
        var m = (int)Math.Round((hours - totalHours) * 60);
        if (m == 60)
        {
            m = 0;
            totalHours += 1;
        }
        if (!includeDays)
        {
            return totalHours == 0
                ? prefix + m + "分"
                : prefix + totalHours + "时" + m + "分";
        }
        var days = totalHours / 24;
        var h = totalHours % 24;
        return prefix + days + "天" + h + "时" + m + "分";
    }
}