using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace TokenMonitor.App.Controls;

/// <summary>
/// C-05 RingGauge —— 缓存命中率环形表盘（03-ui-spec §3.3）。
/// Viewbox 内 88×88 坐标系；前景 Path(ArcSegment) StrokeStart/EndLineCap=Round；
/// 起点 -90°（朝上）；S2 外圈虚线刻度圆 / S4 雷达底层由模板按 SkinFlags 显隐。
/// 入场动画：Fraction 0→目标值 Power3 出（图表入场 600-700ms，仅首次与重算后调用 PlayEntrance）。
/// </summary>
[TemplatePart(Name = PartProgress, Type = typeof(Path))]
public class RingGauge : Control
{
    public const string PartProgress = "PART_Progress";
    private const double Cx = 44, Cy = 44, R = 36;

    public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
        nameof(Fraction), typeof(double), typeof(RingGauge),
        new PropertyMetadata(0.0, (d, _) => ((RingGauge)d).UpdateArc()));

    public static readonly DependencyProperty PercentTextProperty = DependencyProperty.Register(
        nameof(PercentText), typeof(string), typeof(RingGauge), new PropertyMetadata("0%"));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(RingGauge), new PropertyMetadata("HIT RATE"));

    /// <summary>命中率 = hit÷(hit+miss)，0-1。</summary>
    public double Fraction { get => (double)GetValue(FractionProperty); set => SetValue(FractionProperty, value); }
    public string PercentText { get => (string)GetValue(PercentTextProperty); set => SetValue(PercentTextProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    private Path? _progress;

    static RingGauge() => DefaultStyleKeyProperty.OverrideMetadata(typeof(RingGauge),
        new FrameworkPropertyMetadata(typeof(RingGauge)));

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _progress = GetTemplateChild(PartProgress) as Path;
        UpdateArc();
    }

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        // 皮肤切换后几何不变（颜色走 DynamicResource），无需重建
    }

    private void UpdateArc()
    {
        if (_progress is null) return;
        var f = double.IsNaN(Fraction) ? 0 : Math.Clamp(Fraction, 0, 1);
        var geo = new PathGeometry { Figures = new PathFigureCollection() };
        if (f <= 0)
        {
            _progress.Data = geo;
            return;
        }

        var fig = new PathFigure { StartPoint = new Point(Cx, Cy - R), IsClosed = false, IsFilled = false };
        if (f >= 1)
        {
            // 满圆：两段 180° 弧
            fig.Segments.Add(new ArcSegment(
                new Point(Cx, Cy + R),
                new Size(R, R), 0, true, SweepDirection.Clockwise, true));
            fig.Segments.Add(new ArcSegment(
                new Point(Cx, Cy - R),
                new Size(R, R), 0, true, SweepDirection.Clockwise, true));
        }
        else
        {
            var angle = f * 2 * Math.PI;
            var x = Cx + R * Math.Sin(angle);
            var y = Cy - R * Math.Cos(angle);
            fig.Segments.Add(new ArcSegment(
                new Point(x, y),
                new Size(R, R), 0, f > 0.5, SweepDirection.Clockwise, true));
        }
        geo.Figures.Add(fig);
        geo.Freeze();
        _progress.Data = geo;
    }

    /// <summary>入场/重算动画：Fraction 0→当前值，Power3 出，600ms（§5 图表入场行）。</summary>
    public void PlayEntrance(double? target = null)
    {
        var to = target ?? Fraction;
        var anim = new DoubleAnimation(0, Math.Clamp(to, 0, 1), TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        BeginAnimation(FractionProperty, anim);
    }
}
