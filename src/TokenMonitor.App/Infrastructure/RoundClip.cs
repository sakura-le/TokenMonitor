using System.Windows;
using System.Windows.Media;

namespace TokenMonitor.App.Infrastructure;

/// <summary>
/// 圆角裁剪（修"UI 元素溢出窗口/卡片四圆角"）：按元素实际尺寸 + CornerRadius 左上值
/// 生成 RectangleGeometry 裁剪元素及其全部子内容的渲染，防止子元素（标题栏底色、
/// 纹理、云朵等）盖住圆角外的透明区。Radius 用 CornerRadius 类型以便直接绑定
/// 皮肤令牌 Tg.Radius.*（DynamicResource 随皮肤切换自动重裁）。
/// </summary>
public static class RoundClip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(RoundClip), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(RoundClip), new PropertyMetadata(new CornerRadius(0), OnRadiusChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static CornerRadius GetRadius(DependencyObject obj) => (CornerRadius)obj.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject obj, CornerRadius value) => obj.SetValue(RadiusProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.SizeChanged -= OnSizeChanged;
            fe.SizeChanged += OnSizeChanged;
            Apply(fe);
        }
        else
        {
            fe.SizeChanged -= OnSizeChanged;
            fe.Clip = null;
        }
    }

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && GetEnabled(d)) Apply(fe);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        var cr = GetRadius(fe);
        var r = Math.Max(0, cr.TopLeft);
        if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0 || r <= 0)
        {
            fe.Clip = null;
            return;
        }
        var geo = new RectangleGeometry(new Rect(0, 0, fe.ActualWidth, fe.ActualHeight), r, r);
        geo.Freeze();
        fe.Clip = geo;
    }
}
