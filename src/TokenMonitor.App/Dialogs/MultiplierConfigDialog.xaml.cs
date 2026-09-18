using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.Services;
using TokenMonitor.Core.Pricing;

namespace TokenMonitor.App.Dialogs;

public partial class PeriodRowVm : INotifyPropertyChanged
{
    private string _start = "09:00";
    private string _end = "12:30";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, string n) { if (!Equals(f, v)) { f = v; PropertyChanged?.Invoke(this, new(n)); } }

    public string Start { get => _start; set => Set(ref _start, value, nameof(Start)); }
    public string End { get => _end; set => Set(ref _end, value, nameof(End)); }

    public double? StartHour => Fmt.ParseHour(Start);
    public double? EndHour => Fmt.ParseHour(End);
}

/// <summary>
/// 对话框 1：倍率配置（01-§3-D8）。
/// 模型选择 / 时段行（0.5h 粒度，可增删）/ 倍率 / 生效日期 / 当前→下一版本展示条 /
/// Local 口径编辑时展示 UTC↔Local 换算提示。保存 → IPricingEngine.UpdateMultiplierVersion。
/// </summary>
public partial class MultiplierConfigDialog : ShellDialog
{
    private readonly AppServices _svc;
    public ObservableCollection<PeriodRowVm> Periods { get; } = new();
    public ObservableCollection<string> Models { get; } = new();

    private string _chosenModel = "";
    private string _effectiveFrom = DateTime.Today.ToString("yyyy-MM-dd");
    private string _rate = "1.0";
    private string? _versionBar;
    private string? _localHint;

    public string ChosenModel { get => _chosenModel; set { if (Set(ref _chosenModel, value)) RefreshVersionBar(); } }
    public string EffectiveFrom
    {
        get => _effectiveFrom;
        set { if (Set(ref _effectiveFrom, value)) { RefreshVersionBar(); RefreshLocalHint(); } }
    }
    public string Rate { get => _rate; set => Set(ref _rate, value, nameof(Rate)); }
    public string? VersionBar { get => _versionBar; set => Set(ref _versionBar, value, nameof(VersionBar)); }
    public string? LocalHint { get => _localHint; set => Set(ref _localHint, value, nameof(LocalHint)); }

    public RelayCommand AddPeriodCommand { get; }
    public RelayCommand<PeriodRowVm> RemovePeriodCommand { get; }
    public RelayCommand TomorrowCommand { get; }

    public MultiplierConfigDialog(AppServices svc)
    {
        _svc = svc;
        AddPeriodCommand = new RelayCommand(() => Periods.Add(new PeriodRowVm { Start = "00:00", End = "08:00" }));
        RemovePeriodCommand = new RelayCommand<PeriodRowVm>(p => { if (p is not null) Periods.Remove(p); });
        TomorrowCommand = new RelayCommand(() =>
            EffectiveFrom = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"));

        // 模型候选：pricing.json 键 + 出现过的模型（不含 provider 前缀）
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _svc.Engine.Pricing.Document.Multipliers.Keys) names.Add(k);
        if (_svc.Main is not null)
            foreach (var c in _svc.Main.Cards) names.Add(c.Model);
        foreach (var n in names) Models.Add(n);

        // 默认时段：工作日日间（样例）
        Periods.Add(new PeriodRowVm { Start = "09:00", End = "12:30" });
        RefreshVersionBar();
        RefreshLocalHint();
        OnConfirm = Save;
    }

    private void RefreshVersionBar()
    {
        var doc = _svc.Engine.Pricing.Document;
        if (string.IsNullOrEmpty(ChosenModel) || !doc.Multipliers.TryGetValue(ChosenModel, out var cfg) || cfg.History.Count == 0)
        {
            VersionBar = "当前无版本 → 保存将创建 V1";
            return;
        }
        var last = cfg.History[^1];
        VersionBar = $"当前 V{cfg.History.Count} 生效中 · {last.EffectiveFrom ?? "一直生效"} → 保存追加 V{cfg.History.Count + 1}" +
                     (string.IsNullOrWhiteSpace(EffectiveFrom) ? "" : $"（{EffectiveFrom} 起）");
    }

    private void RefreshLocalHint()
    {
        var settings = _svc.Engine.Config.Settings;
        if (settings.EffectiveDateMode != "local")
        {
            LocalHint = null;
            return;
        }
        LocalHint = $"生效日期基准 LOCAL（UTC{Fmt.OffsetLabel(settings.OffsetMin)[3..]}）：生效日期/时段按本地日历解释；跨午夜时段自动平移（含星期平移）。";
    }

    /// <summary>保存（校验 + UpdateMultiplierVersion）。</summary>
    private bool Save()
    {
        if (string.IsNullOrWhiteSpace(ChosenModel))
            return Fail("请选择模型");
        var rate = ParseRate();
        if (rate is null) return Fail("倍率必须为正数字（如 1.5）");

        var periods = new List<MultiplierPeriod>();
        if (Periods.Count == 0) periods.Add(new MultiplierPeriod(0, 24, rate.Value));
        foreach (var p in Periods)
        {
            var s = p.StartHour;
            var e = p.EndHour;
            if (s is null || e is null) return Fail($"时段 {p.Start}–{p.End} 无效（HH:mm，0.5h 粒度）");
            if (e.Value <= s.Value) return Fail($"时段 {p.Start}–{p.End} 结束必须晚于开始");
            periods.Add(new MultiplierPeriod(s.Value, e.Value, rate.Value));
        }

        string? eff = string.IsNullOrWhiteSpace(EffectiveFrom) ? null : EffectiveFrom.Trim();
        if (eff is not null && !DateOnly.TryParse(eff, CultureInfo.InvariantCulture, out _))
            return Fail("生效日期格式应为 yyyy-MM-dd");

        try
        {
            _svc.Engine.Pricing.UpdateMultiplierVersion(ChosenModel, eff, periods);
            Core.SysUtil.Logger.Info("App", $"multiplier saved: {ChosenModel} eff={eff ?? "-"} periods={periods.Count} rate={rate}");
            _svc.Tray?.ShowBalloon("倍率配置", $"{ChosenModel} 已保存并生效");
            return true;
        }
        catch (Exception ex)
        {
            return Fail("保存失败：" + ex.Message);
        }
    }

    private double? ParseRate()
    {
        if (double.TryParse(Rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0) return v;
        if (double.TryParse(Rate, NumberStyles.Float, new CultureInfo("zh-CN"), out v) && v > 0) return v;
        return null;
    }

    private bool Fail(string msg)
    {
        MessageBox.Show(msg, "倍率配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
