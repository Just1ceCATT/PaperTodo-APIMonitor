using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// MiniMax 胶囊：纯圆环进度指示 + 文本。不含内圆点 / 呼吸动画。
///
/// 设计原则：与 BalanceDotCapsuleView 完全独立 —— 不继承、不共享指示器字段、
/// 不共享动画常量。改动 MiniMax 圆环不会牵连 DeepSeek 胶囊，反之亦然。
///
/// 性能契约:
/// - Pen / StreamGeometry 由 BalanceProgressRing 内部缓存并 Freeze。
/// - 所有 Brush 由 ToBrush 返回冻结实例，跨线程共享。
/// </summary>
internal sealed class BalanceRingCapsuleView : Grid
{
    private readonly TextBlock _label;
    private readonly BalanceProgressRing _ring;
    // 圆环列可见性缓存:防止外部反复调用 SetRingVisible 时重写 Column 宽度。
    private bool _ringColumnVisible = true;

    // FontFamily 缓存：theme.FontFamily 是字符串，按字符串相等判断避免重复构造。
    // 避免每次 ApplyTheme 都触发 WPF 字体回退链解析（首次解析可达 100ms 级）。
    private FontFamily? _cachedFontFamily;
    private string? _cachedFontFamilySource;

    public BalanceRingCapsuleView(PaperCapsuleViewContext context)
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 5 列布局：[6 pad][18 ring][5 gap][* text][4 right pad]
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });

        _ring = new BalanceProgressRing
        {
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(_ring, 1);
        Children.Add(_ring);

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Left
        };
        Grid.SetColumn(_label, 3);
        Children.Add(_label);

        ApplyTheme(context.Theme);
    }

    /// <summary>
    /// MiniMax 胶囊刷新：文本 + 圆环弧值 / 颜色（圆环颜色由风险档位 / 剩余百分比决定）。
    /// </summary>
    public void Update(string text, string ringColorHex, double ringArc)
    {
        _label.Text = text;
        _ring.Value = Math.Clamp(ringArc, 0, 1);
        _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
        _ring.InvalidateVisual();
    }

    /// <summary>
    /// 切换圆环可见性（设置 disableRing 时调用）。关闭后圆环列折叠为 0 宽,
    /// 文字列左移填满;切回时恢复原宽度,胶囊宽度不会抖动（Columns 已声明）。
    /// </summary>
    public void SetRingVisible(bool visible)
    {
        _ringColumnVisible = visible;
        // Column 1 是圆环列（[6 pad][18 ring][5 gap][* text][4 right pad]）。
        // 关闭时宽度归 0,gap 列也归 0,文字列自动扩到剩余空间。
        ColumnDefinitions[1].Width = visible
            ? new GridLength(18)
            : new GridLength(0);
        ColumnDefinitions[2].Width = visible
            ? new GridLength(5)
            : new GridLength(0);
        _ring.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 主题切换：文本字体 / 字号 / 颜色，圆环底色（近似 Theme.Tint(38)）。
    /// 字号取 12 × FontScale，与宿主默认 CapsuleTextSize=Medium + AppTypography.Scale 一致。
    /// </summary>
    public void ApplyTheme(PaperBodyTheme theme)
    {
        var scale = Math.Clamp(theme.FontScale, 0.85, 1.2);
        if (_cachedFontFamilySource != theme.FontFamily || _cachedFontFamily == null)
        {
            _cachedFontFamily = new FontFamily(theme.FontFamily);
            _cachedFontFamilySource = theme.FontFamily;
        }
        _label.FontFamily = _cachedFontFamily;
        _label.FontSize = 12.0 * scale;
        _label.FontWeight = FontWeights.Normal;
        _label.Foreground = ToBrush(theme.WeakTextColor, "#707070");

        var accent = ToBrush(theme.AccentColor, "#B07A31");
        var track = new SolidColorBrush(
            Color.FromArgb(38, accent.Color.R, accent.Color.G, accent.Color.B));
        track.Freeze();
        _ring.TrackBrush = track;
        _ring.InvalidateVisual();
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
        brush.Freeze();
        return brush;
    }
}