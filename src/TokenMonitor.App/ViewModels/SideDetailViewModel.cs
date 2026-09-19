using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.App.ViewModels;

/// <summary>折线图数据点（MM-dd 标签 + 总量）。</summary>
public sealed record ChartPoint(string Label, double Value);

/// <summary>
/// 详情侧栏（选中卡；D5）。命中率环 / Stat4 / 近7日折线（倍率模式画 mul_total）/
/// 成本条 A·B 双态 / 由 MainViewModel 每秒喂入双钟文本。
/// 7 日折线缓存键含 (模型, 口径, 倍率)；ConfigChanged/DayRolledOver 失效 [C17]。
/// </summary>
public partial class SideDetailViewModel : ObservableObject
{
    private readonly Services.AppServices _svc;
    private int _chartGeneration;
    private string _pendingChartKey = "";
    private CardViewModel? _card;

    public SideDetailViewModel(Services.AppServices svc)
    {
        _svc = svc;
        Chart = new ObservableCollection<ChartPoint>();
        _labels = new ObservableCollection<string>();
    }

    public ObservableCollection<ChartPoint> Chart { get; }
    private readonly ObservableCollection<string> _labels;
    [ObservableProperty] private string _headText = "详情";
    [ObservableProperty] private string _scopeTag = "本日 · LOCAL";
    [ObservableProperty] private double _ringFraction;
    [ObservableProperty] private string _ringPercentText = "0%";
    [ObservableProperty] private string _ringCaption = "HIT RATE";
    [ObservableProperty] private string _ringInfo1 = "缓存命中率";
    [ObservableProperty] private string _ringInfo2 = "命中 ÷ (命中+未命中)";
    [ObservableProperty] private string _statHitText = "0";
    [ObservableProperty] private string _statMissText = "0";
    [ObservableProperty] private string _statOutputText = "0";
    [ObservableProperty] private string _statReasoningText = "0";
    [ObservableProperty] private bool _costNormal;     // A 态 ¥+$ 双格
    [ObservableProperty] private bool _costUnpriced;   // B 态 不计价
    [ObservableProperty] private string _costCnyText = "¥0.0000";
    [ObservableProperty] private string _costUsdText = "$0.0000";
    [ObservableProperty] private string _unpricedText = "套餐不计价 · Coding Plan";
    [ObservableProperty] private string _lastUpdateText = "--:--:--";

    public void Bind(CardViewModel? card)
    {
        _card = card;
        if (card is null)
        {
            HeadText = "详情";
            return;
        }
        HeadText = card.Model + " 详情";
        Render();
    }

    /// <summary>卡片显示模式变化后重渲（由 MainViewModel 调用）。</summary>
    public void Render()
    {
        var card = _card;
        if (card is null) return;
        HeadText = card.Model + " 详情";
        ScopeTag = $"{card.RangeTag} · {(card.IsUtc ? "UTC" : "LOCAL")}";
        var s = card.SummaryForSide;
        var hit = s.Hit;
        var miss = s.Miss;
        var denom = hit + miss;
        RingFraction = denom > 0 ? (double)hit / denom : 0;
        RingPercentText = Fmt.Pct(RingFraction) + "%";
        StatHitText = Fmt.N0(hit);
        StatMissText = Fmt.N0(miss);
        StatOutputText = Fmt.N0(s.Comp);
        StatReasoningText = Fmt.N0(s.Reasoning);

        // 成本 A/B 态：用最近活跃时刻取报价判定是否计价
        var scope = card.IsUtc ? BucketScope.Utc : BucketScope.Local;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var quote = _svc.Engine.Pricing.GetCost(card.Key, ts, hit, miss, s.Comp, scope);
        CostNormal = quote.Priced || s.Cny > 0 || s.Usd > 0;
        CostUnpriced = !CostNormal;
        CostCnyText = "¥" + Fmt.Money(s.Cny);
        CostUsdText = "$" + Fmt.Money(s.Usd);

        BeginChartQuery(card);
    }

    private string _lastChartSig = "";
    private DateTime _lastChartQueryAt = DateTime.MinValue;

