using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TokenMonitor.App.Controls;

/// <summary>
/// 悬浮球展开态信息条（上翻式逐项滚动，用户要求取代横向跑马灯）：
/// 每项数据停留 5 秒 → 280ms 上翻切到下一项，循环。
/// 实现：双 ContentPresenter 交换——每次动画结束把位移复位到固定值（0 / RowH），
/// 不做累计平移，杜绝累计漂移导致整条滚出可视区的问题。
/// 数据 1s 原地刷新不打断节奏；条目增删时重建并保持节奏。
/// </summary>
public class MarqueeStrip : Border
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(System.Collections.IEnumerable), typeof(MarqueeStrip),
        new PropertyMetadata(null, (d, _) => ((MarqueeStrip)d).Rebuild()));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(MarqueeStrip),
        new PropertyMetadata(null, (d, _) => ((MarqueeStrip)d).Rebuild()));

    public System.Collections.IEnumerable? ItemsSource
    { get => (System.Collections.IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public DataTemplate? ItemTemplate
    { get => (DataTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }

    /// <summary>每项停留时长（用户要求 5 秒）。</summary>
    public TimeSpan Dwell { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>上翻动画时长。</summary>
    public TimeSpan RollDuration { get; set; } = TimeSpan.FromMilliseconds(280);
    private const double RowH = 52;

    private readonly Canvas _stage = new();
    private readonly ContentPresenter _cur = new();
    private readonly ContentPresenter _next = new();
    private readonly System.Collections.Generic.List<object> _items = new();
    private INotifyCollectionChanged? _observed;
    private DispatcherTimer? _dwell;
    private int _index;
    private bool _rolling;

    public MarqueeStrip()
    {
        ClipToBounds = true;
        _cur.Height = RowH;
        _next.Height = RowH;
        Canvas.SetLeft(_cur, 4); Canvas.SetLeft(_next, 4);
        Canvas.SetTop(_cur, 0); Canvas.SetTop(_next, RowH);
        _next.Opacity = 0;
        _stage.Children.Add(_cur);
        _stage.Children.Add(_next);
        Child = _stage;
        Loaded += (_, _) => Rebuild();
        Unloaded += (_, _) => StopDwell();
    }

    private System.Collections.IList? List => ItemsSource as System.Collections.IList;

    private void Rebuild()
    {
        ObserveCollection();
        _items.Clear();
        if (List is not null)
            foreach (var it in List) _items.Add(it);
        _index = 0;
        _cur.Content = _next.Content = null;
        if (_items.Count > 0)
        {
            _cur.Content = _items[0];
            _cur.ContentTemplate = ItemTemplate;
        }
        Restart();
    }

    private void ObserveCollection()
    {
        if (_observed is not null)
        {
            _observed.CollectionChanged -= OnCollectionChanged;
            _observed = null;
        }
        if (ItemsSource is INotifyCollectionChanged n)
        {
            _observed = n;
            n.CollectionChanged += OnCollectionChanged;
        }
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    /// <summary>外部控制（悬浮球展开时启动；收起停止）。</summary>
    public void Start() => Rebuild();
    public void Stop() => StopDwell();
    public void Pause() { try { _dwell?.Stop(); } catch { } }
    public void Resume() { try { if (!_rolling) _dwell?.Start(); } catch { } }

    private void Restart()
    {
        StopDwell();
        ResetPositions();
        if (_items.Count <= 1 || !IsLoaded) return;
        _dwell = new DispatcherTimer { Interval = Dwell };
        _dwell.Tick += (_, _) => RollOnce();
        _dwell.Start();
    }

    private void StopDwell()
    {
        try { _dwell?.Stop(); } catch { }
        _dwell = null;
    }

    private void ResetPositions()
    {
        _cur.BeginAnimation(Canvas.TopProperty, null);
        _next.BeginAnimation(Canvas.TopProperty, null);
        _next.BeginAnimation(OpacityProperty, null);
        Canvas.SetTop(_cur, 0);
        Canvas.SetTop(_next, RowH);
        _next.Opacity = 0;
    }

    private void RollOnce()
    {
        if (_rolling || _items.Count <= 1) return;
        _rolling = true;
        var nextIdx = (_index + 1) % _items.Count;
        _next.Content = _items[nextIdx];
        _next.ContentTemplate = ItemTemplate;
        _next.Opacity = 1;
        Canvas.SetTop(_next, RowH);

        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };
        var aCur = new DoubleAnimation(-RowH, RollDuration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        var aNext = new DoubleAnimation(0, RollDuration) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        int done = 0;
        void OnOneDone(object? s, EventArgs e)
        {
            if (Interlocked.Increment(ref done) < 2) return;
            // 交换：_cur 承接新内容，双呈现器复位到固定值（无累计漂移）
            _index = nextIdx;
            _cur.BeginAnimation(Canvas.TopProperty, null);
            _next.BeginAnimation(Canvas.TopProperty, null);
            _cur.Content = _items[_index];
            _cur.ContentTemplate = ItemTemplate;
            ResetPositions();
            _rolling = false;
        }
        aCur.Completed += OnOneDone;
        aNext.Completed += OnOneDone;
        _cur.BeginAnimation(Canvas.TopProperty, aCur);
        _next.BeginAnimation(Canvas.TopProperty, aNext);
    }
}
