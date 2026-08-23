using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// DeepSeek / OpenCode 胶囊：外圆环 + 内圆点（"◉" 形式）+ 文本。
///
/// 视觉规则：
/// - 圆点默认按风险档位（绿/黄/橙/红）静态显示。
/// - 高峰期（UTC+8 9-12 / 14-18）圆点强制变橙色并呼吸（Opacity 1.0 → 0.35，1.6s 周期）。
/// - 非高峰期圆点不呼吸（Opacity 固定 1.0）。
///
/// 设计原则：与 BalanceRingCapsuleView 完全独立 —— 不继承、不共享指示器字段、
/// 不共享动画常量。圆环由本类自己实例化持有，呼吸动画对象也独立维护，
/// 改动本胶囊的圆点 / 呼吸规则不会牵连 MiniMax 胶囊，反之亦然。
///
/// 性能契约：
/// - 呼吸动画只动 Ellipse.Opacity，不解冻 Ellipse.Fill（Brush 仍冻结）。
/// - ToBrush 返回冻结 Brush，跨线程共享。
/// </summary>
internal sealed class BalanceDotCapsuleView : Grid
{
    private readonly TextBlock _label;
    private readonly BalanceProgressRing _ring;
    // 内圆点：6×6 DIP，居中叠在圆环上。
    private readonly Ellipse _dot;

    // 高峰期橙色常量：#FF9800 与 MiniMax 5h 进度条同源色。
    private const string PeakDotColorHex = "#FF9800";

    // 呼吸动画只读一份，所有 BalanceDotCapsuleView 实例共享（Freeze 后线程安全）。
    private static readonly DoubleAnimation DotBreathAnimation = CreateBreathAnimation();

    // FontFamily 缓存：theme.FontFamily 是字符串，按字符串相等判断避免重复构造。
    private FontFamily? _cachedFontFamily;
    private string? _cachedFontFamilySource;

    public BalanceDotCapsuleView(PaperCapsuleViewContext context)
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        IsHitTestVisible = false;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 5 列布局：[6 pad][18 ring+dot][5 gap][* text][4 right pad]
        // 圆环与圆点共用第 1 列（18 DIP 宽），居中叠加。
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

        _dot = new Ellipse
        {
            // 6×6 DIP：缩到圆环直径（18）的 1/3，让位给外环轮廓，避免视觉上压过 track。
            Width = 6,
            Height = 6,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = Visibility.Visible
        };
        Grid.SetColumn(_dot, 1);
        Children.Add(_dot);

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

    private static DoubleAnimation CreateBreathAnimation()
    {
        var anim = new DoubleAnimation
        {
            From = 1.0,
            To = 0.35,
            Duration = new Duration(TimeSpan.FromSeconds(0.8)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Freeze();
        return anim;
    }

    /// <summary>
    /// 刷新胶囊文本 / 圆环 / 圆点。
    /// - ringColorHex / ringArc：本胶囊不消费颜色（外环保持 track 底色，仅作轮廓），
    ///                      但参数仍保留以保持调用方签名统一。
    /// - dotColorHex：圆点默认颜色（按风险档位：绿/黄/橙/红）。
    /// - isPeakHour=true：圆点变橙色并呼吸；false：按 dotColorHex 静态显示。
    /// </summary>
    public void Update(
        string text,
        string ringColorHex,
        double ringArc,
        string dotColorHex,
        bool isPeakHour)
    {
        _label.Text = text;

        // 圆环：仅作轮廓底色，不显示风险颜色弧。ForegroundBrush 用 Brushes.Transparent，
        // 让 OnRender 只画 track（TrackBrush 由 ApplyTheme 注入），弧段自然透明。
        // Arc 值仍可保留（避免 0/1 抖动），但视觉上不出现。
        _ring.Value = Math.Clamp(ringArc, 0, 1);
        _ring.ForegroundBrush = Brushes.Transparent;
        _ring.InvalidateVisual();

        // 圆点：高峰期强制橙色 + 呼吸；其他时段按 dotColorHex 静态。
        var dotColor = isPeakHour ? PeakDotColorHex : dotColorHex;
        _dot.Fill = ToBrush(dotColor, "#9E9E9E");
        if (isPeakHour)
        {
            _dot.BeginAnimation(UIElement.OpacityProperty, DotBreathAnimation);
        }
        else
        {
            // 清掉动画引用，Opacity 回到默认 1.0（静态）。
            _dot.BeginAnimation(UIElement.OpacityProperty, null);
            _dot.Opacity = 1.0;
        }
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

        // 外圆环底色：浅白色（alpha ≈ 70/255 ≈ 27%），弱视觉锚点。深色主题下清晰可见，
        // 浅色主题下与背景融为一体只剩微弱轮廓，符合"外环不需要颜色、仅作衬托"的设计意图。
        var track = new SolidColorBrush(Color.FromArgb(70, 0xFF, 0xFF, 0xFF));
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