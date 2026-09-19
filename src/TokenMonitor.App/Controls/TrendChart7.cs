using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace TokenMonitor.App.Controls;

/// <summary>
/// C-07 TrendChart7 —— 近 7 日总 Token 折线（03-ui-spec §3.3）。
/// StreamGeometry（PenLineCap/Join=Round，Stroke≈1.6-2.2）+ 3 条弱网格线 +
/// 7 个透明热点 → 命中弹 Popup（「MM-DD · N.NM」，跟随钳位）；
/// 入场：Reveal 0→1（Power3 出 650ms）按比例揭示；
/// 终点标记（S3/S4）在完成后弹现。
/// 颜色全部 FindResource 现取（§3.2 坑 3：禁止缓存 Brush），皮肤切换 → InvalidateVisual。
/// </summary>
public class TrendChart7 : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<double>), typeof(TrendChart7),
        new PropertyMetadata(null, (d, e) => ((TrendChart7)d).OnSeriesChanged(e.NewValue, true)));

    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(
        nameof(Labels), typeof(IReadOnlyList<string>), typeof(TrendChart7),
        new PropertyMetadata(null, (d, e) => ((TrendChart7)d).OnSeriesChanged(e.NewValue, false)));

    /// <summary>描边宽度（1.6-2.2）。</summary>
    public static readonly DependencyProperty LineWidthProperty = DependencyProperty.Register(
        nameof(LineWidth), typeof(double), typeof(TrendChart7),
        new PropertyMetadata(1.8, (d, _) => ((TrendChart7)d).InvalidateVisual()));

    /// <summary>揭示进度 0-1（入场动画目标）。</summary>
    public static readonly DependencyProperty RevealProperty = DependencyProperty.Register(
        nameof(Reveal), typeof(double), typeof(TrendChart7),
        new PropertyMetadata(1.0, (d, _) => ((TrendChart7)d).InvalidateVisual()));

    public IReadOnlyList<double>? Points { get => (IReadOnlyList<double>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public IReadOnlyList<string>? Labels { get => (IReadOnlyList<string>?)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public double LineWidth { get => (double)GetValue(LineWidthProperty); set => SetValue(LineWidthProperty, value); }
    public double Reveal { get => (double)GetValue(RevealProperty); set => SetValue(RevealProperty, value); }

    // 布局常量（对照样例 svg 242×90：左右边 8，绘图区上 6 下 18）
    private const double PadL = 8, PadR = 8, PadT = 6, PadB = 18;

    private readonly List<double> _xs = new();
    private readonly List<double> _ys = new();
    private int _hotIndex = -1;
    private readonly Popup _popup;
    private readonly TextBlock _popupText;

    public TrendChart7()
    {
        ClipToBounds = true;
        Infrastructure.SkinFlags.Instance.PropertyChanged += (_, _) => InvalidateVisual();

        _popupText = new TextBlock { FontSize = 10, FontWeight = FontWeights.Bold };
        _popupText.SetResourceReference(TextBlock.ForegroundProperty, "Tg.OnAccent");
        _popupText.SetResourceReference(TextBlock.FontFamilyProperty, "Tg.Font.Num");
        var host = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Child = _popupText,
            SnapsToDevicePixels = true
        };
        host.SetResourceReference(Border.BackgroundProperty, "Tg.Accent.Brand");
        _popup = new Popup
        {
            Placement = PlacementMode.RelativePoint,
            PlacementTarget = this,
            AllowsTransparency = true,
            StaysOpen = true,
            IsHitTestVisible = false,
            PopupAnimation = PopupAnimation.None,
            Child = host
        };
        IsVisibleChanged += (_, e) => { if (!(bool)e.NewValue) _popup.IsOpen = false; };
    }

    /// <summary>入场/重算描边动画（§5：650ms Power3 出，仅首次与重算后调用）。</summary>
    public void PlayEntrance()
    {
        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650))
        {
            EasingFunction = new PowerEase { Power = 3, EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        BeginAnimation(RevealProperty, anim);
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    private IReadOnlyList<double>? _lastPoints;
    private IReadOnlyList<string>? _lastLabels;

    /// <summary>脏检查：上游（200ms 快照路径）可能每次赋新实例但值不变——值相同跳过重绘。</summary>
    private void OnSeriesChanged(object? newValue, bool isPoints)
    {
        if (isPoints)
        {
            var np = newValue as IReadOnlyList<double>;
            if (SameContents(_lastPoints, np)) return;
            _lastPoints = np;
        }
        else
        {
            var nl = newValue as IReadOnlyList<string>;
            if (SameContents(_lastLabels, nl)) return;
            _lastLabels = nl;
        }
        InvalidateVisual();
    }

    private static bool SameContents<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
            if (!Equals(a[i], b[i])) return false;
        return true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var grid = Res("Tg.Chart.Grid");
        var lineBrush = Res("Tg.Chart.Line");
        var dotBrush = Res("Tg.Chart.LineDot");

        // 3 条弱网格线（底 / 中 / 顶）
        var gp = new Pen(grid, 1);
        gp.Freeze();
        var top = PadT;
        var bottom = h - PadB;
        dc.DrawLine(gp, new Point(PadL, bottom), new Point(w - PadR, bottom));
        dc.DrawLine(gp, new Point(PadL, (top + bottom) / 2), new Point(w - PadR, (top + bottom) / 2));
        dc.DrawLine(gp, new Point(PadL, top), new Point(w - PadR, top));

        var pts = Points;
        if (pts is null || pts.Count == 0) return;

        var n = pts.Count;
        var max = Math.Max(1, pts.Max());
        _xs.Clear();
        _ys.Clear();
        var plotW = w - PadL - PadR;
        var plotH = bottom - top;
        for (var i = 0; i < n; i++)
        {
            _xs.Add(n == 1 ? PadL : PadL + plotW * i / (n - 1));
            _ys.Add(bottom - plotH * (pts[i] / max));
        }

        // 折线（Reveal 按比例揭示；至少 2 点成线）
        var count = Reveal >= 1 ? n : Math.Max(2, (int)Math.Ceiling(n * Math.Clamp(Reveal, 0, 1)));
        count = Math.Min(count, n);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(_xs[0], _ys[0]), false, false);
            for (var i = 1; i < count; i++)
                ctx.LineTo(new Point(_xs[i], _ys[i]), true, true);
        }
        geo.Freeze();
        var pen = new Pen(lineBrush, LineWidth)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        dc.DrawGeometry(null, pen, geo);

        // 终点标记（S3/S4 完成后弹现 3.0；其余肤常显 2.2 弱点）
        if (Reveal >= 1)
        {
            var flags = Infrastructure.SkinFlags.Instance;
            var r = flags.IsSwissGrid || flags.IsSkyHud ? 3.0 : 2.2;
            dc.PushOpacity(flags.IsSwissGrid || flags.IsSkyHud ? 1.0 : 0.85);
            dc.DrawEllipse(dotBrush, null, new Point(_xs[count - 1], _ys[count - 1]), r, r);
            dc.Pop();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pts = Points;
        if (pts is null || pts.Count == 0) return;
        var p = e.GetPosition(this);
        var best = -1;
        var bestD = 14.0;
        for (var i = 0; i < _xs.Count; i++)
        {
            var dx = Math.Abs(_xs[i] - p.X);
            var dy = Math.Abs(_ys[i] - p.Y);
            var d = Math.Max(dx, dy * 1.5);
            if (d < bestD) { bestD = d; best = i; }
        }
        if (best == _hotIndex) return;
        _hotIndex = best;
        if (best < 0)
        {
            _popup.IsOpen = false;
            return;
        }
        var label = Labels is not null && best < Labels.Count ? Labels[best] : "";
        var v = pts[best];
        var vs = v >= 1_000_000
            ? (v / 1_000_000).ToString("0.#", CultureInfo.InvariantCulture) + "M"
            : v >= 1000
                ? (v / 1000).ToString("0.#", CultureInfo.InvariantCulture) + "K"
                : v.ToString("N0", CultureInfo.InvariantCulture);
        _popupText.Text = $"{label} · {vs}";
        _popup.HorizontalOffset = Math.Clamp(_xs[best] + 8, 0, Math.Max(0, ActualWidth - 96));
        _popup.VerticalOffset = Math.Max(0, _ys[best] - 28);
        _popup.IsOpen = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hotIndex = -1;
        _popup.IsOpen = false;
    }
}
