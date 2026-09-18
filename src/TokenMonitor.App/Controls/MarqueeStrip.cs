using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Media;

namespace TokenMonitor.App.Controls;

/// <summary>
/// 悬浮球展开态跑马灯（03-ui-spec §4 展开态）。
/// 内容整条复制两份，TranslateTransform.X 0→-50% 16s Linear 永动；
/// 数据 1s 刷新只改文本不重置动画（动画挂在 Rail 的 RenderTransform 上，
/// ItemsSource 原地更新不触发动画重建）；鼠标悬停 Pause；两端 5% 渐隐遮罩由模板 OpacityMask 提供。
/// </summary>
[TemplatePart(Name = PartRail, Type = typeof(FrameworkElement))]
public class MarqueeStrip : Control
{
    public const string PartRail = "PART_Rail";

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(System.Collections.IEnumerable), typeof(MarqueeStrip),
        new PropertyMetadata(null));

    public static readonly DependencyProperty ItemTemplateProperty = DependencyProperty.Register(
        nameof(ItemTemplate), typeof(DataTemplate), typeof(MarqueeStrip), new PropertyMetadata(null));

    public System.Collections.IEnumerable? ItemsSource
    { get => (System.Collections.IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }

    public DataTemplate? ItemTemplate
    { get => (DataTemplate?)GetValue(ItemTemplateProperty); set => SetValue(ItemTemplateProperty, value); }

    private Panel? _rail;
    private AnimationClock? _clock;

    static MarqueeStrip() => DefaultStyleKeyProperty.OverrideMetadata(typeof(MarqueeStrip),
        new FrameworkPropertyMetadata(typeof(MarqueeStrip)));

    public MarqueeStrip()
    {
        Loaded += (_, _) => StartAnimation();
        Unloaded += (_, _) => StopAnimation();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _rail = GetTemplateChild(PartRail) as Panel;
        if (_rail is not null)
        {
            _rail.RenderTransform = new TranslateTransform();
            _rail.SizeChanged += (_, _) => StartAnimation();
        }
        MouseEnter += (_, _) => Pause();
        MouseLeave += (_, _) => Resume();
        StartAnimation();
    }

    /// <summary>外部控制（悬浮球展开时启动；悬停暂停）。</summary>
    public void Start() => StartAnimation();
    public void Stop() => StopAnimation();
    public void Pause() { try { _clock?.Controller.Pause(); } catch { } }
    public void Resume() { try { _clock?.Controller.Resume(); } catch { } }

    private void StartAnimation()
    {
        if (_rail is null || !IsLoaded || ActualWidth <= 0) return;
        StopAnimation();
        // 周期按 §5 表（16s Linear 循环）；半宽 = 单份内容宽
        var half = _rail.ActualWidth / 2.0;
        if (half <= 0) return;
        var duration = TimeSpan.FromMilliseconds(
            (double)(Application.Current?.TryFindResource("Tg.Dur.MarqueeMs") ?? 16000.0));
        var anim = new DoubleAnimation(0, -half, duration)
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var clock = anim.CreateClock();
        _clock = clock;
        ((TranslateTransform)_rail.RenderTransform).ApplyAnimationClock(TranslateTransform.XProperty, clock);
    }

    private void StopAnimation()
    {
        try { _clock?.Controller.Remove(); } catch { /* 已停止 */ }
        _clock = null;
    }
}
