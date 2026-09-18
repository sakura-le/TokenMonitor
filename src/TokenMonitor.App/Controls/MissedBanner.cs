using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TokenMonitor.App.Controls;

/// <summary>
/// C-02 MissedBanner —— 漏抓警示条（03-ui-spec §3.3）。
/// 固定高 34、圆角 {Tg.Radius.Control}；左侧 3px 危险色缘 + 徽章「漏抓」+
/// 文案「检测到 N 个漏抓请求，请校准补录」+ 去补录 + ✕；S4 斜纹底由模板叠加。
/// 静态常驻，无任何循环动画；显隐（高度 0↔34 + 淡入 180ms）由宿主窗口执行。
/// </summary>
public class MissedBanner : Control
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(long), typeof(MissedBanner), new PropertyMetadata(0L));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MissedBanner),
        new PropertyMetadata("检测到 0 个漏抓请求，请校准补录"));

    public static readonly DependencyProperty GoCommandProperty = DependencyProperty.Register(
        nameof(GoCommand), typeof(ICommand), typeof(MissedBanner), new PropertyMetadata(null));

    public static readonly DependencyProperty CloseCommandProperty = DependencyProperty.Register(
        nameof(CloseCommand), typeof(ICommand), typeof(MissedBanner), new PropertyMetadata(null));

    public long Count { get => (long)GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ICommand? GoCommand { get => (ICommand?)GetValue(GoCommandProperty); set => SetValue(GoCommandProperty, value); }
    public ICommand? CloseCommand { get => (ICommand?)GetValue(CloseCommandProperty); set => SetValue(CloseCommandProperty, value); }

    static MissedBanner() => DefaultStyleKeyProperty.OverrideMetadata(typeof(MissedBanner),
        new FrameworkPropertyMetadata(typeof(MissedBanner)));
}
