using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.Services;
using TokenMonitor.App.ViewModels;

namespace TokenMonitor.App;

/// <summary>
/// 悬浮球窗口（03-ui-spec §4）：透明分层窗口，两态（收起 Ø68 / 展开 470×56 胶囊）。
/// 拖拽 + 12px 屏幕边缘磁吸（180ms 回弹落位）；双击回主面板；右键 = 托盘菜单；
/// 透明度/置顶按 settings；跑马灯动画不因数据刷新重置（MarqueeStrip 保证）。
/// </summary>
public partial class FloatingBall : Window
{
    private readonly AppServices _svc;
    private BallViewModel? _vm;
    private AnimationClock? _radarClock;

    private void StartRadar(TimeSpan period)
    {
        try { _radarClock?.Controller.Remove(); } catch { }
        if (ballRadar is null) return;
        var anim = new DoubleAnimation(0, 360, period) { RepeatBehavior = RepeatBehavior.Forever };
        _radarClock = anim.CreateClock();
        ballRadar.ApplyAnimationClock(RotateTransform.AngleProperty, _radarClock);
    }
    private bool _expanded;
    private bool _preferExpanded;   // 静息形态：false=圆球（默认），true=跑马灯栏；hover 临时展开
    private bool _dragging;
    private Point _dragOffset;
    private bool _movedBeyondClick;

    public FloatingBall()
    {
        InitializeComponent();
        _svc = AppServices.Instance;
        Loaded += OnLoaded;
        SourceInitialized += (_, _) => _svc.UiState.RestoreBall(this);

        PreviewMouseLeftButtonDown += OnMouseDown;
        PreviewMouseMove += OnMouseMove;
        PreviewMouseLeftButtonUp += OnMouseUp;
        MouseDoubleClick += (_, _) => _svc.ShowPanel();
        // hover 自动展开跑马灯、离开回落到静息形态（托盘可切换静息形态）
        MouseEnter += (_, _) => { if (!_dragging) SetExpanded(true); if (_expanded) Strip.Pause(); };
        MouseLeave += (_, _) => { if (!_dragging) SetExpanded(_preferExpanded); if (_expanded) Strip.Resume(); };
    }

