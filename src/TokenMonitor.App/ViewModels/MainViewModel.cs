using System.Collections.ObjectModel;
using System.Windows.Threading;
using System.Collections.Specialized;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.Core.Events;
using TokenMonitor.Core.Stats;

namespace TokenMonitor.App.ViewModels;

/// <summary>
/// 主面板 VM。订阅 PanelStatsTick(200ms)/ConfigChanged/DayRolledOver/ProxyStateChangedEvent
/// （UiThread 分发 + 合并式仅保留最新背压）；向 Engine 下发命令；
/// 异步回包校验（C16 由 CardViewModel/SideDetailViewModel 内实现）；
/// 口径/倍率等缓存随 ConfigChanged/DayRolledOver 失效（C17）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Services.AppServices _svc;
    private readonly List<IDisposable> _subs = new();
    private readonly DispatcherTimer _clockTimer;
    private DateTime _lastRefreshAt;

    public MainViewModel(Services.AppServices svc)
    {
        _svc = svc;
        Cards = new ObservableCollection<CardViewModel>();
        Cards.CollectionChanged += (_, _) => RenumberSerials();
        Side = new SideDetailViewModel(svc);
        Detail = new DualClockVm();
        Banner = new BannerVm();

        ToggleTopmostCommand = new RelayCommand<bool>(v => IsTopmost = v);
        CollapseCommand = new RelayCommand(() => svc.CollapseToBall());
        BannerGoCommand = new RelayCommand(() => svc.Dialogs.ShowManualCalibrate());
        BannerCloseCommand = new RelayCommand(() => Banner.Visible = false);
        OpenMultiplierCommand = new RelayCommand<string?>(m => svc.Dialogs.ShowMultiplierConfig(m));
        OpenPricingCommand = new RelayCommand<string?>(m => svc.Dialogs.ShowPricingConfig(m));
        OpenCalibrateCommand = new RelayCommand<string?>(m => svc.Dialogs.ShowManualCalibrate(m));
        OpenOpLogsCommand = new RelayCommand<string?>(m => svc.Dialogs.ShowOpLogs(m));
        OpenExportCommand = new RelayCommand(() => svc.Dialogs.ShowExport());
        OpenCardVisibilityCommand = new RelayCommand(() => svc.Dialogs.ShowCardVisibility());
        OpenTimeRangeCommand = new RelayCommand<CardViewModel?>(c => svc.Dialogs.ShowTimeRange(c));
        ResetTodayCommand = new RelayCommand<string?>(m => ResetToday(m));
        SelectCardCommand = new RelayCommand<CardViewModel?>(c => { if (c is not null) SelectedCard = c; });
        OpenConfigFileCommand = new RelayCommand(() =>
        {
            try
            {
                var path = System.IO.Path.Combine(svc.Engine.Config.DataDir, "config.json");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { TokenMonitor.Core.SysUtil.Logger.Warn("App", "open config failed: " + ex.Message); }
        });

        // 双钟每秒
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => TickClocks();

        // 事件订阅（UiThread 分发；快照合并式仅保留最新）
        _subs.Add(svc.Engine.Bus.Subscribe<PanelStatsTick>(OnPanelTick, EventDispatch.UiThread));
        _subs.Add(svc.Engine.Bus.Subscribe<BallStatsTick>(OnBallTick, EventDispatch.UiThread));
        _subs.Add(svc.Engine.Bus.Subscribe<ConfigChanged>(OnConfigChanged, EventDispatch.UiThread));
        _subs.Add(svc.Engine.Bus.Subscribe<DayRolledOver>(OnDayRolledOver, EventDispatch.UiThread));
        _subs.Add(svc.Engine.Bus.Subscribe<ProxyStateChangedEvent>(OnProxyStateChanged, EventDispatch.UiThread));
    }

    public ObservableCollection<CardViewModel> Cards { get; }
    public SideDetailViewModel Side { get; }
    public DualClockVm Detail { get; }
    public BannerVm Banner { get; }

    [ObservableProperty] private CardViewModel? _selectedCard;
    [ObservableProperty] private bool _isTopmost = true;
    [ObservableProperty] private double _panelOpacity = 1.0;
    [ObservableProperty] private string _listenText = "127.0.0.1:8280";
    [ObservableProperty] private string _statusText = "● 就绪";
    [ObservableProperty] private string _lastRefreshText = "--:--:--";
    [ObservableProperty] private string _caliberBadgeText = "LOCAL";

    public RelayCommand<bool> ToggleTopmostCommand { get; }
    public RelayCommand CollapseCommand { get; }
    public RelayCommand BannerGoCommand { get; }
    public RelayCommand BannerCloseCommand { get; }
    public RelayCommand<string?> OpenMultiplierCommand { get; }
    public RelayCommand<string?> OpenPricingCommand { get; }
    public RelayCommand<string?> OpenCalibrateCommand { get; }
    public RelayCommand<string?> OpenOpLogsCommand { get; }
    public RelayCommand OpenExportCommand { get; }
    public RelayCommand OpenCardVisibilityCommand { get; }
    public RelayCommand<CardViewModel?> OpenTimeRangeCommand { get; }
    public RelayCommand<string?> ResetTodayCommand { get; }
    public RelayCommand<CardViewModel?> SelectCardCommand { get; }
    public RelayCommand OpenConfigFileCommand { get; }

    partial void OnIsTopmostChanged(bool value) => Application.Current?.Dispatcher.BeginInvoke(() =>
    {
        if (_svc.PanelWindow is not null) _svc.PanelWindow.Topmost = value;
    });

    partial void OnPanelOpacityChanged(double value)
    {
        // 透明度滑杆（S1/S3 ≥0.6、S2 ≥0.7、S4 ≥0.65，低于下限钳制）
        var v = Math.Max(value, _svc.Theme.MinOpacity);
        if (Math.Abs(v - value) > 0.001) PanelOpacity = v;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_svc.PanelWindow is not null) _svc.PanelWindow.Opacity = PanelOpacity;
            _svc.UiState.Save(this, null, _svc.PanelWindow);
        });
    }

    partial void OnSelectedCardChanged(CardViewModel? value)
    {
        Side.Bind(value);
    }

    /// <summary>启动初始化：读 ui_state 还原卡片状态。</summary>
    public void Initialize()
    {
        var ui = _svc.Engine.Config.Ui;
        var hidden = ui?.HiddenCards ?? new HashSet<string>();
        foreach (var kv in ui?.CardScope ?? (IReadOnlyDictionary<string, string>)new Dictionary<string, string>())
            _scopeMemo[kv.Key] = kv.Value;
        foreach (var kv in ui?.CardMultiplierView ?? (IReadOnlyDictionary<string, bool>)new Dictionary<string, bool>())
            _mulMemo[kv.Key] = kv.Value;
        _orderMemo = ui?.CardOrder?.ToList() ?? new List<string>();
        IsTopmost = true;
        var p = ui?.Panel;
        PanelOpacity = Math.Clamp(p is not null && p.Opacity > 0 ? p.Opacity : 1.0, _svc.Theme.MinOpacity, 1.0);
        TickClocks();
        _clockTimer.Start();
    }

    private readonly Dictionary<string, string> _scopeMemo = new();
    private readonly Dictionary<string, bool> _mulMemo = new();
    private List<string> _orderMemo = new();

    // ================= 事件处理 =================

    private void OnPanelTick(PanelStatsTick tick) => ApplySnapshot(tick.Snapshot);

    private void OnBallTick(BallStatsTick tick)
    {
        // 副作用仅激增检测（球的 1s 数据由 BallViewModel 自己订阅）
        _svc.Surge.Feed(TotalForSurge(tick.Snapshot));
    }

    private static long TotalForSurge(StatsSnapshot s)
    {
        long sum = 0;
        foreach (var m in s.UtcModels) sum += m.TotalTokens;
        return sum;
    }

    private void ApplySnapshot(StatsSnapshot snap)
    {
        var byKey = new Dictionary<string, ModelSnapshot>();
        foreach (var m in snap.UtcModels) byKey[m.Provider + "/" + m.Model] = m;
        var localByKey = new Dictionary<string, ModelSnapshot>();
        foreach (var m in snap.LocalModels) localByKey[m.Provider + "/" + m.Model] = m;

        // 新模型 → 建卡
        var changed = false;
        foreach (var key in byKey.Keys)
        {
            if (Cards.Any(c => c.Key == key)) continue;
            var (provider, model) = SplitKey(key);
            var card = new CardViewModel(_svc, provider, model)
            {
                IsUtc = _scopeMemo.TryGetValue(key, out var sc) && sc == "utc",
                MultiplierView = _mulMemo.TryGetValue(key, out var mv) && mv,
                IsHidden = _hiddenMemo.Contains(key),
            };
            card.PropertyChanged += Card_PropertyChanged;
            Cards.Add(card);
            changed = true;
        }

        // 移除消失的模型（快照不再含骨架）
        for (var i = Cards.Count - 1; i >= 0; i--)
        {
            if (byKey.ContainsKey(Cards[i].Key)) continue;
            Cards[i].PropertyChanged -= Card_PropertyChanged;
            _hiddenMemo.Remove(Cards[i].Key);
            Cards.RemoveAt(i);
            changed = true;
        }

        // 排序：ui_state.card_order 优先，其余按名称
        if (changed) ApplyOrder();

        // 数据
        foreach (var card in Cards)
        {
            var utc = byKey.GetValueOrDefault(card.Key);
            var local = localByKey.GetValueOrDefault(card.Key);
            card.ApplySnapshot(
                utc ?? ZeroSnapshot(card, snap, true),
                local ?? ZeroSnapshot(card, snap, false),
                snap.MissedCaptures);
        }

        // 漏抓横幅（§1.6）
        Banner.Count = snap.MissedCaptures;
        if (snap.MissedCaptures > 0 && !Banner.UserClosed) Banner.Visible = true;
        else if (snap.MissedCaptures == 0) Banner.Visible = false;

        // 口径徽章（默认 LOCAL）
        CaliberBadgeText = _svc.Engine.Config.Settings.EffectiveDateMode == "utc" ? "UTC" : "LOCAL";
        _lastRefreshAt = DateTime.Now;
        LastRefreshText = Fmt.Clock(_lastRefreshAt);

        if (SelectedCard is null && Cards.Count > 0) SelectedCard = VisibleCards.FirstOrDefault() ?? Cards[0];
        else if (SelectedCard is not null) Side.Render();
    }

    private readonly HashSet<string> _hiddenMemo = new();

    private static ModelSnapshot ZeroSnapshot(CardViewModel card, StatsSnapshot snap, bool utc)
    {
        var fallback = utc ? snap.UtcSummary : snap.LocalSummary;
        return new ModelSnapshot(card.Provider, card.Model, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, fallback?.MissedCount ?? 0);
    }

    private void Card_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is CardViewModel card && (e.PropertyName is nameof(CardViewModel.IsUtc) or nameof(CardViewModel.MultiplierView)))
            Side.Render();
    }

    private void ApplyOrder()
    {
        List<CardViewModel> ordered = new();
        foreach (var key in _orderMemo)
        {
            var card = Cards.FirstOrDefault(c => c.Key == key);
            if (card is not null) ordered.Add(card);
        }
        foreach (var card in Cards.Except(ordered).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            ordered.Add(card);
        for (var i = 0; i < ordered.Count; i++)
        {
            var newIdx = Cards.IndexOf(ordered[i]);
            if (newIdx != i) Cards.Move(newIdx, i);
        }
        for (var i = 0; i < Cards.Count; i++)
        {
            Cards[i].SerialText = (i + 1).ToString("00");
        }
        _orderMemo = Cards.Select(c => c.Key).ToList();
    }

    private void RenumberSerials()
    {
        for (var i = 0; i < Cards.Count; i++) Cards[i].SerialText = (i + 1).ToString("00");
    }

    public IEnumerable<CardViewModel> VisibleCards => Cards.Where(c => !c.IsHidden);

    /// <summary>隐藏态变化（MainWindow 重建卡片视图时消费）。</summary>
    public event Action? VisibleChanged;

    public void SetHidden(CardViewModel card, bool hidden)
    {
        card.IsHidden = hidden;
        if (hidden) _hiddenMemo.Add(card.Key); else _hiddenMemo.Remove(card.Key);
        VisibleChanged?.Invoke();
        if (SelectedCard is { IsHidden: true }) SelectedCard = VisibleCards.FirstOrDefault();
        _svc.UiState.Save(this, null, null);
    }

    /// <summary>卡片排序落盘（显示隐藏卡片对话框拖拽后调用）。</summary>
    public void ApplyCardOrder(IEnumerable<string> orderedKeys)
    {
        _orderMemo = orderedKeys.ToList();
        ApplyOrder();
        _svc.UiState.Save(this, null, null);
    }

    /// <summary>模型名右键 刷新数据（F5）。</summary>
    public void RefreshCard(CardViewModel card)
    {
        card.InvalidateCaches();
        Side.Render();
    }

    // —— 配置/日期变更：口径缓存清空 [C17] ——
    private void OnConfigChanged(ConfigChanged evt)
    {
        if ((evt.Sections & (ConfigSectionFlags.Settings | ConfigSectionFlags.Pricing)) != 0)
        {
            foreach (var card in Cards) card.InvalidateCaches();
            Side.Render();
        }
        _svc.Tray?.RefreshChecks();
    }

    private void OnDayRolledOver(DayRolledOver evt)
    {
        foreach (var card in Cards) card.InvalidateCaches();
        Side.Render();
    }

    private void OnProxyStateChanged(ProxyStateChangedEvent evt)
    {
        StatusText = evt.IsListening ? "● 代理运行中" : "● 代理已停止";
        ListenText = evt.ListenAddr;
    }

    // ================= 双钟 =================

    private void TickClocks()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var offset = TimeSpan.FromMinutes(_svc.Engine.Config.Settings.OffsetMin);
        var local = nowUtc.ToOffset(offset);
        Detail.UtcClock = Fmt.Clock(nowUtc.DateTime);
        Detail.LocalClock = Fmt.Clock(local.DateTime);
        Detail.LocalLabel = "LOCAL" + (offset >= TimeSpan.Zero ? "+" : "−") + offset.ToString(@"hh\:mm");
        Side.UtcClock = Detail.UtcClock;
        Side.LocalClock = Detail.LocalClock;
        Side.LocalClockLabel = Detail.LocalLabel;
        OnPropertyChanged(nameof(UtcClockText));
        OnPropertyChanged(nameof(LocalClockText));
    }

    public string UtcClockText => Detail.UtcClock;
    public string LocalClockText => Detail.LocalClock;

    // ================= 命令实现 =================

    private void ResetToday(string? modelKey)
    {
        var label = string.IsNullOrEmpty(modelKey) ? "全部模型" : modelKey;
        var r = MessageBox.Show($"确认重置「{label}」的今日数据？\n该操作将删除今日 usage_log/漏抓记录，且已自动备份。",
            "重置今日", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        Task.Run(() =>
        {
            try { _svc.Engine.ResetToday(string.IsNullOrEmpty(modelKey) ? null : modelKey); }
            catch (Exception ex) { TokenMonitor.Core.SysUtil.Logger.Error("App", "reset today failed", ex); }
        });
    }

    public void PersistOrder() => _svc.UiState.Save(this, null, null);

    private static (string Provider, string Model) SplitKey(string key)
    {
        var i = key.IndexOf('/');
        return i < 0 ? ("", key) : (key[..i], key[(i + 1)..]);
    }

    public void Dispose() => _subs.ForEach(s => s.Dispose());

    /// <summary>状态条 VM 聚合。</summary>
    public sealed partial class DualClockVm : ObservableObject
    {
        [ObservableProperty] private string _utcClock = "--:--:--";
        [ObservableProperty] private string _localClock = "--:--:--";
        [ObservableProperty] private string _localLabel = "LOCAL";
    }

    public sealed partial class BannerVm : ObservableObject
    {
        [ObservableProperty] private long _count;
        [ObservableProperty] private bool _visible;
        [ObservableProperty] private bool _userClosed;

        partial void OnVisibleChanged(bool value)
        {
            if (!value) { }
        }

        public string Text => $"检测到 {Count} 个漏抓请求，请校准补录";
        partial void OnCountChanged(long value) => OnPropertyChanged(nameof(Text));
    }
}
