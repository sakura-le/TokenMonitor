using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TokenMonitor.App.Controls;

/// <summary>
/// C-03 ModelCard —— 模型卡（03-ui-spec §3.3）。
/// 结构 Grid 行：Header(ProviderBadge/Title/MissChip) → Row1(请求数|增量) →
/// Total 大数字 → Bars×3 → Footer(实际/倍率分段+口径点)。
/// 选中态四肤差异（§1.6/§3.3）经 SkinFlags 显隐：S1 偏移硬影 / S2·S4 四角记号 /
/// S3 顶杠+序号染色；口径 UTC → 卡描边换 Warn。模板在 Controls.Shared.xaml。
/// </summary>
public class ModelCard : Control
{
    public static readonly DependencyProperty ProviderTextProperty = DependencyProperty.Register(
        nameof(ProviderText), typeof(string), typeof(ModelCard), new PropertyMetadata(""));

    /// <summary>提供商徽章键：Glm / DeepSeek / Qwen / Default（→ Tg.Provider.* 画刷触发）。</summary>
    public static readonly DependencyProperty ProviderKindProperty = DependencyProperty.Register(
        nameof(ProviderKind), typeof(string), typeof(ModelCard), new PropertyMetadata("Default"));

    public static readonly DependencyProperty ModelNameProperty = DependencyProperty.Register(
        nameof(ModelName), typeof(string), typeof(ModelCard), new PropertyMetadata(""));

    public static readonly DependencyProperty RequestCountTextProperty = DependencyProperty.Register(
        nameof(RequestCountText), typeof(string), typeof(ModelCard), new PropertyMetadata("0"));

    public static readonly DependencyProperty DeltaTextProperty = DependencyProperty.Register(
        nameof(DeltaText), typeof(string), typeof(ModelCard), new PropertyMetadata(""));

    public static readonly DependencyProperty TotalTextProperty = DependencyProperty.Register(
        nameof(TotalText), typeof(string), typeof(ModelCard), new PropertyMetadata("0"));

    public static readonly DependencyProperty TotalUnitTextProperty = DependencyProperty.Register(
        nameof(TotalUnitText), typeof(string), typeof(ModelCard), new PropertyMetadata("TOTAL"));

    public static readonly DependencyProperty MissCountProperty = DependencyProperty.Register(
        nameof(MissCount), typeof(long), typeof(ModelCard), new PropertyMetadata(0L));

    /// <summary>倍率徽章文案（如「×1.5 倍率计」）；空 = 隐藏。</summary>
    public static readonly DependencyProperty MulBadgeTextProperty = DependencyProperty.Register(
        nameof(MulBadgeText), typeof(string), typeof(ModelCard), new PropertyMetadata(""));

    public static readonly DependencyProperty RatioHProperty = DependencyProperty.Register(
        nameof(RatioH), typeof(double), typeof(ModelCard), new PropertyMetadata(0.0));

    public static readonly DependencyProperty RatioMProperty = DependencyProperty.Register(
        nameof(RatioM), typeof(double), typeof(ModelCard), new PropertyMetadata(0.0));

    public static readonly DependencyProperty RatioOProperty = DependencyProperty.Register(
        nameof(RatioO), typeof(double), typeof(ModelCard), new PropertyMetadata(0.0));

    public static readonly DependencyProperty RatioHTextProperty = DependencyProperty.Register(
        nameof(RatioHText), typeof(string), typeof(ModelCard), new PropertyMetadata("0"));

    public static readonly DependencyProperty RatioMTextProperty = DependencyProperty.Register(
        nameof(RatioMText), typeof(string), typeof(ModelCard), new PropertyMetadata("0"));

