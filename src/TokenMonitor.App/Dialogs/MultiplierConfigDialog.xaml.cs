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

/// <summary>时段行 VM（起止时间；计价配置的规则卡用）。</summary>
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

/// <summary>倍率时段面板 VM：一个时段 panel = 起止时间 + 该时段的倍率（倍率按时段分别设置）。</summary>
public partial class PeriodPanelVm : INotifyPropertyChanged
{
    private string _start = "00:00";
    private string _end = "08:00";
    private string _rate = "1.0";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, string n) { if (!Equals(f, v)) { f = v; PropertyChanged?.Invoke(this, new(n)); } }

    public string Start { get => _start; set => Set(ref _start, value, nameof(Start)); }
    public string End { get => _end; set => Set(ref _end, value, nameof(End)); }
    public string Rate { get => _rate; set => Set(ref _rate, value, nameof(Rate)); }

    public double? StartHour => Fmt.ParseHour(Start);
    public double? EndHour => Fmt.ParseHour(End);
}

/// <summary>
/// 对话框 1：倍率配置（01-§3-D8）。
/// 模型选择 / 时段 panel 列表（每个时段 = 起止时间下拉 + 倍率，可增删）/ 生效日期 /
/// 当前→下一版本展示条 / Local 口径编辑时展示 UTC↔Local 换算提示。
/// 保存 → IPricingEngine.UpdateMultiplierVersion。
/// 打开（或切换模型）时载入该模型当前生效版本的时段与倍率。
/// </summary>
public partial class MultiplierConfigDialog : ShellDialog
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly AppServices _svc;
    public ObservableCollection<PeriodPanelVm> Periods { get; } = new();
    public ObservableCollection<string> Models { get; } = new();

    private string _chosenModel = "";
    private string _effectiveFrom = DateTime.Today.ToString("yyyy-MM-dd");
    private string? _versionBar;
    private string? _localHint;

    public string ChosenModel
    {
        get => _chosenModel;
        set { if (Set(ref _chosenModel, value)) { LoadModel(value); RefreshVersionBar(); } }
    }
    public string EffectiveFrom
    {
        get => _effectiveFrom;
        set { if (Set(ref _effectiveFrom, value)) { RefreshVersionBar(); RefreshLocalHint(); } }
    }
    public string? VersionBar { get => _versionBar; set => Set(ref _versionBar, value, nameof(VersionBar)); }
    public string? LocalHint { get => _localHint; set => Set(ref _localHint, value, nameof(LocalHint)); }

    public RelayCommand AddPeriodCommand { get; }
    public RelayCommand<PeriodPanelVm> RemovePeriodCommand { get; }
    public RelayCommand TomorrowCommand { get; }

    public MultiplierConfigDialog(AppServices svc)
    {
        // 命令先于 InitializeComponent 赋值（见 CardVisibilityDialog 同注）
        _svc = svc;
        AddPeriodCommand = new RelayCommand(AddPeriod);
        RemovePeriodCommand = new RelayCommand<PeriodPanelVm>(p => { if (p is not null) Periods.Remove(p); });
        TomorrowCommand = new RelayCommand(() =>
            EffectiveFrom = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"));
        InitializeComponent();

        // 模型候选：pricing.json 键 + 卡片键（规范键为 provider/model 完整键）；
        // 被同名规范键遮蔽的遗留裸键不列出（见 ModelKeyOptions）
        var names = ModelKeyOptions.Build(
            _svc.Engine.Pricing.Document.Multipliers.Keys,
            _svc.Main?.Cards.Select(c => c.Key) ?? []);
        foreach (var n in names) Models.Add(n);

        // 未指定模型时的示例时段（工作日日间）
        AddPeriod();
        RefreshVersionBar();
        RefreshLocalHint();
        OnConfirm = Save;
    }

    private void AddPeriod() => Periods.Add(new PeriodPanelVm { Start = "00:00", End = "08:00", Rate = "1.0" });

    /// <summary>载入该模型链尾版本的时段与倍率（链尾 = 最近保存 = 当前生效）。
    /// 该模型尚无规范键配置时回退到同名遗留裸键（旧版剥离前缀写入），使旧配置可见并在保存时改写到规范键。</summary>
    private void LoadModel(string? modelKey)
    {
        Periods.Clear();
        if (string.IsNullOrWhiteSpace(modelKey)) { AddPeriod(); return; }
        var cfg = ModelKeyOptions.Resolve(modelKey, _svc.Engine.Pricing.Document.Multipliers);
        if (cfg is not { History.Count: > 0 })
        {
            AddPeriod();
            return;
        }
        var last = cfg.History[^1];
        // 存量恒为 UTC；local 口径下换算成本地时钟显示（原前端 tzShift.js 的职责，02-§4.5）
        foreach (var p in TimeBasis.ToDisplay(last.Periods, _svc.Engine.Pricing.EffectiveContext.Mode,
                                               _svc.Engine.Config.Settings.OffsetMin))
            Periods.Add(new PeriodPanelVm
            {
                Start = Fmt.HourLabel(p.Start),
                End = Fmt.HourLabel(p.End),
                Rate = p.Rate.ToString("0.####", Inv),
            });
        if (Periods.Count == 0) AddPeriod();
        // 生效日期回填：与已保存版本同日期保存 = 覆盖该版本（Upsert 按 EffectiveFrom 去重）
        EffectiveFrom = last.EffectiveFrom ?? "";
    }

    private void RefreshVersionBar()
    {
        var doc = _svc.Engine.Pricing.Document;
        var cfg = ModelKeyOptions.Resolve(ChosenModel, doc.Multipliers);
        var legacy = cfg is not null && !doc.Multipliers.ContainsKey(ChosenModel)
            ? " · 读自遗留键（保存将写入 " + ChosenModel + "）" : "";
        if (string.IsNullOrEmpty(ChosenModel) || cfg is not { History.Count: > 0 })
        {
            VersionBar = $"当前无版本 → 保存将创建 V1{legacy}";
            return;
        }
        var last = cfg.History[^1];
        var savedEff = last.EffectiveFrom ?? "";
        var curEff = EffectiveFrom.Trim();
        var action = string.Equals(savedEff, curEff, StringComparison.Ordinal)
            ? $"保存覆盖 V{cfg.History.Count}"
            : $"保存追加 V{cfg.History.Count + 1}";
        VersionBar = $"当前 V{cfg.History.Count} 生效中 · {(savedEff.Length == 0 ? "一直生效" : savedEff)}" +
                     (curEff.Length == 0 ? "" : $" · 生效日期 {curEff}") + " → " + action + legacy;
    }

    private void RefreshLocalHint()
    {
        var settings = _svc.Engine.Config.Settings;
        // 两种基准都给出提示：UTC 基准下也要让用户知道"我现在填的是 UTC 时段"
        LocalHint = TimeBasis.Hint(settings.EffectiveDateMode, settings.OffsetMin);
    }

    /// <summary>保存（校验 + UpdateMultiplierVersion）。</summary>
    private bool Save()
    {
        if (string.IsNullOrWhiteSpace(ChosenModel))
            return Fail("请选择模型");

        var periods = new List<MultiplierPeriod>();
        if (Periods.Count == 0) periods.Add(new MultiplierPeriod(0, 24, 1.0));   // 无时段 = 全天 ×1（等同不设倍率）
        foreach (var p in Periods)
        {
            var s = p.StartHour;
            var e = p.EndHour;
            if (s is null || e is null) return Fail($"时段 {p.Start}–{p.End} 无效（HH:mm，0.5h 粒度）");
            if (e.Value <= s.Value) return Fail($"时段 {p.Start}–{p.End} 结束必须晚于开始");
            var rate = ParseRate(p.Rate);
            if (rate is null) return Fail($"时段 {p.Start}–{p.End} 的倍率必须为正数字（如 1.5）");
            periods.Add(new MultiplierPeriod(s.Value, e.Value, rate.Value));
        }

        string? eff = string.IsNullOrWhiteSpace(EffectiveFrom) ? null : EffectiveFrom.Trim();
        if (eff is not null && !DateOnly.TryParse(eff, CultureInfo.InvariantCulture, out _))
            return Fail("生效日期格式应为 yyyy-MM-dd");

        try
        {
            // 编辑区（local 口径下为本地时间）→ UTC 存储；跨午夜拆分由 PricingShift 负责
            var stored = TimeBasis.ToStorage(periods, _svc.Engine.Pricing.EffectiveContext.Mode,
                                             _svc.Engine.Config.Settings.OffsetMin);
            _svc.Engine.Pricing.UpdateMultiplierVersion(ChosenModel, eff, stored);
            Core.SysUtil.Logger.Info("App", $"multiplier saved: {ChosenModel} eff={eff ?? "-"} periods={stored.Count}");
            _svc.Tray?.ShowBalloon("倍率配置", $"{ChosenModel} 已保存并生效");
            // 键没有对应卡片 → 保存不会体现为界面上的数值变化，明确告知（避免"保存了但不生效"的错觉）；
            // 必须指定 owner，否则置顶对话框会把这个 MessageBox 压在后面（同 CardVisibilityDialog 教训）
            var warn = ModelKeyOptions.NoCardWarning(ChosenModel, _svc.Main?.Cards.Select(c => c.Key) ?? []);
            if (warn is not null) MessageBox.Show(this, warn, "倍率配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }
        catch (Exception ex)
        {
            return Fail("保存失败：" + ex.Message);
        }
    }

    private static double? ParseRate(string? text)
    {
        if (double.TryParse(text, NumberStyles.Float, Inv, out var v) && v > 0) return v;
        if (double.TryParse(text, NumberStyles.Float, new CultureInfo("zh-CN"), out v) && v > 0) return v;
        return null;
    }

    private bool Fail(string msg)
    {
        MessageBox.Show(msg, "倍率配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
