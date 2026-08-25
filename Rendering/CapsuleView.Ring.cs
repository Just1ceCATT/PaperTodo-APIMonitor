using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using PaperTodo.Plugin.ApiBalanceMonitor.Models;

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

        // Overlay 层：跨 5 列覆盖在 ring/label 之上，hook 触发时显示。
        BuildOverlayLayer();

        ApplyTheme(context.Theme);
    }

    // ----- Overlay 渲染（hook 触发时临时覆盖胶囊） -----
    private readonly Grid _overlayLayer = new();
    private readonly Border _overlaySpinnerBadge = new();
    private readonly TextBlock _overlaySpinnerText = new();
    private readonly RotateTransform _overlaySpinnerRotate = new();
    private readonly DoubleAnimation _overlaySpinnerAnim = new();
    private DispatcherTimer? _overlayCountdown;
    // Color overlay 缓存原始值,倒计时恢复时用
    private string? _storedLabelText;
    private string? _storedRingColorHex;
    private double _storedRingArc;

    private void BuildOverlayLayer()
    {
        _overlayLayer.HorizontalAlignment = HorizontalAlignment.Stretch;
        _overlayLayer.VerticalAlignment = VerticalAlignment.Stretch;
        _overlayLayer.IsHitTestVisible = false;
        _overlayLayer.Background = Brushes.Transparent;
        _overlayLayer.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(_overlayLayer, 5);
        Children.Add(_overlayLayer);

        _overlaySpinnerRotate.Angle = 0;
        _overlaySpinnerAnim.From = 0;
        _overlaySpinnerAnim.To = 360;
        _overlaySpinnerAnim.Duration = TimeSpan.FromSeconds(1.5);
        _overlaySpinnerAnim.RepeatBehavior = RepeatBehavior.Forever;
        _overlaySpinnerAnim.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
        _overlaySpinnerBadge.Width = 14;
        _overlaySpinnerBadge.Height = 14;
        _overlaySpinnerBadge.CornerRadius = new CornerRadius(2);
        _overlaySpinnerBadge.Background = new SolidColorBrush(Color.FromRgb(0x21, 0x96, 0xF3));
        _overlaySpinnerBadge.HorizontalAlignment = HorizontalAlignment.Left;
        _overlaySpinnerBadge.VerticalAlignment = VerticalAlignment.Center;
        _overlaySpinnerBadge.RenderTransform = _overlaySpinnerRotate;
        _overlaySpinnerBadge.RenderTransformOrigin = new Point(0.5, 0.5);
        _overlaySpinnerBadge.Child = BuildHourglassGlyph();
        _overlaySpinnerBadge.Visibility = Visibility.Collapsed;

        _overlaySpinnerText.Margin = new Thickness(20, 0, 0, 0);
        _overlaySpinnerText.VerticalAlignment = VerticalAlignment.Center;
        _overlaySpinnerText.FontSize = 12.0;
        _overlaySpinnerText.FontWeight = FontWeights.Medium;
        _overlaySpinnerText.Foreground = ToBrush("#2196F3", "#707070");
        _overlaySpinnerText.Visibility = Visibility.Collapsed;

        var spinnerRow = new Grid();
        spinnerRow.Children.Add(_overlaySpinnerBadge);
        spinnerRow.Children.Add(_overlaySpinnerText);
        _overlayLayer.Children.Add(spinnerRow);
    }

    /// <summary>蓝色沙漏图形：上三角 + 下三角。</summary>
    private static FrameworkElement BuildHourglassGlyph()
    {
        var canvas = new Canvas { Width = 14, Height = 14, Background = Brushes.Transparent };
        var top = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 2,1 L 12,1 L 7,7 Z"),
            Fill = Brushes.White
        };
        var bottom = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 7,7 L 12,13 L 2,13 Z"),
            Fill = Brushes.White
        };
        canvas.Children.Add(top);
        canvas.Children.Add(bottom);
        return canvas;
    }

    /// <summary>
    /// 设置 hook overlay：用 WPF 原生控件实现 PNG 描述的效果,不是直接显示 PNG。
    /// - Color overlay (Stop/Permission/Failure):改 _label.Text + _ring 颜色,带倒计时自动恢复
    /// - Spinner overlay (PreTool/PostTool):蓝色沙漏 + 固定文本,持续到下次 Update()
    /// </summary>
    public void SetHookOverlay(HookOverlayKind kind, int durationSeconds, string pluginDir)
    {
        // 先清掉旧倒计时与动画
        _overlayCountdown?.Stop();
        _overlayCountdown = null;
        _overlaySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _overlaySpinnerBadge.Visibility = Visibility.Collapsed;
        _overlaySpinnerText.Visibility = Visibility.Collapsed;

        // 恢复上一次 color overlay 的暂存值(防止叠加)
        RestoreColorOverlayIfAny();

        if (kind == HookOverlayKind.None)
        {
            _overlayLayer.Visibility = Visibility.Collapsed;
            return;
        }

        if (kind == HookOverlayKind.StopImage)
        {
            ShowColorOverlay("任务完成", "#4CAF50", 1.0, durationSeconds);
        }
        else if (kind == HookOverlayKind.PermissionImage)
        {
            ShowColorOverlay("等待回复", "#FF9800", 1.0, durationSeconds);
        }
        else if (kind == HookOverlayKind.FailureImage)
        {
            ShowColorOverlay("执行异常", "#F44336", 1.0, durationSeconds);
        }
        else if (kind == HookOverlayKind.PreToolSpinner)
        {
            ShowSpinnerOverlay("准备调用工具");
        }
        else if (kind == HookOverlayKind.PostToolSpinner)
        {
            ShowSpinnerOverlay("文件编辑完成");
        }
        PaperTodo.Plugin.ApiBalanceMonitor.Services.HookTrace.Write(
            $"SetHookOverlay kind={kind} dur={durationSeconds}s label='{_label.Text}'");
    }

    /// <summary>
    /// 临时修改 _label.Text + _ring 颜色 + arc,显示 hook 状态。
    /// 倒计时后由 RestoreColorOverlayIfAny 恢复。
    /// </summary>
    private void ShowColorOverlay(string text, string ringColorHex, double arc, int durationSeconds)
    {
        _storedLabelText = _label.Text;
        _storedRingColorHex = _ring.ForegroundBrush is SolidColorBrush sb ? sb.Color.ToString() : null;
        _storedRingArc = _ring.Value;
        _label.Text = text;
        _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
        _ring.Value = Math.Clamp(arc, 0, 1);
        _ring.InvalidateVisual();
        _label.InvalidateMeasure();
        // 倒计时恢复余额快照
        _overlayCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(durationSeconds) };
        _overlayCountdown.Tick += (_, _) =>
        {
            _overlayCountdown?.Stop();
            _overlayCountdown = null;
            RestoreColorOverlayIfAny();
        };
        _overlayCountdown.Start();
        PaperTodo.Plugin.ApiBalanceMonitor.Services.HookTrace.Write(
            $"ShowColorOverlay AFTER text='{_label.Text}'");
    }

    /// <summary>恢复暂存的 _label.Text / _ring 颜色 / arc;若未暂存则不动。</summary>
    private void RestoreColorOverlayIfAny()
    {
        if (_storedLabelText != null)
        {
            _label.Text = _storedLabelText;
            _storedLabelText = null;
        }
        if (_storedRingColorHex != null)
        {
            _ring.ForegroundBrush = ToBrush(_storedRingColorHex, "#9E9E9E");
            _ring.Value = _storedRingArc;
            _ring.InvalidateVisual();
            _storedRingColorHex = null;
        }
    }

    private void ShowSpinnerOverlay(string text)
    {
        _overlaySpinnerText.Text = text;
        _overlaySpinnerBadge.Visibility = Visibility.Visible;
        _overlaySpinnerText.Visibility = Visibility.Visible;
        _overlayLayer.Visibility = Visibility.Visible;
        _overlaySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, _overlaySpinnerAnim);
        // Spinner 类型无倒计时:持续到下次 Update() 由外部 HideHookOverlay 清掉
    }

    /// <summary>外部在 Update() 时清掉 spinner overlay(spinner 是"持续型")。</summary>
    public void HideHookOverlay()
    {
        _overlayCountdown?.Stop();
        _overlayCountdown = null;
        _overlaySpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
        _overlaySpinnerBadge.Visibility = Visibility.Collapsed;
        _overlaySpinnerText.Visibility = Visibility.Collapsed;
        RestoreColorOverlayIfAny();
        _overlayLayer.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// MiniMax 胶囊刷新：文本 + 圆环弧值 / 颜色（圆环颜色由风险档位 / 剩余百分比决定）。
    /// text 真正变化时触发 350ms 淡入（避免每次 polling tick 都闪）。
    /// </summary>
    public void Update(string text, string ringColorHex, double ringArc)
    {
        var textChanged = _label.Text != text;
        _label.Text = text;
        _ring.Value = Math.Clamp(ringArc, 0, 1);
        _ring.ForegroundBrush = ToBrush(ringColorHex, "#9E9E9E");
        _ring.InvalidateVisual();
        // 余额快照推送时清掉 spinner overlay（spinner 持续到下一次 Update）;
        // PNG overlay 已经有自己的倒计时,自然结束。
        if (_overlayLayer.Visibility == Visibility.Visible &&
            (_overlaySpinnerBadge.Visibility == Visibility.Visible ||
             _overlaySpinnerText.Visibility == Visibility.Visible))
        {
            HideHookOverlay();
        }
        if (textChanged)
        {
            BeginLabelFadeIn();
        }
    }

    /// <summary>
    /// 文字淡入：Opacity 0 → 1, 350ms CubicEase EaseOut。
    /// 只动 _label.Opacity，不影响呼吸动效与 disableRing 的列宽。
    /// </summary>
    private void BeginLabelFadeIn()
    {
        _label.Opacity = 0;
        var anim = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(350),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        _label.BeginAnimation(UIElement.OpacityProperty, anim);
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
        // 修复 D：列宽变了，文字可显示宽度变了，强制重测避免一帧截断。
        _label.InvalidateMeasure();
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