    /// <summary>近 7 日折线查询（异步回包校验发起键 [C16]）。
    /// 节流：同签名 15 秒内不重查——此前挂在 200ms 快照路径上每秒 5 次清空重填集合
    /// 并重放入场动画，造成折线图频烁。</summary>
    private void BeginChartQuery(CardViewModel card)
    {
        var sig = $"{card.Key}|{card.IsUtc}|{card.MultiplierView}";
        if (sig == _lastChartSig && (DateTime.UtcNow - _lastChartQueryAt).TotalSeconds < 15) return;
        _lastChartSig = sig;
        _lastChartQueryAt = DateTime.UtcNow;
        var gen = ++_chartGeneration;
        var scope = card.IsUtc ? BucketScope.Utc : BucketScope.Local;
        var key = $"{card.Key}|{scope}|{card.MultiplierView}|{gen}";
        _pendingChartKey = key;
        var mul = card.MultiplierView;
        var modelKey = card.Key;

        Task.Run(() =>
        {
            try
            {
                var res = _svc.Engine.Queries.GetRecentDays(7, scope);
                var pts = new List<ChartPoint>();
                var byDate = res.Rows.GroupBy(r => r.Date).OrderBy(g => g.Key);
                // 恰好 7 个日历日 [C7]：右侧对齐今天的 7 天
                var dates = Enumerable.Range(0, 7).Select(i =>
                    DateTime.UtcNow.AddDays(-(6 - i)).ToString("yyyy-MM-dd")).ToList();
                if (scope == BucketScope.Local)
                    dates = Enumerable.Range(0, 7).Select(i =>
                        DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(_svc.Engine.Config.Settings.OffsetMin))
                            .AddDays(-(6 - i)).ToString("yyyy-MM-dd")).ToList();
                foreach (var d in dates)
                {
                    var g = byDate.FirstOrDefault(x => x.Key == d);
                    long sum = 0;
                    if (g is not null)
                        foreach (var r in g)
                        {
                            if ((r.Provider + "/" + r.Model) != modelKey) continue;
                            sum += mul ? r.MulTotal : r.TotalTokens;
                        }
                    pts.Add(new ChartPoint(d[5..], sum));
                }
                return (Key0: key, Gen: gen, Pts: pts);
            }
            catch (Exception ex)
            {
                TokenMonitor.Core.SysUtil.Logger.Warn("App", "chart query failed: " + ex.Message);
                return (Key0: key, Gen: gen, Pts: new List<ChartPoint>());
            }
        }).ContinueWith(t =>
        {
            if (t.Result.Gen != _chartGeneration || t.Result.Key0 != _pendingChartKey) return;
            var pts = t.Result.Pts;
            // 数据去重：与现有内容一致则不清空重填（避免无意义重绘/动画重放）
            if (Chart.Count == pts.Count)
            {
                var same = true;
                for (var i = 0; i < pts.Count; i++)
                    if (Chart[i].Label != pts[i].Label || Math.Abs(Chart[i].Value - pts[i].Value) > 0.5) { same = false; break; }
                if (same) return;
            }
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Chart.Clear();
                foreach (var p in pts) Chart.Add(p);
                ChartVersion++;
                OnPropertyChanged(nameof(Chart));
            });
        }, TaskScheduler.Default);
    }

    /// <summary>折线数据版本（MainViewModel 在 DayRolledOver/ConfigChanged 后 ++ 并触发重查）。</summary>
    [ObservableProperty] private int _chartVersion;

    /// <summary>双钟文本（MainViewModel 每秒写入）。</summary>
    [ObservableProperty] private string _utcClock = "--:--:--";
    [ObservableProperty] private string _localClock = "--:--:--";
    [ObservableProperty] private string _localClockLabel = "LOCAL";

    /// <summary>图表控件直接消费 Points/Labels 的便捷投影。</summary>
    public IReadOnlyList<double> ChartValues => Chart.Select(c => c.Value).ToList();
    public IReadOnlyList<string> ChartLabels => Chart.Select(c => c.Label).ToList();
}
