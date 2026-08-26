using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// 圆环进度控件。完全 1:1 复刻宿主 CapsuleProgressRing(PaperWindow.PluginCapsule.cs):
/// Pen 粗细 2,半径 = max(1, size/2 - 1.5),起点 -90° 顺时针,value≥0.999 画整圆。
///
/// 性能优化(避免 5 秒延迟):
/// - Pen 缓存:仅在 TrackBrush / ForegroundBrush 引用变化时重建并 Freeze。
/// - StreamGeometry 缓存:按角度桶(0.02)离散化,动画期间 Value 逐帧变化时复用同桶的
///   frozen StreamGeometry，避免每帧重建 Geometry / Point / closure / Freeze 的分配。
///
/// 完成动画:BeginCheckmarkAnimation() 触发"圆环闭合 → 短暂停留 → 对勾描边绘制"
/// 连续动画,总时长 ~400ms。对勾用 Path.StrokeDashOffset 动画实现。
/// 几何完全在圆环内(圆环占 12.5~87.5 范围,安全区 30~70)。
///
/// 注意:此控件是底层图元，被 BalanceRingCapsuleView(MiniMax) 和
/// BalanceDotCapsuleView(DeepSeek) 各自持有自己的实例，不在两个胶囊之间共享。
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

    // Geometry 缓存:按角度桶离散化缓存(动画期间 Value 逐帧变化时复用同一桶的 frozen
    // StreamGeometry，避免每帧重建 Geometry / Point / closure / Freeze 的分配风暴)。
    // 桶距 0.02 → 51 个桶,最差视觉误差 ~2°,人眼对圆环末端不敏感。
    private const double GeometryBucketStep = 0.02;
    private int _cachedGeometryBucket = int.MinValue;
    private double _cachedGeometryValue = double.NaN;
    private StreamGeometry? _cachedGeometry;

    // 对勾动画:Path 元素作为视觉子节点,StrokeDashOffset 动画实现描边绘制。
    // Geometry 坐标基于 100x100 单元,通过 Stretch=Fill 等比缩放到控件实际尺寸。
    // 几何路径完全包裹在圆环内（外接圆半径 ≈ 65 in 100 单位,最大点距圆心 ≤ 33 单位）,
    // 视觉宽度约占圆环内径的 40%,保证勾主体清晰、被圆环"包裹"而非贴近边缘。
    //   左下 (35,50) → 拐点 (46,60) → 右上 (68,38)
    //   短边 ~14.9 + 长边 ~31.1 = 总长 ~46.0(按 100x100 单位)
    private Path? _checkmarkPath;
    private double _checkmarkTotalLength = 34.9;

    // 默认对勾描边色:提取为静态冻结实例,允许多线程共享并走 WPF 渲染快路径。
    // 默认绿色与 StopImage 同色系,在绿色圆环上显示同色描边。
    private static readonly SolidColorBrush DefaultCheckmarkStroke =
        CreateFrozenStroke(Color.FromRgb(0x4C, 0xAF, 0x50));

    private static SolidColorBrush CreateFrozenStroke(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public BalanceProgressRing()
    {
        // 几何路径:用像素绝对坐标(直接对 18×18 圆环设计)，不用 Stretch=Fill,
        // 避免非正方形 bbox 在拉伸时把拐点推到 18×18 边缘外、与圆环 stroke 视觉重叠。
        // 顶点全部落在距圆心 ≤ 6.5 px 的内圈安全区(圆环内边 = 中心 7.5 - stroke/2 = 6.5)。
        //   左端 (4, 9) → 拐点 (8, 12) → 右上 (13, 5)
        //   距离圆心:5.0 / 3.32 / 5.66 px,均 ≤ 6.5,被圆环完整包裹且不贴内边。
        // 像素路径总长:5.0 + 8.6 ≈ 13.6 px（用于设置 dasharray / dashoffset）.
        _checkmarkPath = new Path
        {
            Stroke = DefaultCheckmarkStroke,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            // 实线 + 100 间隙;动画时 dashOffset 从 13.6 → 0,沿路径"绘制"。
            StrokeDashArray = new DoubleCollection { 0, 100 },
            StrokeDashOffset = 13.6,
            Data = Geometry.Parse("M 4,9 L 8,12 L 13,5"),
            Stretch = Stretch.None,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        AddVisualChild(_checkmarkPath);
        AddLogicalChild(_checkmarkPath);
    }

    /// <summary>设置对勾描边颜色(默认绿,可被调用方覆盖为红/橙等)。</summary>
    public void SetCheckmarkBrush(Brush brush)
    {
        if (_checkmarkPath != null && brush != null)
        {
            // 防御性冻结:外部传未冻结的 Brush 时,clone 后冻结避免渲染期间走慢路径。
            if (!brush.IsFrozen && brush is SolidColorBrush scb)
            {
                var frozen = new SolidColorBrush(scb.Color) { Opacity = scb.Opacity };
                frozen.Freeze();
                _checkmarkPath.Stroke = frozen;
                return;
            }
            _checkmarkPath.Stroke = brush;
        }
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual? GetVisualChild(int index)
    {
        if (index == 0) return _checkmarkPath;
        throw new System.ArgumentOutOfRangeException(nameof(index));
    }

    protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
    {
        if (_checkmarkPath != null)
        {
            _checkmarkPath.Measure(availableSize);
        }
        return base.MeasureOverride(availableSize);
    }

    protected override System.Windows.Size ArrangeOverride(System.Windows.Size finalSize)
    {
        if (_checkmarkPath != null)
        {
            _checkmarkPath.Arrange(new Rect(finalSize));
            // 用像素绝对坐标绘制对勾,总长为屏幕像素长度 13.6 px(由 18×18 圆环大小直接决定)。
            // 父级 BalanceProgressRing 强制 Width=Height=18,所以这里不用再按尺寸缩放。
            _checkmarkTotalLength = 13.6;
        }
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>
    /// 触发"圆环闭合 + 短暂停留 + 对勾描边"连续动画。
    /// 默认时序参考 https://css-tricks.com 的成功动画视觉节奏（圆环 ease-in-out 闭合 + 对勾 ease-out 描边），
    /// 总时长 ~950ms（600ms 圆环填充 + -50ms 提前让对勾在末端开始 + 400ms 对勾描边）。
    /// 调用方也可自定义参数适应不同场景（如 fade-out→fade-in 流中圆环填充会拉到 1000ms+ 配合更长节奏）。
    /// </summary>
    public void BeginCheckmarkAnimation(int fillDurationMs = 600,
                                       int holdDurationMs = -50,
                                       int strokeDurationMs = 400)
    {
        // 阶段 1: 圆环填充到 1（如果当前 < 1）
        // ease-in-out:圆环"充到满"的 S 形曲线,慢启动、中间加速、缓结束,与 HTML CSS 的 ease-in-out 同款。
        if (Value < 0.999)
        {
            var fillAnim = new DoubleAnimation(Value, 1.0,
                TimeSpan.FromMilliseconds(fillDurationMs))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            BeginAnimation(ValueProperty, fillAnim);
        }

        // 阶段 2 + 3:对勾描边动画。先停掉之前的动画避免叠加。
        // ease-out:描边起笔快、收笔慢的"画完一勾"自然感。
        if (_checkmarkPath != null)
        {
            _checkmarkPath.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            _checkmarkPath.Visibility = Visibility.Visible;
            var totalLength = _checkmarkTotalLength;
            _checkmarkPath.StrokeDashArray = new DoubleCollection { totalLength, totalLength };
            var beginTime = TimeSpan.FromMilliseconds(fillDurationMs + holdDurationMs);
            var strokeAnim = new DoubleAnimation
            {
                From = totalLength,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(strokeDurationMs),
                BeginTime = beginTime,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            _checkmarkPath.BeginAnimation(Path.StrokeDashOffsetProperty, strokeAnim);
        }
    }

    /// <summary>立即清除对勾显示(隐藏 + 停动画),用于动画前的状态重置。</summary>
    public void ResetCheckmark()
    {
        if (_checkmarkPath != null)
        {
            _checkmarkPath.BeginAnimation(Path.StrokeDashOffsetProperty, null);
            _checkmarkPath.Visibility = Visibility.Collapsed;
            _checkmarkPath.StrokeDashOffset = _checkmarkTotalLength;
        }
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(BalanceProgressRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

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

        // 按角度桶离散化判定:动画期间 value 跨多个相邻桶,大多数帧会命中上次的桶,
        // 避免每帧 new StreamGeometry + new Point[] + closure + Freeze。
        var bucket = (int)Math.Round(value / GeometryBucketStep);
        if (_cachedGeometry == null || _cachedGeometryBucket != bucket)
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
            _cachedGeometryBucket = bucket;
        }

        dc.DrawGeometry(null, GetValuePen(), _cachedGeometry);
    }
}
