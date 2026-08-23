using System.Windows;
using System.Windows.Media;

namespace PaperTodo.Plugin.ApiBalanceMonitor.Rendering;

/// <summary>
/// 圆环进度控件。完全 1:1 复刻宿主 CapsuleProgressRing(PaperWindow.PluginCapsule.cs):
/// Pen 粗细 2,半径 = max(1, size/2 - 1.5),起点 -90° 顺时针,value≥0.999 画整圆。
///
/// 性能优化(避免 5 秒延迟):
/// - Pen 缓存:仅在 TrackBrush / ForegroundBrush 引用变化时重建并 Freeze。
/// - StreamGeometry 缓存:仅在 value 变化时重建弧形几何,Freeze 后可跨帧复用。
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