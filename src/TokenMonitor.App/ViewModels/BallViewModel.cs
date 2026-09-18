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

        // 跑马灯条目：本地口径各模型（原地改文本，动画不重置）
        var seen = new HashSet<string>();
        foreach (var m in s.LocalModels)
        {
            var key = m.Provider + "/" + m.Model;
            seen.Add(key);
            var title = (string.IsNullOrEmpty(m.Provider) ? m.Model : m.Provider + "-" + m.Model).ToUpperInvariant();
            if (!_items.TryGetValue(key, out var vm))
            {
                vm = new MarqueeItemVm { Key = key, Title = title };
                _items[key] = vm;
                Items.Add(vm);
            }
            vm.Title = title;
            vm.Value = Fmt.N0(m.TotalTokens);
            vm.Delta = m.DeltaTokens > 0 ? "+" + Fmt.Compact(m.DeltaTokens) : "";
        }
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(Items[i].Key)) continue;
            _items.Remove(Items[i].Key);
            Items.RemoveAt(i);
        }
    }

    public void Dispose()
    {
        _subs.ForEach(s => s.Dispose());
        _clockTimer.Stop();
    }
}
