using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.Core.Stats;

namespace TokenMonitor.App.ViewModels;

/// <summary>卡片数据范围（D6；模型名右键 数据范围 子菜单）。</summary>
public enum CardRange
{
    Today, Week, Days7, Month, LastMonth, Quarter, Year, Custom
}

/// <summary>
/// 单模型卡 VM。数据源 = StatsSnapshot（200ms 实时，仅 Today 范围）或
/// IStatsQueryService 范围查询（其余预设/自定义，异步回包校验发起键 [C16]）。
/// 口径（utc/local）与倍率视图（实际/倍率）双二态；变更即持久化 ui_state。
/// </summary>
public partial class CardViewModel : ObservableObject
{
    private readonly Services.AppServices _svc;

    public CardViewModel(Services.AppServices svc, string provider, string model)
    {
        _svc = svc;
        Provider = provider;
        Model = model;
        Key = string.IsNullOrEmpty(provider) ? model : provider + "/" + model;
        ToggleCaliberCommand = new RelayCommand(ToggleCaliber);
        SetActualViewCommand = new RelayCommand(() => SetMultiplierView(false));
        SetMulViewCommand = new RelayCommand(() => SetMultiplierView(true));
        RefreshCommand = new RelayCommand(() => _svc.Main?.RefreshCard(this));
        SelectRangeCommand = new RelayCommand<string?>(s =>
        {
            if (s is null) return;
            if (s == "Custom") { _svc.Dialogs.ShowTimeRange(this); return; }
            if (Enum.TryParse<CardRange>(s, out var r)) SetRange(r);
        });
        ToggleVisibilityCommand = new RelayCommand(() => _svc.Main?.SetHidden(this, !IsHidden));
        // 卡片「设置」下拉：以本卡为作用域转发（取代原三类右键菜单）。
        // 全部经 Dispatcher 延迟：菜单项 Click 内同步打开模态对话框会让菜单关闭流程被模态循环阻塞，
        // 造成对话框内部控件交互异常（"点添加规则卡无反应"类问题的根因）。
        // 菜单项点击 → 延迟到下一批 Background 操作再开模态（规范写法：
        // 直接同步 ShowDialog 会在菜单单击处理栈内压入模态帧，实测 ShowDialog 永不返回）
        OpenMultiplierCommand = new RelayCommand(() => Defer(() => _svc.Dialogs.ShowMultiplierConfig(Key)));
        OpenPricingCommand = new RelayCommand(() => Defer(() => _svc.Dialogs.ShowPricingConfig(Key)));
        OpenCalibrateCommand = new RelayCommand(() => Defer(() => _svc.Dialogs.ShowManualCalibrate(Key)));
        OpenOpLogsCommand = new RelayCommand(() => Defer(() => _svc.Dialogs.ShowOpLogs(Key)));
        OpenExportCommand = new RelayCommand(() => Defer(() => _svc.Dialogs.ShowExport()));
        OpenConfigCommand = new RelayCommand(() => _svc.Main?.OpenConfigFileCommand.Execute(null));
        OpenVisibilityCommand = new RelayCommand(() => _svc.Main?.OpenCardVisibilityCommand.Execute(null));
        ResetTodayCommand = new RelayCommand(() => _svc.Main?.ResetTodayCommand.Execute(Key));
    }

