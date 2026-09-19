using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TokenMonitor.App.Services;
using TokenMonitor.Core.Pricing;

namespace TokenMonitor.App.Dialogs;

/// <summary>计价规则卡 VM（时段行 + 星期 chips + 三单价）。</summary>
public partial class PriceRuleVm : System.ComponentModel.INotifyPropertyChanged
{
    public string Title { get; set; } = "规则";
    public ObservableCollection<PeriodRowVm> Periods { get; } = new();

    private bool _d1 = true, _d2 = true, _d3 = true, _d4 = true, _d5 = true, _d6, _d7;
    private string _input = "2.5000", _cache = "0.2500", _output = "8.5000";

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, string n) { if (!Equals(f, v)) { f = v; PropertyChanged?.Invoke(this, new(n)); } }

    public bool D1 { get => _d1; set => Set(ref _d1, value, nameof(D1)); }
    public bool D2 { get => _d2; set => Set(ref _d2, value, nameof(D2)); }
    public bool D3 { get => _d3; set => Set(ref _d3, value, nameof(D3)); }
    public bool D4 { get => _d4; set => Set(ref _d4, value, nameof(D4)); }
    public bool D5 { get => _d5; set => Set(ref _d5, value, nameof(D5)); }
    public bool D6 { get => _d6; set => Set(ref _d6, value, nameof(D6)); }
    public bool D7 { get => _d7; set => Set(ref _d7, value, nameof(D7)); }
    public string InputPer1M { get => _input; set => Set(ref _input, value, nameof(InputPer1M)); }
    public string CachePer1M { get => _cache; set => Set(ref _cache, value, nameof(CachePer1M)); }
    public string OutputPer1M { get => _output; set => Set(ref _output, value, nameof(OutputPer1M)); }

    public List<int>? Days
    {
        get
        {
            var l = new List<int>();
            if (D1) l.Add(1); if (D2) l.Add(2); if (D3) l.Add(3); if (D4) l.Add(4);
            if (D5) l.Add(5); if (D6) l.Add(6); if (D7) l.Add(7);
            return l.Count is 0 or 7 ? null : l;   // 全选或缺省 = 每天
        }
        set
        {
            if (value is null) { D1 = D2 = D3 = D4 = D5 = D6 = D7 = true; return; }
            D1 = value.Contains(1); D2 = value.Contains(2); D3 = value.Contains(3); D4 = value.Contains(4);
            D5 = value.Contains(5); D6 = value.Contains(6); D7 = value.Contains(7);
        }
    }
}

/// <summary>
/// 对话框 2：计价配置（01-§3-D8）。
/// 模型 + 货币(CNY/USD) + 生效日期；规则卡 = 时段行(0.5h) + 星期 chips + 三单价 + 删除；
/// 版本条「当前 V{n} 生效中 → 保存追加 V{n+1}」；底部说明（跨午夜平移 + 自动备份）。
/// </summary>
public partial class PricingConfigDialog : ShellDialog
{
    private readonly AppServices _svc;
    public ObservableCollection<string> Models { get; } = new();
    public ObservableCollection<PriceRuleVm> Rules { get; } = new();

    private string _chosenModel = "";
    private string _effectiveFrom = DateTime.Today.ToString("yyyy-MM-dd");
    private string? _versionBar;
    private string _currency = "CNY";

    public string ChosenModel { get => _chosenModel; set { if (Set(ref _chosenModel, value)) RefreshVersionBar(); } }
    public string EffectiveFrom { get => _effectiveFrom; set { if (Set(ref _effectiveFrom, value, nameof(EffectiveFrom))) RefreshVersionBar(); } }
    public string? VersionBar { get => _versionBar; set => Set(ref _versionBar, value, nameof(VersionBar)); }

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand<PriceRuleVm> RemoveRuleCommand { get; }
    public RelayCommand<PeriodRowVm> RemovePeriodCommand { get; }

    public PricingConfigDialog(AppServices svc)
    {
        InitializeComponent();
        _svc = svc;
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand<PriceRuleVm>(r => { if (r is not null) Rules.Remove(r); });
        RemovePeriodCommand = new RelayCommand<PeriodRowVm>(p =>
        {
            if (p is null) return;
            foreach (var r in Rules) r.Periods.Remove(p);
        });

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in _svc.Engine.Pricing.Document.Pricing.Keys) names.Add(k);
        if (_svc.Main is not null)
            foreach (var c in _svc.Main.Cards) names.Add(c.Model);
        foreach (var n in names) Models.Add(n);

