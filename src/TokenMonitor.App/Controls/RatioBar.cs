using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace TokenMonitor.App.Controls;

public enum RatioKind { Hit, Miss, Output }

/// <summary>
/// C-04 RatioBar —— H 缓存命中 / M 未命中 / O 输出 占比条（03-ui-spec §3.3）。
/// Grid 3 列 32|*|40；Track 高 6-7、CornerRadius={Tg.Radius.Bar}、ClipToBounds；
/// Fill 宽度 = 轨道宽 × Ratio（MultiplyConverter）；S2/S4 轨道刻纹由 Tg.Texture.Bars 提供。
/// </summary>
[TemplatePart(Name = PartTrack, Type = typeof(FrameworkElement))]
[TemplatePart(Name = PartFill, Type = typeof(FrameworkElement))]
public class RatioBar : Control
{
    public const string PartTrack = "PART_Track";
    public const string PartFill = "PART_Fill";

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RatioBar), new PropertyMetadata(""));

    /// <summary>占比 0-1（of 总量）。</summary>
    public static readonly DependencyProperty RatioProperty = DependencyProperty.Register(
        nameof(Ratio), typeof(double), typeof(RatioBar), new PropertyMetadata(0.0, OnRatioChanged));

    public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
        nameof(ValueText), typeof(string), typeof(RatioBar), new PropertyMetadata("0"));

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(RatioKind), typeof(RatioBar), new PropertyMetadata(RatioKind.Hit));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }
    public RatioKind Kind { get => (RatioKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private FrameworkElement? _track;
    private FrameworkElement? _fill;

    static RatioBar() => DefaultStyleKeyProperty.OverrideMetadata(typeof(RatioBar),
        new FrameworkPropertyMetadata(typeof(RatioBar)));

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _track = GetTemplateChild(PartTrack) as FrameworkElement;
        _fill = GetTemplateChild(PartFill) as FrameworkElement;
        BindFillWidth();
        UpdateClamp();
    }

    private static void OnRatioChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RatioBar)d).UpdateClamp();

    private void BindFillWidth()
    {
        if (_track is null || _fill is null) return;
        var mb = new MultiBinding
        {
            Converter = Infrastructure.MultiplyConverter.Instance,
            Mode = BindingMode.OneWay
        };
        mb.Bindings.Add(new Binding(nameof(ActualWidth)) { Source = _track, Mode = BindingMode.OneWay });
        mb.Bindings.Add(new Binding(nameof(Ratio)) { Source = this, Mode = BindingMode.OneWay });
        _fill.SetBinding(WidthProperty, mb);
    }

    /// <summary>占比钳制到 [0,1]，非有限值视为 0。</summary>
    private void UpdateClamp()
    {
        if (_fill is null) return;
        var r = Ratio;
        _fill.Visibility = r > 0.0005 && !double.IsNaN(r) ? Visibility.Visible : Visibility.Collapsed;
    }
}
