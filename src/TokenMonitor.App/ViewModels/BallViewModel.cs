using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.Core.Events;
using TokenMonitor.Core.Stats;

namespace TokenMonitor.App.ViewModels;

/// <summary>跑马灯条目（1s 只改文本，不重建动画容器）。</summary>
public sealed partial class MarqueeItemVm : ObservableObject
{
    public string Key { get; init; } = "";
    [ObservableProperty] private string _title = "";
    /// <summary>指标名（TOTAL/命中/未命中/输出/命中率/调用）。</summary>
    [ObservableProperty] private string _metric = "";
    [ObservableProperty] private string _value = "0";
    [ObservableProperty] private string _delta = "";
}

/// <summary>
/// 悬浮球 VM：订阅 BallStatsTick（1s，合并式仅保留最新）。
/// 收起态 = 总量缩写 + 增量 + 命中弧；展开态 = LIVE 徽 + 跑马灯 + 双钟。
/// </summary>
public partial class BallViewModel : ObservableObject
{
    private readonly Services.AppServices _svc;
    private readonly List<IDisposable> _subs = new();
    private readonly DispatcherTimer _clockTimer;
    private readonly Dictionary<string, MarqueeItemVm> _items = new();

    public BallViewModel(Services.AppServices svc)
    {
        _svc = svc;
        Items = new ObservableCollection<MarqueeItemVm>();
        _subs.Add(svc.Engine.Bus.Subscribe<BallStatsTick>(OnTick, EventDispatch.UiThread));
        _subs.Add(svc.Engine.Bus.Subscribe<ConfigChanged>(_ => { }));
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) =>
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var offset = TimeSpan.FromMinutes(svc.Engine.Config.Settings.OffsetMin);
            UtcSmall = Fmt.Clock(nowUtc.DateTime)[..5];
            LocalBig = Fmt.Clock(nowUtc.ToOffset(offset).DateTime);
        };
        _clockTimer.Start();
    }

    public ObservableCollection<MarqueeItemVm> Items { get; }

    [ObservableProperty] private string _ballTotal = "0";
    [ObservableProperty] private string _ballDelta = "";
    [ObservableProperty] private double _hitFraction;
    [ObservableProperty] private string _liveText = "LIVE";
    [ObservableProperty] private string _localBig = "--:--:--";
    [ObservableProperty] private string _utcSmall = "--:--";
    [ObservableProperty] private bool _isListening = true;

    private void OnTick(BallStatsTick tick)
    {
        var s = tick.Snapshot;
        long total = 0, delta = 0, hit = 0, miss = 0;
        foreach (var m in s.LocalModels)
        {
            total += m.TotalTokens;
            delta += m.DeltaTokens;
            hit += m.CacheHitTokens;
            miss += m.CacheMissTokens;
        }
        BallTotal = Fmt.Compact(total);
        BallDelta = delta > 0 ? "▲" + Fmt.Compact(delta) : "";
        HitFraction = hit + miss > 0 ? (double)hit / (hit + miss) : 0;

        RebuildMarquee(s);
    }

    /// <summary>最近活跃多久之内仍算"正在跑"（衔接同一模型的连续请求，避免模式来回跳）。</summary>
    private const int ActiveGraceSeconds = 15;

    /// <summary>
    /// 跑马灯内容（用户要求）：
    /// ① 有模型正在跑（代理在途，或 <see cref="ActiveGraceSeconds"/> 秒内有过请求）→ 循环该模型的
    ///    总 token / 命中 / 未命中 / 输出 / 命中率 / 调用次数；
    /// ② 空闲 → 按面板卡片顺序循环每张**可见卡片**的总 token 与命中率（取该卡自己的口径与倍率视图）。
    /// </summary>
    private void RebuildMarquee(StatsSnapshot s)
    {
        var rows = new List<MarqueeRow>();
        var running = PickRunningModel(s);
        if (running is { } m)
        {
            var title = Label(m.Provider, m.Model);
            rows.Add(new($"{title}|TOTAL", title, "TOTAL", Fmt.N0(m.TotalTokens),
                m.DeltaTokens > 0 ? "+" + Fmt.Compact(m.DeltaTokens) : ""));
            rows.Add(new($"{title}|HIT", title, "命中", Fmt.N0(m.CacheHitTokens), ""));
            rows.Add(new($"{title}|MISS", title, "未命中", Fmt.N0(m.CacheMissTokens), ""));
            rows.Add(new($"{title}|OUT", title, "输出", Fmt.N0(m.CompletionTokens), ""));
            rows.Add(new($"{title}|RATE", title, "命中率", RateText(m.CacheHitTokens, m.CacheMissTokens), ""));
            rows.Add(new($"{title}|CALLS", title, "调用", Fmt.N0(m.RequestCount), ""));
        }
        else
        {
            foreach (var card in _svc.Main?.VisibleCards ?? Enumerable.Empty<CardViewModel>())
            {
                var snap = FindModel(s, card.Key, card.IsUtc);
                if (snap is null) continue;
                var title = Label(snap.Provider, snap.Model);
                var (tot, h, mi) = card.MultiplierView
                    ? (snap.MulTotal, snap.MulCacheHit, snap.MulCacheMiss)
                    : (snap.TotalTokens, snap.CacheHitTokens, snap.CacheMissTokens);
                rows.Add(new($"{title}|TOTAL", title, "TOTAL", Fmt.N0(tot), ""));
                rows.Add(new($"{title}|RATE", title, "命中率", RateText(h, mi), ""));
            }
        }
        SyncItems(rows);
    }

    /// <summary>挑选"正在跑"的模型：在途优先，其次最近活跃；无则 null（进入卡片汇总模式）。</summary>
    private ModelSnapshot? PickRunningModel(StatsSnapshot s)
    {
        var inFlight = _svc.Engine.Proxy.InFlightModels;
        ModelSnapshot? best = null;
        var bestScore = long.MinValue;
        foreach (var m in s.LocalModels)
        {
            var key = m.Provider + "/" + m.Model;
            var flying = inFlight.Count > 0 && inFlight.Any(f => SameModelName(f, key));
            var age = s.CreatedAtUnix - m.LastActiveUnix;
            if (!flying && age > ActiveGraceSeconds) continue;
            var score = (flying ? 1_000_000_000L : 0) + m.LastActiveUnix;
            if (score > bestScore) { bestScore = score; best = m; }
        }
        return best;
    }

    /// <summary>请求模型名与快照键的宽松匹配（代理侧是原始请求名，快照键为 provider/model，且可能是别名）。</summary>
    private static bool SameModelName(string requestModel, string snapshotKey)
    {
        if (string.Equals(requestModel, snapshotKey, StringComparison.OrdinalIgnoreCase)) return true;
        static string Seg(string s)
        {
            var i = s.LastIndexOf('/');
            return i < 0 ? s : s[(i + 1)..];
        }
        return string.Equals(Seg(requestModel), Seg(snapshotKey), StringComparison.OrdinalIgnoreCase);
    }

    private static ModelSnapshot? FindModel(StatsSnapshot s, string key, bool utc)
    {
        foreach (var m in utc ? s.UtcModels : s.LocalModels)
            if (m.Provider + "/" + m.Model == key || (m.Provider.Length == 0 && m.Model == key)) return m;
        return null;
    }

    private static string RateText(long hit, long miss)
        => hit + miss > 0 ? Fmt.Pct((double)hit / (hit + miss)) + "%" : "—";

    /// <summary>模型显示名：PROVIDER-MODEL（无 provider 时仅模型名），全大写。</summary>
    private static string Label(string provider, string model)
        => (string.IsNullOrEmpty(provider) ? model : provider + "-" + model).ToUpperInvariant();

    private readonly record struct MarqueeRow(string Key, string Title, string Metric, string Value, string Delta);

    /// <summary>行集合未变则原地改文本（不打断滚动节奏）；模式切换/模型增删才重建。</summary>
    private void SyncItems(List<MarqueeRow> rows)
    {
        var same = Items.Count == rows.Count;
        if (same)
            for (var i = 0; i < rows.Count; i++)
                if (Items[i].Key != rows[i].Key) { same = false; break; }

        if (same)
        {
            for (var i = 0; i < rows.Count; i++)
            {
                var it = Items[i];
                it.Title = rows[i].Title;
                it.Metric = rows[i].Metric;
                it.Value = rows[i].Value;
                it.Delta = rows[i].Delta;
            }
            return;
        }

        _items.Clear();
        Items.Clear();
        foreach (var r in rows)
        {
            var vm = new MarqueeItemVm { Key = r.Key, Title = r.Title, Metric = r.Metric, Value = r.Value, Delta = r.Delta };
            _items[r.Key] = vm;
            Items.Add(vm);
        }
    }

    public void Dispose()
    {
        _subs.ForEach(s => s.Dispose());
        _clockTimer.Stop();
    }
}
