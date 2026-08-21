using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// 1.7 自定义胶囊视图(由 IPaperCapsuleViewProvider 返回)。
/// 5 列布局 [6 pad][18 ring][5 gap][* text][4 right pad];嵌套 BalanceProgressRing 自绘圆环。
/// 完全 1:1 复刻宿主 1.6 模板视觉布局;BalanceProgressRing 完全 1:1 复刻宿主 CapsuleProgressRing。
///
/// 性能契约(避免首屏 5 秒卡顿):
/// - Pen / StreamGeometry 必须缓存并 Freeze,逐字段搬运不重写;
/// - ToBrush 已返回冻结 Brush,跨线程共享。
/// </summary>
internal sealed class BalanceCapsuleView : Grid
{
    private readonly TextBlock _label;
    private readonly BalanceProgressRing _ring;

    // FontFamily 缓存:theme.FontFamily 是 Source 字符串,按字符串相等判断避免重复构造。
    // 避免每次 ApplyTheme 都触发 WPF 字体回退链解析(首次解析可达 100ms 级)。
    private FontFamily? _cachedFontFamily;
    private string? _cachedFontFamilySource;

    public BalanceCapsuleView(PaperCapsuleViewContext context)
    {
        Background = Brushes.Transparent;
        ClipToBounds = true;
        // 宿主还会强制重置 IsHitTestVisible / Focusable / Stretch 对齐,本地保险。
        IsHitTestVisible = false;
        Focusable = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 列布局:[6 pad][18 ring][5 gap][* text][4 right pad]
        // 移除 sun 列后右 padding 缩为 4 DIP,差额 6 的原口径不再适用。
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
    /// 刷新胶囊文本与圆环颜色 / 弧值。
    /// </summary>
    public void Update(
        string text,
        string ringColorHex,
        double ringArc)
    {
        _label.Text = text;
        _ring.Value = Math.Clamp(ringArc, 0, 1);
        _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
        _ring.InvalidateVisual();
    }

    /// <summary>
    /// 主题切换:重设文本字体 / 字号 / 颜色,圆环底色(近似 Theme.Tint(38))。
    /// 字号取 12 × FontScale,与宿主默认 CapsuleTextSize=Medium + AppTypography.Scale 一致。
    /// </summary>
    public void ApplyTheme(PaperBodyTheme theme)
    {
        var scale = Math.Clamp(theme.FontScale, 0.85, 1.2);
        // FontFamily 缓存:theme.FontFamily 是 Source 字符串,按字符串相等判断避免重复构造。
        if (_cachedFontFamilySource != theme.FontFamily || _cachedFontFamily == null)
        {
            _cachedFontFamily = new FontFamily(theme.FontFamily);
            _cachedFontFamilySource = theme.FontFamily;
        }
        _label.FontFamily = _cachedFontFamily;
        _label.FontSize = 12.0 * scale;
        _label.FontWeight = FontWeights.Normal;
        // BrightWeakTextBrush 在浅色下等于 WeakTextBrush,深色下浅化 22%。
        // 这里取 WeakTextColor 作为单一字段近似(浅色完全一致,深色略偏暗,但 1.6 模板
        // 对未设 Color 的 Text 组件也走这条 Tone 兜底,视觉同源)。
        // ToBrush 已返回冻结 Brush,可直接复用,无需每次重建。
        _label.Foreground = ToBrush(theme.WeakTextColor, "#707070");

        // TrackBrush 近似 Theme.Tint(38):在当前 AccentColor 上叠加 alpha=38。
        // PaperBodyTheme 不暴露 Theme.Tint,使用最近的色板字段 AccentColor 做近似。
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
        // 冻结 brush:让 WPF 渲染系统走快路径(避免每帧 IsFrozen 检查),
        // 并允许跨线程共享(GPU worker 线程可直接读取颜色与变换)。
        brush.Freeze();
        return brush;
    }
}

/// <summary>
/// 圆环进度控件。完全 1:1 复刻宿主 CapsuleProgressRing(PaperWindow.PluginCapsule.cs):
/// Pen 粗细 2,半径 = max(1, size/2 - 1.5),起点 -90° 顺时针,value≥0.999 画整圆。
///
/// 性能优化(避免 5 秒延迟):
/// - Pen 缓存:仅在 TrackBrush / ForegroundBrush 引用变化时重建并 Freeze。
/// - StreamGeometry 缓存:仅在 value 变化时重建弧形几何,Freeze 后可跨帧复用。
/// </summary>
internal sealed class BalanceProgressRing : FrameworkElement
{
    public double Value { get; set; }
    public Brush ForegroundBrush { get; set; } = Brushes.Gray;
    public Brush TrackBrush { get; set; } = Brushes.LightGray;

    // Pen 缓存:仅在 brush 引用变化时重建(重建后 Freeze 启用渲染快路径)
    private Pen? _cachedTrackPen;
    private Pen? _cachedValuePen;
    private Brush? _cachedTrackBrushRef;
    private Brush? _cachedFgBrushRef;

    // Geometry 缓存:仅在 value 变化时重建(Freeze 后线程安全)
    private double _cachedGeometryValue = double.NaN;
    private StreamGeometry? _cachedGeometry;

    private Pen GetTrackPen()
    {
        if (_cachedTrackPen == null || !ReferenceEquals(_cachedTrackBrushRef, TrackBrush))
        {
            _cachedTrackPen = new Pen(TrackBrush, 2);
            _cachedTrackPen.Freeze();
            _cachedTrackBrushRef = TrackBrush;
        }
        return _cachedTrackPen;
    }

    private Pen GetValuePen()
    {
        if (_cachedValuePen == null || !ReferenceEquals(_cachedFgBrushRef, ForegroundBrush))
        {
            _cachedValuePen = new Pen(ForegroundBrush, 2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            _cachedValuePen.Freeze();
            _cachedFgBrushRef = ForegroundBrush;
        }
        return _cachedValuePen;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 2)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(1, size / 2 - 1.5);
        dc.DrawEllipse(null, GetTrackPen(), center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        if (value <= 0)
        {
            return;
        }
        if (value >= 0.999)
        {
            dc.DrawEllipse(null, GetValuePen(), center, radius, radius);
            return;
        }

        // 弧形 Geometry 缓存:value 不变时复用上一次构建的 StreamGeometry,
        // 避免每次 OnRender 都走 Open/ArcTo/Freeze 路径。
        if (_cachedGeometry == null || _cachedGeometryValue != value)
        {
            var geo = new StreamGeometry();
            var startAngle = -90.0;
            var endAngle = startAngle + value * 360.0;
            using (var ctx = geo.Open())
            {
                Point PointAt(double angle)
                {
                    var radians = angle * Math.PI / 180.0;
                    return new Point(
                        center.X + Math.Cos(radians) * radius,
                        center.Y + Math.Sin(radians) * radius);
                }
                ctx.BeginFigure(PointAt(startAngle), false, false);
                ctx.ArcTo(
                    PointAt(endAngle),
                    new Size(radius, radius),
                    0,
                    value > 0.5,
                    SweepDirection.Clockwise,
                    true,
                    false);
            }
            geo.Freeze();
            _cachedGeometry = geo;
            _cachedGeometryValue = value;
        }

        dc.DrawGeometry(null, GetValuePen(), _cachedGeometry);
    }
}