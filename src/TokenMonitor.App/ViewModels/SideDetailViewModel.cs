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
/// 折线取数规则（用户定稿）：**前 6 天走 DB 查询（当天内不变量，事件失效 [C17]），
/// “今日点”与卡片同源——直接取实时快照（200ms 面板路径原地改点，无动画、无 DB 轮询）**；
/// 入场动画仅在切换卡片或手动“刷新数据”时重放；DayRolledOver/ConfigChanged 触发强制重查（无动画）。
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
        Chart.Clear();                     // 换卡：清空旧卡点位，新查询落地前 live 更新自动跳过
        ForceChartRefresh(withEntrance: true);  // 切换卡片 = 入场动画的触发点之一
        Render();
    }

    /// <summary>卡片显示模式变化后重渲（由 MainViewModel 调用）。折线只做“今日点”的实时更新。</summary>
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

        UpdateLiveTodayPoint(card); // 200ms 实时路径：只改“今日点”，不查库、不触发动画
    }

    /// <summary>强制重查近 7 日（跳过节流）：切卡/手动“刷新数据”（withEntrance=true，重放入场动画），
    /// DayRolledOver/ConfigChanged（false，跨零点重排日期与改价重算历史，静默落地）。
    /// DB 只负责前 6 天；“今日槽”落地时以实时快照覆盖——当日点的唯一权威来源是内存快照。</summary>
    public void ForceChartRefresh(bool withEntrance)
    {
        var card = _card;
        if (card is null) return;
        _entrancePending = withEntrance;
        var gen = ++_chartGeneration;
        var scope = card.IsUtc ? BucketScope.Utc : BucketScope.Local;
        var key = $"{card.Key}|{scope}|{card.MultiplierView}|{gen}";
        _pendingChartKey = key;
        var mul = card.MultiplierView;
        var modelKey = card.Key;
        var offset = _svc.Engine.Config.Settings.OffsetMin;

        Task.Run(() =>
        {
            try
            {
                var res = _svc.Engine.Queries.GetRecentDays(7, scope);
                var pts = BuildSevenPoints(res, scope, offset, mul, modelKey);
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
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var card0 = _card;
                if (card0 is null) return;
                var pts = t.Result.Pts;
                if (pts.Count == 7) // 今日槽以快照覆盖：DB 对当日天然滞后（缓存/刷写窗口）
                    pts[^1] = pts[^1] with { Label = TodayLabel(card0), Value = LiveTodayValue(card0) };
                ApplyPts(pts, entrance: _entrancePending);
            });
        }, TaskScheduler.Default);
    }

    private List<ChartPoint> BuildSevenPoints(TokenMonitor.Core.Stats.RecentDaysResult res,
        BucketScope scope, int offset, bool mul, string modelKey)
    {
        var pts = new List<ChartPoint>();
        var byDate = res.Rows.GroupBy(r => r.Date).OrderBy(g => g.Key);
        // 恰好 7 个日历日 [C7]：右侧对齐今天的 7 天
        var dates = Enumerable.Range(0, 7).Select(i =>
            DateTime.UtcNow.AddDays(-(6 - i)).ToString("yyyy-MM-dd")).ToList();
        if (scope == BucketScope.Local)
            dates = Enumerable.Range(0, 7).Select(i =>
                DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(offset))
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
        return pts;
    }

    /// <summary>落地 7 点（UI 线程）：值与标签都变了才动集合；entrance=false → LiveVersion（不重放动画）。</summary>
    private void ApplyPts(List<ChartPoint> pts, bool entrance)
    {
        if (pts.Count != 7) return;
        if (Chart.Count == pts.Count)
        {
            var same = true;
            for (var i = 0; i < pts.Count; i++)
                if (Chart[i].Label != pts[i].Label || Math.Abs(Chart[i].Value - pts[i].Value) > 0.5) { same = false; break; }
            if (same) return;
        }
        if (Chart.Count != pts.Count)
        {
            Chart.Clear();
            foreach (var p in pts) Chart.Add(p);
        }
        else
        {
            for (var i = 0; i < pts.Count; i++) Chart[i] = pts[i];
        }
        if (entrance) ChartVersion++; else LiveVersion++;
    }

    /// <summary>200ms 实时路径：把“今日点”替换为与卡片同源的快照值（无动画、无 DB）。
    /// 跨零点窗口（最后一个点还是昨天的标签）不动，等 DayRolledOver 的强制重查重排日期。</summary>
    private void UpdateLiveTodayPoint(CardViewModel card)
    {
        if (Chart.Count != 7) return;
        var today = TodayLabel(card);
        if (Chart[6].Label != today) return;
        var v = LiveTodayValue(card);
        if (Math.Abs(Chart[6].Value - v) <= 0.5) return; // 无变化不重绘（TrendChart7 也有同值脏检查）
        Chart[6] = Chart[6] with { Value = v };
        LiveVersion++;
    }

    private string TodayLabel(CardViewModel card) => card.IsUtc
        ? DateTime.UtcNow.ToString("yyyy-MM-dd")
        : DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromMinutes(_svc.Engine.Config.Settings.OffsetMin))
            .ToString("yyyy-MM-dd");

    /// <summary>与卡片渲染同一份数据：今日点的实时值（实际/倍率视图随卡）。</summary>
    private long LiveTodayValue(CardViewModel card)
    {
        var s = card.CurrentSnap;
        return s is null ? 0 : (card.MultiplierView ? s.MulTotal : s.TotalTokens);
    }

    /// <summary>折线数据版本：数据有任何落地（含 200ms 今日点实时更新）时 ++；
    /// MainWindow 据此重设 Trend.Points/Labels。入场动画只看 ChartVersion。</summary>
    [ObservableProperty] private int _liveVersion;

    /// <summary>入场动画版本：仅在切换卡片/手动“刷新数据”的强制重查落地时 ++（重放描边动画）。</summary>
    [ObservableProperty] private int _chartVersion;
    private bool _entrancePending;

    /// <summary>双钟文本（MainViewModel 每秒写入）。</summary>
    [ObservableProperty] private string _utcClock = "--:--:--";
    [ObservableProperty] private string _localClock = "--:--:--";
    [ObservableProperty] private string _localClockLabel = "LOCAL";

    /// <summary>图表控件直接消费 Points/Labels 的便捷投影。</summary>
    public IReadOnlyList<double> ChartValues => Chart.Select(c => c.Value).ToList();
    public IReadOnlyList<string> ChartLabels => Chart.Select(c => c.Label).ToList();
}