    public static readonly DependencyProperty RatioOTextProperty = DependencyProperty.Register(
        nameof(RatioOText), typeof(string), typeof(ModelCard), new PropertyMetadata("0"));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(ModelCard), new PropertyMetadata(false));

    /// <summary>true = 该卡口径为 UTC（描边/口径点琥珀）。</summary>
    public static readonly DependencyProperty IsUtcProperty = DependencyProperty.Register(
        nameof(IsUtc), typeof(bool), typeof(ModelCard), new PropertyMetadata(false));

    /// <summary>true = 显示倍率值（×N 徽章出现）。</summary>
    public static readonly DependencyProperty IsMultiplierProperty = DependencyProperty.Register(
        nameof(IsMultiplier), typeof(bool), typeof(ModelCard), new PropertyMetadata(false));

    /// <summary>S3 序号水印（1-based）。</summary>
    public static readonly DependencyProperty SerialTextProperty = DependencyProperty.Register(
        nameof(SerialText), typeof(string), typeof(ModelCard), new PropertyMetadata(""));

    // —— 命令（由 CardViewModel 提供）——
    public static readonly DependencyProperty ToggleCaliberCommandProperty = DependencyProperty.Register(
        nameof(ToggleCaliberCommand), typeof(ICommand), typeof(ModelCard), new PropertyMetadata(null));

    public static readonly DependencyProperty SetActualViewCommandProperty = DependencyProperty.Register(
        nameof(SetActualViewCommand), typeof(ICommand), typeof(ModelCard), new PropertyMetadata(null));

    public static readonly DependencyProperty SetMulViewCommandProperty = DependencyProperty.Register(
        nameof(SetMulViewCommand), typeof(ICommand), typeof(ModelCard), new PropertyMetadata(null));

    /// <summary>口径点显示文案（目标口径，随样例交互）。</summary>
    public static readonly DependencyProperty CaliberDotTextProperty = DependencyProperty.Register(
        nameof(CaliberDotText), typeof(string), typeof(ModelCard), new PropertyMetadata("UTC"));

    public string ProviderText { get => (string)GetValue(ProviderTextProperty); set => SetValue(ProviderTextProperty, value); }
    public string ProviderKind { get => (string)GetValue(ProviderKindProperty); set => SetValue(ProviderKindProperty, value); }
    public string ModelName { get => (string)GetValue(ModelNameProperty); set => SetValue(ModelNameProperty, value); }
    public string RequestCountText { get => (string)GetValue(RequestCountTextProperty); set => SetValue(RequestCountTextProperty, value); }
    public string DeltaText { get => (string)GetValue(DeltaTextProperty); set => SetValue(DeltaTextProperty, value); }
    public string TotalText { get => (string)GetValue(TotalTextProperty); set => SetValue(TotalTextProperty, value); }
    public string TotalUnitText { get => (string)GetValue(TotalUnitTextProperty); set => SetValue(TotalUnitTextProperty, value); }
    public long MissCount { get => (long)GetValue(MissCountProperty); set => SetValue(MissCountProperty, value); }
    public string MulBadgeText { get => (string)GetValue(MulBadgeTextProperty); set => SetValue(MulBadgeTextProperty, value); }
    public double RatioH { get => (double)GetValue(RatioHProperty); set => SetValue(RatioHProperty, value); }
    public double RatioM { get => (double)GetValue(RatioMProperty); set => SetValue(RatioMProperty, value); }
    public double RatioO { get => (double)GetValue(RatioOProperty); set => SetValue(RatioOProperty, value); }
    public string RatioHText { get => (string)GetValue(RatioHTextProperty); set => SetValue(RatioHTextProperty, value); }
    public string RatioMText { get => (string)GetValue(RatioMTextProperty); set => SetValue(RatioMTextProperty, value); }
    public string RatioOText { get => (string)GetValue(RatioOTextProperty); set => SetValue(RatioOTextProperty, value); }
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }
    public bool IsUtc { get => (bool)GetValue(IsUtcProperty); set => SetValue(IsUtcProperty, value); }
    public bool IsMultiplier { get => (bool)GetValue(IsMultiplierProperty); set => SetValue(IsMultiplierProperty, value); }
    public string SerialText { get => (string)GetValue(SerialTextProperty); set => SetValue(SerialTextProperty, value); }
    public string CaliberDotText { get => (string)GetValue(CaliberDotTextProperty); set => SetValue(CaliberDotTextProperty, value); }
    public ICommand? ToggleCaliberCommand { get => (ICommand?)GetValue(ToggleCaliberCommandProperty); set => SetValue(ToggleCaliberCommandProperty, value); }
    public ICommand? SetActualViewCommand { get => (ICommand?)GetValue(SetActualViewCommandProperty); set => SetValue(SetActualViewCommandProperty, value); }
    public ICommand? SetMulViewCommand { get => (ICommand?)GetValue(SetMulViewCommandProperty); set => SetValue(SetMulViewCommandProperty, value); }

    static ModelCard() => DefaultStyleKeyProperty.OverrideMetadata(typeof(ModelCard),
        new FrameworkPropertyMetadata(typeof(ModelCard)));
}
