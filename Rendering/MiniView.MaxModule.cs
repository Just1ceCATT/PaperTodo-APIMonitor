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
        _maxRootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4, GridUnitType.Pixel) });
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
        var hourlyHead = new Grid { Margin = new Thickness(0, 0, 0, 4) };
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

        // 5h 倒计时行:左时钟图标 + 右倒计时文字,Grid 横向布局。
        // margin 统一 0,0,0,0:与上方进度条的 4 DIP 间距由 _hourlyBarGrid 的 bottom margin 提供,
        // 三段(head→bar→reset)间距全部用"上一段 bottom=4"显式声明,避免 StackPanel 默认紧贴 + 各元素自带 margin 的不对称写法。
        _hourlyClockGlyph = BuildClockGlyph();
        _hourlyResetRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 0, 0)
        };
        _hourlyResetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _hourlyResetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_hourlyClockGlyph, 0);
        Grid.SetColumn(_hourlyReset, 1);
        _hourlyResetRow.Children.Add(_hourlyClockGlyph);
        _hourlyResetRow.Children.Add(_hourlyReset);

        var hourlyStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false
        };
        hourlyStack.Children.Add(hourlyHead);
        hourlyStack.Children.Add(_hourlyBarGrid);
        hourlyStack.Children.Add(_hourlyResetRow);
        Grid.SetRow(hourlyStack, 0);
        _hourlyStack = hourlyStack;
        _maxRootGrid.Children.Add(hourlyStack);

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
        var weeklyHead = new Grid { Margin = new Thickness(0, 0, 0, 4) };
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

        // 周倒计时行:左时钟图标 + 右倒计时文字,Grid 横向布局。margin 同上统一为 0。
        _weeklyClockGlyph = BuildClockGlyph();
        _weeklyResetRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 0, 0)
        };
        _weeklyResetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _weeklyResetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(_weeklyClockGlyph, 0);
        Grid.SetColumn(_weeklyReset, 1);
        _weeklyResetRow.Children.Add(_weeklyClockGlyph);
        _weeklyResetRow.Children.Add(_weeklyReset);

        // 底部 footer:更新于 HH:mm:ss,弱文字小字号,右对齐嵌在周模块底部。
        _footer = new TextBlock
        {
            Text = "尚未拉取",
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // footer 行:左刷新图标 + 右时间戳文字,Grid 横向布局,作为整体放到 _maxRootGrid Row 2 右下角。
        _refreshGlyph = BuildRefreshGlyph();
        _footerRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
            Margin = new Thickness(0, 0, 0, 2)
        };
        _footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_refreshGlyph, 0);
        Grid.SetColumn(_footer, 1);
        _footerRow.Children.Add(_refreshGlyph);
        _footerRow.Children.Add(_footer);

        // weeklyStack 不再含 _footer(避免自然高度超 row 高度导致上下溢出,
        // divider 1px 被内容覆盖)。_footerRow 改放到 _maxRootGrid Row 2 内右下角独立显示。
        // Margin(0,-5,0,5) 把"周额度 / 百分比 / 周额度进度条 / xx天后重置"几个组件作为一组
        // 整体上移 5px,底部留 5px 避免与 _footerRow 重叠。
        var weeklyStack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsHitTestVisible = false,
            Margin = new Thickness(0, -5, 0, 5)
        };
        weeklyStack.Children.Add(weeklyHead);
        weeklyStack.Children.Add(_weeklyBarGrid);
        weeklyStack.Children.Add(_weeklyResetRow);
        Grid.SetRow(weeklyStack, 2);
        _weeklyStack = weeklyStack;
        _maxRootGrid.Children.Add(weeklyStack);

        // _footerRow 在 _maxRootGrid Row 2 内右下角:不撑高 weeklyStack 自然高度。
        Grid.SetRow(_footerRow, 2);
        Grid.SetColumn(_footerRow, 0);
        _maxRootGrid.Children.Add(_footerRow);
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

    /// <summary>圆角进度条:track 铺底色,fill 按 ratio 收窄;高 6 DIP、半径 3(半圆端匹配高度)。</summary>
    private static (Grid grid, Rectangle track, Rectangle fill) BuildStyledProgressBar()
    {
        var grid = new Grid
        {
            Height = 6,
            // 仅保留 bottom margin(4 DIP),与 hourlyHead 的 bottom margin 配合,
            // 让"标签行→进度条→倒计时行"三段间距显式声明;右侧不再预留 24px。
            Margin = new Thickness(0, 0, 0, 4)
        };
        var track = new Rectangle
        {
            RadiusX = 3,
            RadiusY = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var fill = new Rectangle
        {
            RadiusX = 3,
            RadiusY = 3,
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
    /// 5h(includeDays=false):"x 时 y 分 后重置",小时为 0 时简化为 "x 分 后重置"。
    /// 周(includeDays=true):"x 天 x 时 x 分 后重置",三段都显示不省略。
    /// NaN / 0 / 负数视为未拉取。
    /// </summary>
    private static string FormatRemaining(double hours, bool includeDays)
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
                ? m + "分钟后重置"
                : totalHours + "小时" + m + "分钟后重置";
        }
        var days = totalHours / 24;
        var h = totalHours % 24;
        return days + "天" + h + "小时" + m + "分钟后重置";
    }

    /// <summary>
    /// 时钟图标:完整圆 + 12 点短针 + 3 点长针,viewBox 14x14,Stroke 由 ApplyTheme 注入。
    /// 用 SVG path 标记语法(M/L/A)经 Geometry.Parse 解析:两段 ArcTo 拼成完整圆
    /// (单段 ArcTo 在两端为直径关系时只能画半圆)。
    /// </summary>
    private static Path BuildClockGlyph()
    {
        // 圆心 (7, 7)、半径 6:(1, 7) ↔ (13, 7) 为直径两端,两段 180° 弧拼成完整圆。
        // 时针 (7, 7)→(7, 4):朝 12 点方向,长 3。分针 (7, 7)→(10, 7):朝 3 点方向,长 3。
        var data = Geometry.Parse("M 1,7 A 6,6 0 0 1 13,7 A 6,6 0 0 1 1,7 M 7,7 L 7,4 M 7,7 L 10,7");
        if (data.CanFreeze)
        {
            data.Freeze();
        }
        return new Path
        {
            Data = data,
            Width = 14,
            Height = 14,
            Margin = new Thickness(0, 0, 6, 0),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
    }

    /// <summary>
/// 刷新图标:经典"双弧双箭头"刷新形态(viewBox 32×32),4 段组合:
///   1) 上方弧线:`M 3.51,9 a 9,9 0 0 1 14.85,-3.36 L 21,9`  → 从左侧底部逆时针弧到右上方,再直线回到右边中点
///   2) 上方箭头:`M 21,3 v6 h-6`  → 右上角"┐"形(朝下的钩子),与上弧线终点 (21, 9) 相连
///   3) 下方弧线:`M 20.49,15 a 9,9 0 0 1 -14.85,3.36 L 3,15` → 从右侧顶部逆时针弧到左下方,再直线回到左边中点
///   4) 下方箭头:`M 3,21 v-6 h6`  → 左下角"└"形(朝上的钩子),与下弧线终点 (3, 15) 相连
/// 两个半圆对置 + 两个箭头反向 → 视觉上明显的"循环/旋转"语义,这就是经典的 refresh 按钮样式。
///
/// **Bounds = (3, 3, 21, 21) = 18×18 正方形**(viewBox 32×32 内)。
/// Path Width=14 + Height=14 + Stretch=Uniform → 完美 1:1 缩放,圆弧不变形,与时钟图标行为一致。
///
/// 端点圆角:SVG 用 stroke-linecap="round" + stroke-linejoin="round",WPF 等价
/// StrokeStartLineCap / StrokeEndLineCap = PenLineCap.Round + StrokeLineJoin = PenLineJoin.Round,
/// 让端点和拐角圆滑,视觉更精致。
///
/// Margin right=6:与时钟图标 _weeklyClockGlyph 一致,Grid Column 宽度 = 14 + 6 = 20,
/// 保持图标右边与"更新于 xx:xx"文字之间 6 DIP 间距。
/// </summary>
private static Path BuildRefreshGlyph()
{
    var data = Geometry.Parse(
        "M 21,3 v6 h-6 " +                                          // 上箭头 (右上 ┐ 朝下)
        "M 3,21 v-6 h6 " +                                          // 下箭头 (左下 └ 朝上)
        "M 3.51,9 a 9,9 0 0 1 14.85,-3.36 L 21,9 " +               // 上弧线:左下弧到右上 + 直线到右中
        "M 20.49,15 a 9,9 0 0 1 -14.85,3.36 L 3,15");              // 下弧线:右上弧到左下 + 直线到左中
    if (data.CanFreeze)
    {
        data.Freeze();
    }
    return new Path
    {
        Data = data,
        // 整体比时钟图标小一档:时钟 14×14,刷新 12×12(约 -14%),
        // 不抢"更新于 xx:xx"文字视觉权重。Stretch=Uniform 让 Geometry 18×18
        // 按比例缩到 12×12,保持 1:1 比例不拉伸。
        Width = 12,
        Height = 12,
        Margin = new Thickness(0, 0, 6, 0),
        Stretch = Stretch.Uniform,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };
}
}