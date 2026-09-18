using System.Windows;
using System.Windows.Controls;

namespace TokenMonitor.App.Controls;

/// <summary>
/// C-09 DualClock —— UTC 与 LOCAL 双钟（03-ui-spec §1.3 / 样例 clocks 区）。
/// 每秒走字由 ViewModel 驱动（只改文本，不动结构）；S2 闪烁光标由模板触发。
/// </summary>
public class DualClockControl : Control
{
    public static readonly DependencyProperty Zone1LabelProperty = DependencyProperty.Register(
        nameof(Zone1Label), typeof(string), typeof(DualClockControl), new PropertyMetadata("UTC"));

    public static readonly DependencyProperty Zone1TimeProperty = DependencyProperty.Register(
        nameof(Zone1Time), typeof(string), typeof(DualClockControl), new PropertyMetadata("--:--:--"));

    public static readonly DependencyProperty Zone2LabelProperty = DependencyProperty.Register(
        nameof(Zone2Label), typeof(string), typeof(DualClockControl), new PropertyMetadata("LOCAL"));

    public static readonly DependencyProperty Zone2TimeProperty = DependencyProperty.Register(
        nameof(Zone2Time), typeof(string), typeof(DualClockControl), new PropertyMetadata("--:--:--"));

    public string Zone1Label { get => (string)GetValue(Zone1LabelProperty); set => SetValue(Zone1LabelProperty, value); }
    public string Zone1Time { get => (string)GetValue(Zone1TimeProperty); set => SetValue(Zone1TimeProperty, value); }
    public string Zone2Label { get => (string)GetValue(Zone2LabelProperty); set => SetValue(Zone2LabelProperty, value); }
    public string Zone2Time { get => (string)GetValue(Zone2TimeProperty); set => SetValue(Zone2TimeProperty, value); }

    static DualClockControl() => DefaultStyleKeyProperty.OverrideMetadata(typeof(DualClockControl),
        new FrameworkPropertyMetadata(typeof(DualClockControl)));
}