    private static void Defer(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null) { action(); return; }
        d.BeginInvoke(action, System.Windows.Threading.DispatcherPriority.Background);
    }

    public string Provider { get; }
    public string Model { get; }
    public string Key { get; }

    // —— 状态 ——
    /// <summary>true=UTC 口径；false=Local（默认）。</summary>
    public bool IsUtc { get => _isUtc; set { if (SetProperty(ref _isUtc, value)) { OnDisplayModeChanged(); } } }
    private bool _isUtc;

    /// <summary>true=显示倍率值。</summary>
    public bool MultiplierView { get => _multiplierView; set { if (SetProperty(ref _multiplierView, value)) { OnDisplayModeChanged(); } } }
    private bool _multiplierView;

    /// <summary>隐藏（显示隐藏卡片对话框）。</summary>
    public bool IsHidden { get => _isHidden; set => SetProperty(ref _isHidden, value); }
    private bool _isHidden;

    public CardRange Range { get => _range; set { if (SetProperty(ref _range, value)) OnDisplayModeChanged(); } }
    private CardRange _range = CardRange.Today;
    public DateTime CustomStart { get; set; } = DateTime.Today.AddDays(-6);
    public DateTime CustomEnd { get; set; } = DateTime.Today;

    [ObservableProperty] private string _rangeTag = "本日";

    // —— 展示 ——
    [ObservableProperty] private string _providerTag = "";
    [ObservableProperty] private string _providerKind = "Default";
    [ObservableProperty] private string _requestCountText = "0";
    [ObservableProperty] private string _deltaText = "";
    [ObservableProperty] private string _totalText = "0";
    [ObservableProperty] private string _totalUnitText = "TOTAL";
    [ObservableProperty] private long _missCount;
    [ObservableProperty] private string _mulBadgeText = "";
    [ObservableProperty] private double _ratioH;
    [ObservableProperty] private double _ratioM;
    [ObservableProperty] private double _ratioO;
    [ObservableProperty] private string _ratioHText = "0";
    [ObservableProperty] private string _ratioMText = "0";
    [ObservableProperty] private string _ratioOText = "0";
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _serialText = "";
    [ObservableProperty] private string _caliberDotText = "UTC";

    /// <summary>口径 UTC 徽章色（供侧栏 tag 复用）。</summary>
    public string ScopeText => IsUtc ? "UTC" : "LOCAL" + Fmt.OffsetLabel(_svc.Engine.Config.Settings.OffsetMin).Replace("UTC", "+");

    [ObservableProperty] private bool _isStale;   // 范围查询中

    /// <summary>口径点文案：显示目标口径（与样例交互一致）。</summary>
    public string DotText => IsUtc ? "LOCAL" : "UTC";

    public RelayCommand ToggleCaliberCommand { get; }
    public RelayCommand SetActualViewCommand { get; }
    public RelayCommand SetMulViewCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand<string?> SelectRangeCommand { get; }
    public RelayCommand ToggleVisibilityCommand { get; }
    public RelayCommand OpenMultiplierCommand { get; }
    public RelayCommand OpenPricingCommand { get; }
    public RelayCommand OpenCalibrateCommand { get; }
    public RelayCommand OpenOpLogsCommand { get; }
    public RelayCommand OpenExportCommand { get; }
    public RelayCommand OpenConfigCommand { get; }
    public RelayCommand OpenVisibilityCommand { get; }
    public RelayCommand ResetTodayCommand { get; }

    // —— 快照（Today 实时） ——
    private ModelSnapshot? _utcSnap;
    private ModelSnapshot? _localSnap;

    // —— 范围查询防串 [C16] ——
    private int _queryGeneration;
    private string _pendingQueryKey = "";
    private IReadOnlyList<TokenMonitor.Core.Storage.ModelDailyAggregate>? _rangeRows;

    public void ApplySnapshot(ModelSnapshot utc, ModelSnapshot local, long missedTotal)
    {
        _utcSnap = utc;
        _localSnap = local;
        if (Range == CardRange.Today) Render();
        var missed = (IsUtc ? utc.MissedCount : local.MissedCount);
        MissCount = missed;
    }

    private void OnDisplayModeChanged()
    {
        OnPropertyChanged(nameof(DotText));
        OnPropertyChanged(nameof(ScopeText));
        MissCount = (IsUtc ? _utcSnap?.MissedCount : _localSnap?.MissedCount) ?? 0;
        if (Range == CardRange.Today) Render();
        else BeginRangeQuery();
        _svc.UiState.Save(_svc.Main, null, null);
    }

    public void SetRange(CardRange range, DateTime? start = null, DateTime? end = null)
    {
        if (start is not null) CustomStart = start.Value;
        if (end is not null) CustomEnd = end.Value;
        Range = range;
        RangeTag = range switch
        {
            CardRange.Today => "本日",
            CardRange.Week => "本周",
            CardRange.Days7 => "近 7 日",
            CardRange.Month => "本月",
            CardRange.LastMonth => "上月",
            CardRange.Quarter => "本季度",
            CardRange.Year => "本年",
            CardRange.Custom => $"{CustomStart:MM-dd}~{CustomEnd:MM-dd}",
            _ => "",
        };
    }

    public void SetMultiplierView(bool mul)
    {
        MultiplierView = mul;
    }

    private void ToggleCaliber() => IsUtc = !IsUtc;

    // —— 渲染 ——
    private ModelSnapshot? Snap => IsUtc ? _utcSnap : _localSnap;

    /// <summary>当前显示模式的缓存失效（C17：offset/配置变更后调用）。</summary>
    public void InvalidateCaches()
    {
        _rangeRows = null;
        _queryGeneration++;
        if (Range != CardRange.Today) BeginRangeQuery();
    }

    public void Refresh()
    {
        if (Range == CardRange.Today) Render();
        else BeginRangeQuery();
    }

    private void Render()
    {
        var s = Snap;
        if (s is null) return;
        ProviderTag = ProviderBadge(Provider);
        ProviderKind = ProviderKindOf(Provider);
        RequestCountText = Fmt.N0(s.RequestCount);
        DeltaText = s.DeltaTokens > 0 ? Fmt.DeltaFull(s.DeltaTokens) : "";
        MissCount = s.MissedCount;

        long total, hit, miss, comp, reasoning;
        if (MultiplierView)
        {
            total = s.MulTotal;
            hit = s.MulCacheHit;
            miss = s.MulCacheMiss;
            comp = s.MulCompletion;
            reasoning = s.MulReasoning;
            TotalUnitText = "TOTAL ×";
        }
        else
        {
            total = s.TotalTokens;
            hit = s.CacheHitTokens;
            miss = s.CacheMissTokens;
            comp = s.CompletionTokens;
            reasoning = s.ReasoningTokens;
            TotalUnitText = "TOTAL";
        }
        var rate = _svc.Engine.Pricing.GetMultiplier(Key, DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            IsUtc ? TokenMonitor.Core.Pricing.BucketScope.Utc : TokenMonitor.Core.Pricing.BucketScope.Local);
        MulBadgeText = MultiplierView && rate.Matched && Math.Abs(rate.Rate - 1.0) > 0.001
            ? Fmt.Rate(rate.Rate) + " 倍率计" : "";

        TotalText = Fmt.N0(total);
        RenderRatios(total, hit, miss, comp);
    }

    private void RenderRatios(long total, long hit, long miss, long comp)
    {
        var t = Math.Max(1, total);
        RatioH = (double)hit / t;
        RatioM = (double)miss / t;
        RatioO = (double)comp / t;
        RatioHText = Fmt.Pct(RatioH);
        RatioMText = Fmt.Pct(RatioM);
        RatioOText = Fmt.Pct(RatioO);
    }

    /// <summary>范围查询（异步；回包校验 [C16]：卡片键 + 发起序号 + 范围仍一致）。</summary>
    public void BeginRangeQuery()
    {
        var gen = ++_queryGeneration;
        var scope = IsUtc ? TokenMonitor.Core.Pricing.BucketScope.Utc : TokenMonitor.Core.Pricing.BucketScope.Local;
        var (start, end) = RangeDates();
        var key = $"{Key}|{scope}|{Range}|{start:yyyyMMdd}|{end:yyyyMMdd}|{MultiplierView}|{gen}";
        _pendingQueryKey = key;
        IsStale = Range != CardRange.Today;

        var engine = _svc.Engine;
        Task.Run(() =>
        {
            try
            {
                IReadOnlyList<TokenMonitor.Core.Storage.ModelDailyAggregate> rows;
                if (Range == CardRange.Days7)
                    rows = engine.Queries.GetRecentDays(7, scope).Rows;
                else
                    rows = engine.Queries.GetRange(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), scope);
                return (Key0: key, Gen: gen, Rows: rows);
            }
            catch (Exception ex)
            {
                TokenMonitor.Core.SysUtil.Logger.Warn("App", $"range query failed: {ex.Message}");
                return (Key0: key, Gen: gen, Rows: (IReadOnlyList<TokenMonitor.Core.Storage.ModelDailyAggregate>)Array.Empty<TokenMonitor.Core.Storage.ModelDailyAggregate>());
            }
        }).ContinueWith(t =>
        {
            if (t.Result.Gen != _queryGeneration || t.Result.Key0 != _pendingQueryKey) return; // 过期回包丢弃
            _rangeRows = t.Result.Rows;
            IsStale = false;
            Application.Current?.Dispatcher.BeginInvoke(RenderRange);
        }, TaskScheduler.Default);
    }

    private void RenderRange()
    {
        var rows = _rangeRows;
        if (rows is null) return;
        ProviderTag = ProviderBadge(Provider);
        ProviderKind = ProviderKindOf(Provider);
        DeltaText = "";   // 范围视图无实时增量

        long req = 0, total = 0, hit = 0, miss = 0, comp = 0, reasoning = 0;
        foreach (var r in rows)
        {
            if ((r.Provider + "/" + r.Model) != Key && !(string.IsNullOrEmpty(r.Provider) && r.Model == Key)) continue;
            req += r.RequestCount;
            total += MultiplierView ? r.MulTotal : r.TotalTokens;
            hit += MultiplierView ? r.MulCacheHit : r.CacheHitTokens;
            miss += MultiplierView ? r.MulCacheMiss : r.CacheMissTokens;
            comp += MultiplierView ? r.MulCompletion : r.CompletionTokens;
            reasoning += MultiplierView ? r.MulReasoning : r.ReasoningTokens;
        }
        RequestCountText = Fmt.N0(req);
        TotalUnitText = MultiplierView ? "TOTAL ×" : "TOTAL";
        TotalText = Fmt.N0(total);
        MissCount = 0;   // 范围视图不展示漏抓（当日概念）
        MulBadgeText = MultiplierView ? "× 倍率计" : "";
        RenderRatios(total, hit, miss, comp);
        OnPropertyChanged(nameof(SummaryForSide));
    }

    /// <summary>侧栏数据源（Today=实时快照；其余=范围聚合）。</summary>
    public (long Total, long Hit, long Miss, long Comp, long Reasoning, long Req, double Cny, double Usd) SummaryForSide
    {
        get
        {
            if (Range == CardRange.Today)
            {
                var s = Snap;
                if (s is null) return (0, 0, 0, 0, 0, 0, 0, 0);
                return MultiplierView
                    ? (s.MulTotal, s.MulCacheHit, s.MulCacheMiss, s.MulCompletion, s.MulReasoning, s.RequestCount, s.CostCNY, s.CostUSD)
                    : (s.TotalTokens, s.CacheHitTokens, s.CacheMissTokens, s.CompletionTokens, s.ReasoningTokens, s.RequestCount, s.CostCNY, s.CostUSD);
            }
            var rows = _rangeRows;
            long total = 0, hit = 0, miss = 0, comp = 0, rea = 0, req = 0; double cny = 0, usd = 0;
            if (rows is not null)
                foreach (var r in rows)
                {
                    if ((r.Provider + "/" + r.Model) != Key) continue;
                    req += r.RequestCount;
                    total += MultiplierView ? r.MulTotal : r.TotalTokens;
                    hit += MultiplierView ? r.MulCacheHit : r.CacheHitTokens;
                    miss += MultiplierView ? r.MulCacheMiss : r.CacheMissTokens;
                    comp += MultiplierView ? r.MulCompletion : r.CompletionTokens;
                    rea += MultiplierView ? r.MulReasoning : r.ReasoningTokens;
                    cny += r.CostCNY; usd += r.CostUSD;
                }
            return (total, hit, miss, comp, rea, req, cny, usd);
        }
    }

    /// <summary>范围日期区间（按口径日历解释，[C9] 由 Core.GetRange 换算）。</summary>
    public (DateTime Start, DateTime End) RangeDates()
    {
        var today = DateTime.Today;   // LOCAL 日历；UTC 口径由 Core 按口径换算
        return Range switch
        {
            CardRange.Today => (today, today),
            CardRange.Week => (StartOfWeek(today), today),
            CardRange.Days7 => (today.AddDays(-6), today),
            CardRange.Month => (new DateTime(today.Year, today.Month, 1), today),
            CardRange.LastMonth => (new DateTime(today.Year, today.Month, 1).AddMonths(-1),
                                    new DateTime(today.Year, today.Month, 1).AddDays(-1)),
            CardRange.Quarter => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            CardRange.Year => (new DateTime(today.Year, 1, 1), today),
            CardRange.Custom => (CustomStart, CustomEnd),
            _ => (today, today),
        };
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        var diff = ((int)d.DayOfWeek + 6) % 7;   // 周一为一周始
        return d.AddDays(-diff);
    }

    public static string ProviderBadge(string provider) => provider switch
    {
        "GLM" or "Zhipu" => "GLM",
        "DeepSeek" => "DS",
        "Moonshot" => "MS",
        "MiMo" => "MM",
        "DashScope" or "Qwen" => "QW",
        "" => "—",
        _ => provider.Length <= 2 ? provider.ToUpperInvariant() : provider[..2].ToUpperInvariant(),
    };

    public static string ProviderKindOf(string provider) => provider switch
    {
        "GLM" or "Zhipu" => "Glm",
        "DeepSeek" => "DeepSeek",
        "DashScope" or "Qwen" => "Qwen",
        _ => "Default",
    };
}
