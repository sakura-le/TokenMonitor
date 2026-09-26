using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TokenMonitor.App.Infrastructure;
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

    public IReadOnlyList<int>? Days
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
/// 模型 + 货币(CNY/USD) + 生效日期；规则卡 = 时段行(0.5h，时间下拉) + 星期 chips + 三单价 + 删除；
/// 版本条「当前 V{n} 生效中 → 保存…」；底部说明（跨午夜平移 + 自动备份）。
/// 打开（或切换模型）时载入该模型链尾版本的规则，保存后再次进入即看到已保存内容。
/// </summary>
public partial class PricingConfigDialog : ShellDialog
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly AppServices _svc;
    public ObservableCollection<string> Models { get; } = new();
    public ObservableCollection<PriceRuleVm> Rules { get; } = new();

    private string _chosenModel = "";
    private string _effectiveFrom = DateTime.Today.ToString("yyyy-MM-dd");
    private string? _versionBar;
    private string _currency = "CNY";
    private bool _loading;

    /// <summary>时段编辑基准（生效日期口径）：local → 编辑区按本地时间，保存换算为 UTC 存储。</summary>
    public string BasisHint => TimeBasis.Hint(BasisMode, BasisOffset);

    private string BasisMode => _svc.Engine.Pricing.EffectiveContext.Mode;
    private int BasisOffset => _svc.Engine.Config.Settings.OffsetMin;

    public string ChosenModel
    {
        get => _chosenModel;
        set { if (Set(ref _chosenModel, value)) { LoadModel(value); RefreshVersionBar(); } }
    }
    public string EffectiveFrom { get => _effectiveFrom; set { if (Set(ref _effectiveFrom, value)) RefreshVersionBar(); } }
    public string? VersionBar { get => _versionBar; set => Set(ref _versionBar, value, nameof(VersionBar)); }

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand<PriceRuleVm> RemoveRuleCommand { get; }
    public RelayCommand<PeriodRowVm> RemovePeriodCommand { get; }

    public PricingConfigDialog(AppServices svc)
    {
        // 命令先于 InitializeComponent 赋值（见 CardVisibilityDialog 同注）
        _svc = svc;
        AddRuleCommand = new RelayCommand(AddRule);
        RemoveRuleCommand = new RelayCommand<PriceRuleVm>(r => { if (r is not null) Rules.Remove(r); });
        RemovePeriodCommand = new RelayCommand<PeriodRowVm>(p =>
        {
            if (p is null) return;
            foreach (var r in Rules) r.Periods.Remove(p);
        });
        InitializeComponent();

        var names = ModelKeyOptions.Build(
            _svc.Engine.Pricing.Document.Pricing.Keys,
            _svc.Main?.Cards.Select(c => c.Key) ?? []);
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

    private void AddRule_Click(object sender, RoutedEventArgs e) => AddRule();

    private void AddRule()
    {
        var r = new PriceRuleVm { Title = $"规则{Rules.Count + 1}" };
        r.Periods.Add(new PeriodRowVm { Start = "09:00", End = "18:00" });
        Rules.Add(r);
    }

    private void Currency_Changed(object sender, SelectionChangedEventArgs e)
    {
        _currency = CurrencyBox.SelectedIndex == 1 ? "USD" : "CNY";
        if (!_loading) RefreshVersionBar();
    }

    /// <summary>载入该模型链尾版本（最近保存 = 当前生效）的货币、生效日期与规则卡。
    /// 该模型尚无规范键配置时回退到同名遗留裸键（旧版剥离前缀写入），使旧配置可见并在保存时改写到规范键。</summary>
    private void LoadModel(string? modelKey)
    {
        Rules.Clear();
        var cfg = ModelKeyOptions.Resolve(modelKey, _svc.Engine.Pricing.Document.Pricing);
        if (cfg is { History.Count: > 0 })
        {
            var last = cfg.History[^1];
            _loading = true;
            try { CurrencyBox.SelectedIndex = string.Equals(last.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? 1 : 0; }
            finally { _loading = false; }
            _currency = last.Currency;
            EffectiveFrom = last.EffectiveFrom ?? "";
            var idx = 0;
            // 存量恒为 UTC；local 口径下换算成本地时钟显示（原前端 tzShift.js 的职责，02-§4.5）
            foreach (var rule in TimeBasis.ToDisplay(last.Rules, BasisMode, BasisOffset))
            {
                var vm = new PriceRuleVm { Title = $"规则{++idx}" };
                vm.Days = rule.Days;
                vm.InputPer1M = Num(rule.InputPer1M);
                vm.CachePer1M = Num(rule.CachePer1M);
                vm.OutputPer1M = Num(rule.OutputPer1M);
                vm.Periods.Add(new PeriodRowVm
                {
                    Start = Fmt.HourLabel(rule.Start),
                    End = Fmt.HourLabel(rule.End),
                });
                Rules.Add(vm);
            }
        }
        if (Rules.Count == 0) AddRule();   // 无历史版本 / 规则为空 → 默认空白规则卡
    }

    private static string Num(double v) => v.ToString("0.####", Inv);

    private void RefreshVersionBar()
    {
        var doc = _svc.Engine.Pricing.Document;
        var (mode, _) = _svc.Engine.Pricing.EffectiveContext;
        var cfg = ModelKeyOptions.Resolve(ChosenModel, doc.Pricing);
        var legacy = cfg is not null && !doc.Pricing.ContainsKey(ChosenModel)
            ? " · 读自遗留键（保存将写入 " + ChosenModel + "）" : "";
        if (string.IsNullOrEmpty(ChosenModel) || cfg is not { History.Count: > 0 })
        {
            VersionBar = $"当前无版本 → 保存将创建 V1 · 基准 {mode.ToUpperInvariant()} · 货币 {_currency}{legacy}";
            return;
        }
        var last = cfg.History[^1];
        var savedEff = last.EffectiveFrom ?? "";
        var curEff = EffectiveFrom.Trim();
        var action = string.Equals(savedEff, curEff, StringComparison.Ordinal)
            ? $"保存覆盖 V{cfg.History.Count}"
            : $"保存追加 V{cfg.History.Count + 1}";
        VersionBar = $"V{cfg.History.Count} 生效中 · {(savedEff.Length == 0 ? "一直生效" : savedEff)}" +
                     (curEff.Length == 0 ? "" : $" · 生效日期 {curEff}") + " → " + action +
                     $" · 基准 {mode.ToUpperInvariant()} · 货币 {_currency}{legacy}";
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
            }
        }
        if (rules.Any(r => r.InputPer1M < 0 || r.CachePer1M < 0 || r.OutputPer1M < 0)) return false;

        // 编辑区（local 口径下为本地时间）→ UTC 存储；跨午夜拆分与星期平移由 PricingShift 负责
        var stored = TimeBasis.ToStorage(rules, BasisMode, BasisOffset);

        try
        {
            _svc.Engine.Pricing.UpdatePricingVersion(ChosenModel, eff, _currency, stored);
            Core.SysUtil.Logger.Info("App", $"pricing saved: {ChosenModel} eff={eff ?? "-"} currency={_currency} rules={stored.Count} basis={BasisMode}");
            _svc.Tray?.ShowBalloon("计价配置", $"{ChosenModel} 已保存并生效");
            // 键没有对应卡片 → 保存不会体现为界面上的金额变化，明确告知（避免"保存了但不生效"的错觉）；
            // 必须指定 owner，否则置顶对话框会把这个 MessageBox 压在后面（同 CardVisibilityDialog 教训）
            var warn = ModelKeyOptions.NoCardWarning(ChosenModel, _svc.Main?.Cards.Select(c => c.Key) ?? []);
            if (warn is not null) MessageBox.Show(this, warn, "计价配置", MessageBoxButton.OK, MessageBoxImage.Warning);
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