        CurrencyBox.Items.Add("CNY ¥");
        CurrencyBox.Items.Add("USD $");
        CurrencyBox.SelectedIndex = 0;

        AddRule();
        RefreshVersionBar();
        OnConfirm = Save;
    }

    public string? ChosenModelTag { get => ChosenModel; }

    public string? ChosenModelInit
    {
        set => ChosenModel = value ?? "";
    }

    private void AddRule()
    {
        var r = new PriceRuleVm { Title = $"规则{Rules.Count + 1}" };
        r.Periods.Add(new PeriodRowVm { Start = "09:00", End = "18:00" });
        Rules.Add(r);
    }

    private void Currency_Changed(object sender, SelectionChangedEventArgs e)
    {
        _currency = CurrencyBox.SelectedIndex == 1 ? "USD" : "CNY";
    }

    private void RefreshVersionBar()
    {
        var doc = _svc.Engine.Pricing.Document;
        var (mode, _) = _svc.Engine.Pricing.EffectiveContext;
        if (string.IsNullOrEmpty(ChosenModel) || !doc.Pricing.TryGetValue(ChosenModel, out var cfg) || cfg.History.Count == 0)
        {
            VersionBar = $"当前无版本 → 保存将创建 V1 · 基准 {mode.ToUpperInvariant()}";
            return;
        }
        var last = cfg.History[^1];
        VersionBar = $"V{cfg.History.Count} 生效中 · {last.EffectiveFrom ?? "一直生效"} → 保存追加 V{cfg.History.Count + 1}" +
                     $" · 基准 {mode.ToUpperInvariant()} · 货币 {_currency}";
    }

    private bool Save()
    {
        if (string.IsNullOrWhiteSpace(ChosenModel)) return Fail("请选择模型");
        if (Rules.Count == 0) return Fail("至少需要一条计价规则");

        string? eff = string.IsNullOrWhiteSpace(EffectiveFrom) ? null : EffectiveFrom.Trim();
        if (eff is not null && !DateOnly.TryParse(eff, CultureInfo.InvariantCulture, out _))
            return Fail("生效日期格式应为 yyyy-MM-dd");

        var rules = new List<PriceRule>();
        foreach (var r in Rules)
        {
            if (r.Periods.Count == 0) return Fail($"「{r.Title}」至少需要一条时段行");
            double? prevEnd = null;
            foreach (var p in r.Periods)
            {
                var s = p.StartHour;
                var e = p.EndHour;
                if (s is null || e is null) return Fail($"「{r.Title}」时段 {p.Start}–{p.End} 无效（HH:mm，0.5h 粒度）");
                if (e.Value <= s.Value) return Fail($"「{r.Title}」时段 {p.Start}–{p.End} 结束必须晚于开始");
                rules.Add(new PriceRule(s.Value, e.Value, r.Days,
                    Parse(r.InputPer1M, $"{r.Title} input_per_1m") ?? -1,
                    Parse(r.CachePer1M, $"{r.Title} cache_per_1m") ?? -1,
                    Parse(r.OutputPer1M, $"{r.Title} output_per_1m") ?? -1));
                prevEnd = e;
            }
        }
        if (rules.Any(r => r.InputPer1M < 0 || r.CachePer1M < 0 || r.OutputPer1M < 0)) return false;

        try
        {
            _svc.Engine.Pricing.UpdatePricingVersion(ChosenModel, eff, _currency, rules);
            Core.SysUtil.Logger.Info("App", $"pricing saved: {ChosenModel} eff={eff ?? "-"} currency={_currency} rules={rules.Count}");
            _svc.Tray?.ShowBalloon("计价配置", $"{ChosenModel} 已保存并生效");
            return true;
        }
        catch (Exception ex)
        {
            return Fail("保存失败：" + ex.Message);
        }
    }

    private static double? Parse(string text, string label)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
        {
            MessageBox.Show($"单价 {label} 必须为非负数字", "计价配置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return v;
    }

    private bool Fail(string msg)
    {
        MessageBox.Show(msg, "计价配置", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