    private BallViewModel Vm => _vm ??= (BallViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DataContext = _vm = AppServices.Instance.Ball;
        Strip.ItemsSource = _vm!.Items;
        // 右键菜单：绑定到窗口由 WPF 托管开合（自动处理捕获/重绘）。
        // 不用手工 menu.IsOpen=true —— 该方式在透明分层窗口上会引发渲染停滞（球面变空白）。
        ContextMenu = _svc.Tray?.BallMenu;
        // settings: 透明度（0-255）与置顶
        var s = _svc.Engine.Config.Settings;
        Opacity = s.BallOpacity <= 0 ? 1.0 : Math.Clamp(s.BallOpacity / 255.0, 0.2, 1.0);
        Topmost = s.BallTopmost;
        UpdateHitArc(_vm.HitFraction);
        _vm.PropertyChanged += (_, e2) =>
        {
            if (e2.PropertyName is nameof(BallViewModel.HitFraction)) UpdateHitArc(_vm.HitFraction);
        };

        // S4 雷达扫描扇：6s Linear 永动；激增 2.5s（§5 映射；§3.2 坑 4：由代码重建动画）
        StartRadar(TimeSpan.FromMilliseconds(6000));
        Infrastructure.SkinFlags.Instance.SurgeChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => StartRadar(Infrastructure.SkinFlags.Instance.SurgeRadar
                ? TimeSpan.FromMilliseconds(2500)
                : TimeSpan.FromMilliseconds(6000)));
    }

    /// <summary>命中弧几何（起点朝上，= hit÷(hit+miss)）。</summary>
    private void UpdateHitArc(double fraction)
    {
        var f = Math.Clamp(fraction, 0, 1);
        const double cx = 34, cy = 34, r = 30;
        var geo = new PathGeometry();
        if (f <= 0)
        {
            HitArc.Data = geo;
            return;
        }
        var fig = new PathFigure { StartPoint = new Point(cx, cy - r), IsClosed = false, IsFilled = false };
        if (f >= 1)
        {
            fig.Segments.Add(new ArcSegment(new Point(cx, cy + r), new Size(r, r), 0, true, SweepDirection.Clockwise, true));
            fig.Segments.Add(new ArcSegment(new Point(cx, cy - r), new Size(r, r), 0, true, SweepDirection.Clockwise, true));
        }
        else
        {
            var angle = f * 2 * Math.PI;
            fig.Segments.Add(new ArcSegment(
                new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle)),
                new Size(r, r), 0, f > 0.5, SweepDirection.Clockwise, true));
        }
        geo.Figures.Add(fig);
        geo.Freeze();
        HitArc.Data = geo;
    }

    // ================= 两态 =================

    public void ToggleState() => SetExpanded(!_expanded);

    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded) return;
        _expanded = expanded;
        var w = expanded ? 470d : 68d;
        var h = expanded ? 56d : 68d;
        var wa = SystemParameters.WorkArea;
        // 首次显示前的 NaN 兜底（未 Show 过的窗口 Left/Top/Width 均为 NaN）
        if (double.IsNaN(Width) || double.IsNaN(Height)) { Width = w; Height = h; }
        if (double.IsNaN(Left) || double.IsNaN(Top))
        {
            Left = wa.Right - w - 12;
            Top = wa.Bottom - 160;
        }
        // 展开时防止超出右缘
        var targetLeft = expanded ? Math.Min(Left, Math.Max(wa.Right - w - 8, 0)) : Left;

        var animW = new DoubleAnimation(Width, w, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease() };
        var animH = new DoubleAnimation(Height, h, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease() };
        var animL = new DoubleAnimation(Left, targetLeft, TimeSpan.FromMilliseconds(220)) { EasingFunction = Ease() };
        BeginAnimation(WidthProperty, animW);
        BeginAnimation(HeightProperty, animH);
        BeginAnimation(LeftProperty, animL);

        if (expanded)
        {
            Pill.Visibility = Visibility.Visible;
            BallFace.Visibility = Visibility.Collapsed;
            Strip.Start();
        }
        else
        {
            BallFace.Visibility = Visibility.Visible;
            Strip.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(110));
            fade.Completed += (_, _) => { Pill.Visibility = Visibility.Collapsed; Pill.Opacity = 1; };
            Pill.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>面板收起后联动（AppServices.CollapseToBall）：以静息形态出现（默认=圆球）。</summary>
    public void ExpandFromPanel()
    {
        if (!IsVisible)
        {
            // 从未显示过：先定位再显示（SourceInitialized 已随 Show 触发 RestoreBall）
            Show();
            if (double.IsNaN(Left) || double.IsNaN(Top))
            {
                var wa = SystemParameters.WorkArea;
                Left = wa.Right - 80;
                Top = wa.Bottom - 160;
            }
        }
        SetExpanded(_preferExpanded);
    }

    /// <summary>托盘「切换形态」：切换静息形态（圆球 ↔ 跑马灯栏）。</summary>
    public void ToggleShape()
    {
        _preferExpanded = !_preferExpanded;
        SetExpanded(_preferExpanded);
    }

    private static IEasingFunction Ease() => new CircleEase { EasingMode = EasingMode.EaseOut };

    // ================= 拖拽 + 磁吸 =================

    private Point _origScreenDip;
    private double _origLeft, _origTop;

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _movedBeyondClick = false;
        _dragOffset = e.GetPosition(this);
        _origLeft = Left;
        _origTop = Top;
        var sp = PointToScreen(_dragOffset);
        var dpi = VisualTreeHelper.GetDpi(this);
        _origScreenDip = new Point(sp.X / dpi.DpiScaleX, sp.Y / dpi.DpiScaleY);
        CaptureMouse();
        // 拖拽中：scale 1.06（§4）
        var st = BallFace.RenderTransform as ScaleTransform ?? new ScaleTransform();
        st.ScaleX = st.ScaleY = 1.06;
        BallFace.RenderTransformOrigin = new Point(0.5, 0.5);
        BallFace.RenderTransform = st;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var sp = PointToScreen(e.GetPosition(this));
        var cur = new Point(sp.X / dpi.DpiScaleX, sp.Y / dpi.DpiScaleY);
        var left = _origLeft + (cur.X - _origScreenDip.X);
        var top = _origTop + (cur.Y - _origScreenDip.Y);
        if (Math.Abs(left - Left) > 3 || Math.Abs(top - Top) > 3) _movedBeyondClick = true;
        Left = left;
        Top = top;
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        var st = BallFace.RenderTransform as ScaleTransform;
        if (st is not null) { st.ScaleX = st.ScaleY = 1.0; }
        if (_movedBeyondClick)
        {
            SnapToEdge();
            _svc.UiState.Save(null, this, null);
        }
    }

    /// <summary>接近屏幕边缘 12px 磁吸，180ms 回弹落位。</summary>
    private void SnapToEdge()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;
        var wa = SystemParameters.WorkArea;
        const double magnet = 12;
        var targetLeft = Left;
        var targetTop = Top;
        var size = _expanded ? ActualWidth : 68;

        if (Left < wa.Left + magnet) targetLeft = wa.Left + 2;
        else if (Left + size > wa.Right - magnet) targetLeft = wa.Right - size - 2;
        if (Top < wa.Top + magnet) targetTop = wa.Top + 2;
        else if (Top + 68 > wa.Bottom - magnet) targetTop = wa.Bottom - 70;

        var animL = new DoubleAnimation(Left, targetLeft, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease() };
        var animT = new DoubleAnimation(Top, targetTop, TimeSpan.FromMilliseconds(180)) { EasingFunction = Ease() };
        BeginAnimation(LeftProperty, animL);
        BeginAnimation(TopProperty, animT);
    }

    // ================= 右键 / 时钟 =================
    // 右键菜单改由窗口 ContextMenu 托管（见 OnLoaded），不再手工弹出。
}
