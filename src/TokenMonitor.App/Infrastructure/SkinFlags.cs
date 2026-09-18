using System.ComponentModel;

namespace TokenMonitor.App.Infrastructure;

/// <summary>
/// 皮肤形态标记（跨皮肤的结构性差异开关）。
/// 皮肤字典只放 Brush/Color/CornerRadius/Thickness/Effect/FontFamily（03-ui-spec §2.1 约定），
/// "选中态用角标记号还是顶杠""是否有雷达扫描"这类结构性差异无法用画刷表达，
/// 故由 ThemeService 在切换皮肤时同步本单例；模板经绑定消费，不缓存任何 Brush。
/// </summary>
public sealed class SkinFlags : INotifyPropertyChanged
{
    public static SkinFlags Instance { get; } = new();

    private string _skinKey = "GraphiteTerminal";

    /// <summary>当前皮肤键：EditorialInk / GraphiteTerminal / SwissGrid / SkyHud。</summary>
    public string SkinKey
    {
        get => _skinKey;
        private set
        {
            if (_skinKey == value) return;
            _skinKey = value;
            OnPropertyChanged(nameof(SkinKey));
            OnPropertyChanged(nameof(IsEditorialInk));
            OnPropertyChanged(nameof(IsGraphiteTerminal));
            OnPropertyChanged(nameof(IsSwissGrid));
            OnPropertyChanged(nameof(IsSkyHud));
            // 结构差异派生
            OnPropertyChanged(nameof(CornerMarks));      // S2/S4 选中四角记号
            OnPropertyChanged(nameof(TopBarSelection));   // S3 顶缘红杠 + 序号水印
            OnPropertyChanged(nameof(HardShadowSelection));// S1 偏移硬影
            OnPropertyChanged(nameof(SerialWatermark));   // S3 巨型序号水印
            OnPropertyChanged(nameof(IsRadarSkin));       // S4 雷达
            OnPropertyChanged(nameof(IsTerminalSkin));    // S2 光标闪烁/刻纹
            OnPropertyChanged(nameof(IsSkySkin));         // S4 云朵/斜纹/胶囊
            OnPropertyChanged(nameof(IsLight));           // 浅色肤
            OnPropertyChanged(nameof(SurgeRadar));        // 激增态雷达加速
            OnPropertyChanged(nameof(SurgeBlink));        // 激增态光标加速
            OnPropertyChanged(nameof(SurgePulse));        // 激增态增量脉冲
        }
    }

    /// <summary>应用皮肤键（ThemeService 调用；不触发皮肤字典变更，只同步结构标记）。</summary>
    public void SetSkin(string key) => SkinKey = key;

    public bool IsEditorialInk => SkinKey == "EditorialInk";
    public bool IsGraphiteTerminal => SkinKey == "GraphiteTerminal";
    public bool IsSwissGrid => SkinKey == "SwissGrid";
    public bool IsSkyHud => SkinKey == "SkyHud";

    /// <summary>S2/S4：选中卡四角 L 形记号 / 瞄准框。</summary>
    public bool CornerMarks => IsGraphiteTerminal || IsSkyHud;
    /// <summary>S3：选中卡顶缘 3px 红杠。</summary>
    public bool TopBarSelection => IsSwissGrid;
    /// <summary>S1：选中卡偏移硬影 + 墨描边。</summary>
    public bool HardShadowSelection => IsEditorialInk;
    /// <summary>S3：巨型序号水印。</summary>
    public bool SerialWatermark => IsSwissGrid;
    /// <summary>S4：雷达扫描环（环形图 + 悬浮球）。</summary>
    public bool IsRadarSkin => IsSkyHud;
    /// <summary>S2：终端刻纹 / 光标闪烁。</summary>
    public bool IsTerminalSkin => IsGraphiteTerminal;
    /// <summary>S4：云朵 / 斜纹警戒条。</summary>
    public bool IsSkySkin => IsSkyHud;
    /// <summary>浅色肤（S1/S3）——窗口描边加强。</summary>
    public bool IsLight => IsEditorialInk || IsSwissGrid;

    // —— 流量激增 → 环境动效加速（03-ui-spec §5 映射，克制红线）——
    private bool _surge;
    /// <summary>激增态开关（滞回判定由 SurgeDetector 负责）。</summary>
    public bool Surge
    {
        get => _surge;
        set
        {
            if (_surge == value) return;
            _surge = value;
            OnPropertyChanged(nameof(Surge));
            OnPropertyChanged(nameof(SurgeRadar));
            OnPropertyChanged(nameof(SurgeBlink));
            OnPropertyChanged(nameof(SurgePulse));
        }
    }
    /// <summary>S4：雷达 6s→2.5s。</summary>
    public bool SurgeRadar => _surge && IsSkyHud;
    /// <summary>S2：光标 1.1s→0.45s。</summary>
    public bool SurgeBlink => _surge && IsGraphiteTerminal;
    /// <summary>S1/S3：增量数字一次性脉冲（≤1.2Hz 由触发方控制）。</summary>
    public bool SurgePulse => _surge && (IsEditorialInk || IsSwissGrid);

    /// <summary>激增状态变更（供需要重建动画的控件订阅，如雷达转速）。</summary>
    public event EventHandler? SurgeChanged;
    public void NotifySurgeChanged() => SurgeChanged?.Invoke(this, EventArgs.Empty);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
