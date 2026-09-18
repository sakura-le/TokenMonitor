using TokenMonitor.Core.Config;
using TokenMonitor.Core.Events;
using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Proxy;
using TokenMonitor.Core.Stats;
using TokenMonitor.Core.Storage;
using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core;

/// <summary>
/// 组合根（01-§1.1 / §2.3.9 / §7 生命周期）：装配全部组件的唯一入口。
/// 启动：配置 → 存储 → 摄入协调 → 后台历史重算 → 整桶恢复 → 代理监听（失败不终止进程，附录 B-9）→ 定时器。
/// 退出：停定时器 → 代理优雅停止 [S5] → Flush + Checkpoint → 配置落盘。
/// </summary>
public sealed class MonitorEngine : ITokenMonitorEngine
{
    private readonly MonitorEngineOptions _options;
    private readonly TimeProvider _clock;
    private readonly SysUtil.SysUtil _sys;
    private readonly string _dataDir;

    public IConfigService Config { get; }
    public IPricingEngine Pricing { get; }
    public IStore Store { get; }
    public IProxyEngine Proxy { get; }
    public IEventBus Bus { get; }
    public IStatsQueryService Queries { get; }
    public IExportService Exporter { get; }
    public ICalibrationService Calibration { get; }
    public IImportService Import { get; }

    private readonly ConfigService _configConcrete;
    private readonly PricingEngine _pricingConcrete;
    private readonly Store _storeConcrete;
    private readonly UsageCoordinator _coordinator;
    private readonly StatsTicker _ticker;
    private readonly DayWatch _dayWatch;
    private readonly RateFunc _rateFn;
    private readonly CostFunc _costFn;
    private readonly object _stateGate = new();
    private bool _started;
    private bool _stopped;
    private Task? _startupRecalcTask;

    public MonitorEngine(MonitorEngineOptions? options = null, ISysUtil? sysUtil = null)
    {
        _options = options ?? new MonitorEngineOptions();
        _clock = _options.TimeProvider ?? TimeProvider.System;
        _sys = (sysUtil as SysUtil.SysUtil) ?? new SysUtil.SysUtil(_options.DataDir);
        _dataDir = _options.DataDir ?? _sys.DataDir;
        Logger.Init(Path.Combine(_dataDir, "logs"));

        Config = _configConcrete = new ConfigService(_dataDir, _clock);
        Pricing = _pricingConcrete = new PricingEngine(Path.Combine(_dataDir, "pricing.json"));
        _pricingConcrete.SetEffectiveContext(Config.Settings.EffectiveDateMode, Config.Settings.OffsetMin);

        Store = _storeConcrete = new Store(_dataDir, _clock, _options.StoreFlushInterval);
        Bus = new EventBus();

        var usageFileLog = new UsageFileLogger(_dataDir, Config.Settings.OffsetMin);
        var acc = new Accumulator(_pricingConcrete);
        _coordinator = new UsageCoordinator(acc, _storeConcrete, usageFileLog, _pricingConcrete, Bus,
            _clock, _options.MissedCountTtl);

        _rateFn = (key, ts, hour, offsetMin) => _pricingConcrete.QuoteRate(key, ts, hour, offsetMin);
        _costFn = (key, ts, hour, offsetMin, hit, miss, comp) => _pricingConcrete.QuoteCost(key, ts, hour, offsetMin, hit, miss, comp);

        Queries = new StatsQueryService(_storeConcrete, _rateFn, _costFn,
            () => _pricingConcrete.EffectiveContext, () => _pricingConcrete.Revision, Bus, _clock);
        Exporter = new ExportService(_storeConcrete, _rateFn, _costFn,
            () => Config.Settings.OffsetMin, Path.Combine(_dataDir, "export"), _clock);
        Calibration = new CalibrationService(_storeConcrete, _rateFn, _costFn,
            () => _coordinator.RebuildToday(), _dataDir, Bus, _clock);
        Import = new ImportService(_storeConcrete, _configConcrete, _pricingConcrete, _rateFn, _costFn,
            () => _coordinator.RebuildToday(), _dataDir, Bus, _clock);

        Proxy = new ProxyEngine(() => Config.Proxy, _coordinator, new UsageParser(),
            new ProxyOptions { SseReadCap = _options.SseReadCap }, _clock);
        // IProxyEngine.StateChanged 转发为总线事件（托盘 SetStatus、面板状态条）
        Proxy.StateChanged += (_, e) => Bus.PublishCoalesced(new ProxyStateChangedEvent(e.IsListening, e.ListenAddr, e.Error));
        // 倍率/计价变更 → 后台重算 daily → 整桶重建 → 广播（D8-1：无 400ms sleep，锁自然串行）
        _pricingConcrete.Changed += OnPricingChanged;

        _ticker = new StatsTicker(_coordinator, Bus, _options.PanelTickInterval, _options.BallTickInterval);
        _dayWatch = new DayWatch(_coordinator, OnRollover, () => Config.Settings.OffsetMin, _options.DayWatchInterval, _clock);
    }

    private void OnPricingChanged(object? sender, EventArgs e)
    {
        // 写锁已释放（§8.5 禁止持锁发布）；重算放后台，主流程不受影响（§7.3 任务级兜底）
        _ = Task.Run(() =>
        {
            try
            {
                Store.RecalcDerived(_rateFn, _costFn);
                _coordinator.InvalidateMissedCache();
                Queries.Invalidate();
                _coordinator.RebuildToday();
                Bus.PublishCoalesced(new ConfigChanged(ConfigSectionFlags.Pricing));
            }
            catch (Exception ex)
            {
                Logger.Warn("Stats", $"计价变更重算失败: {ex.Message}");
            }
        });
    }

    private void OnRollover(string utcDate, string localDate, int offsetMin)
    {
        Bus.PublishCoalesced(new DayRolledOver(utcDate, localDate, offsetMin)); // RebuildToday 完成后发布（不持锁）
    }

    // —— 启动 / 退出（§7）——

    public Task StartAsync(CancellationToken ct)
    {
        lock (_stateGate)
        {
            if (_started) throw new InvalidOperationException("MonitorEngine 已启动（全生命周期仅一次）");
            if (_stopped) throw new InvalidOperationException("MonitorEngine 已停止");
            // 1-7 步骤中的配置/存储/服务装配已在构造完成；此处完成运行期引导
            _started = true;
        }
        Logger.Info("App", $"MonitorEngine 启动，数据目录: {_dataDir}");

        // 8. 历史重算（后台一次，修正历史/导入遗留；完成前 UI 可先渲染旧快照）
        _startupRecalcTask = Task.Run(() =>
        {
            try
            {
                Store.RecalcDerived(_rateFn, _costFn);
            }
            catch (Exception ex)
            {
                Logger.Warn("Storage", $"启动历史重算失败: {ex.Message}");
            }
        }, ct);

        // 9. 恢复今日桶（UTC 从 usage_daily 整桶替换 + Local 从 usage_log 重放 + 历史骨架）
        _coordinator.RebuildToday();
        Bus.PublishCoalesced(new DayRolledOver(
            TimeMath.UtcDate(_clock.GetUtcNow().ToUnixTimeSeconds()),
            TimeMath.LocalDate(_clock.GetUtcNow().ToUnixTimeSeconds(), Config.Settings.OffsetMin),
            Config.Settings.OffsetMin));

        // 10. 代理监听：失败 → ProxyStateChanged(false, addr, error)，进程继续运行（托盘/面板可用）
        try
        {
            Proxy.Start(Config.Proxy.ListenAddr);
        }
        catch (ProxyStartException ex)
        {
            Logger.Error("Proxy", $"代理启动失败（进程继续运行，可在改配置后『重载配置』）: {ex.Message}", ex);
        }

        // 11. 定时器：StatsTicker（200ms/1s）+ DayWatch（30s）
        _ticker.Start();
        _dayWatch.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        lock (_stateGate)
        {
            if (_stopped) throw new InvalidOperationException("MonitorEngine 已停止（全生命周期仅一次）");
            if (!_started) throw new InvalidOperationException("MonitorEngine 尚未启动");
            _stopped = true;
        }
        Logger.Info("App", "MonitorEngine 停止中");
        // 1. 停定时器（先断 UI 数据流）
        _ticker.Dispose();
        _dayWatch.Dispose();
        // 2. 代理优雅停止（等待在途捕获至多 10s → 强制兜底 [S5]）
        try
        {
            Proxy.Stop(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            Logger.Warn("Proxy", $"代理停止异常: {ex.Message}");
        }
        // 3. Flush + WAL checkpoint
        try
        {
            Store.Flush();
            Store.CheckpointWal();
        }
        catch (Exception ex)
        {
            Logger.Warn("Storage", $"退出落盘异常: {ex.Message}");
        }
        // 4. ui_state 防抖队列强制落盘
        try
        {
            _configConcrete.FlushUiState();
        }
        catch (Exception ex)
        {
            Logger.Warn("Config", $"退出保存 ui_state 失败: {ex.Message}");
        }
        // 6. Store 连接关闭（Dispose 由容器/调用方经 IAsyncDisposable 统一收口）
        Logger.Info("App", "MonitorEngine 已停止");
        return Task.CompletedTask;
    }

    // —— 命令（§8.2 协议：锁内快照交换 + DB 回放；重活在 Store/Accumulator 锁上自然串行）——

    public void SetTimezone(int offsetMin)
    {
        if (offsetMin is < -720 or > 840 || offsetMin % 30 != 0)
            throw new ArgumentException($"offset_min 非法（[-720,840] 且 30 分钟步进）: {offsetMin}", nameof(offsetMin));
        Config.SaveSettings(Config.Settings with { OffsetMin = offsetMin });
        Pricing.SetEffectiveContext(Config.Settings.EffectiveDateMode, offsetMin); // 写锁内换 offset、清平移缓存 [S3]
        _coordinator.InvalidateMissedCache();
        _coordinator.RebuildToday();                                               // 串行锁内 Flush→按新 offset 换桶 [C5]
        Queries.Invalidate();
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        Bus.PublishCoalesced(new DayRolledOver(TimeMath.UtcDate(now), TimeMath.LocalDate(now, offsetMin), offsetMin));
        Bus.PublishCoalesced(new ConfigChanged(ConfigSectionFlags.Settings));
        Logger.Info("App", $"本地统计时区切换为 {_sys.OffsetLabel(offsetMin)}");
    }

    public void SetEffectiveDateMode(string mode)
    {
        var normalized = mode != "local" ? "utc" : "local"; // 原 setEffectiveDateMode 防御保持
        if (normalized == Config.Settings.EffectiveDateMode) return; // 与现值相同 → 直接返回
        Config.SaveSettings(Config.Settings with { EffectiveDateMode = normalized });
        Pricing.SetEffectiveContext(normalized, Config.Settings.OffsetMin);
        // 生效日期日历变了 → daily 必须重算（后台；版本生效日期选择变化的落点）
        _ = Task.Run(() =>
        {
            try
            {
                Store.RecalcDerived(_rateFn, _costFn);
            }
            catch (Exception ex)
            {
                Logger.Warn("Storage", $"口径变更重算失败: {ex.Message}");
            }
        });
        _coordinator.InvalidateMissedCache();
        _coordinator.RebuildToday();
        Queries.Invalidate();
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        Bus.PublishCoalesced(new DayRolledOver(TimeMath.UtcDate(now), TimeMath.LocalDate(now, Config.Settings.OffsetMin), Config.Settings.OffsetMin));
        Bus.PublishCoalesced(new ConfigChanged(ConfigSectionFlags.Settings | ConfigSectionFlags.Pricing));
        Logger.Info("App", $"生效日期口径切换为 {normalized}");
    }

    public void ReloadProxyConfig()
    {
        Config.ReloadProxy(); // 重读 config.json → ValidateAndRepair → 更新路由表
        var addr = Config.Proxy.ListenAddr;
        if (!string.Equals(addr, Proxy.ListenAddr, StringComparison.Ordinal))
        {
            // 地址变更 → Stop/Start（单进程内重建监听，不再整进程重启）
            try
            {
                Proxy.Stop(TimeSpan.FromSeconds(3));
                Proxy.Start(addr);
            }
            catch (ProxyStartException ex)
            {
                Logger.Error("Proxy", $"重载配置后启动监听失败: {ex.Message}", ex);
            }
        }
        else
        {
            // 地址未变 → 热换路由表（Func<ProxyConfig> 每请求取最新，无需重建）
            Logger.Info("Proxy", "配置已重载（监听地址未变，路由表热更新）");
        }
        Bus.PublishCoalesced(new ConfigChanged(ConfigSectionFlags.Proxy));
    }

    public void ResetToday(string? modelKey)
    {
        Store.ResetToday(modelKey, Config.Settings.OffsetMin);
        Store.LogOp(modelKey ?? "all", "reset_today", $"重置今日 {modelKey ?? "全部"}", "");
        _coordinator.InvalidateMissedCache();
        _coordinator.RebuildToday();
        Queries.Invalidate();
        Bus.PublishCoalesced(new ConfigChanged(ConfigSectionFlags.Settings));
    }

    public void DeleteModelData(string modelKey)
    {
        Store.DeleteModelData(modelKey);
        Store.LogOp(modelKey, "delete_model_data", $"删除模型全部数据 {modelKey}", "");
        _coordinator.InvalidateMissedCache();
        _coordinator.RebuildToday();
        Queries.Invalidate();
        Bus.PublishCoalesced(new ConfigChanged(ConfigSectionFlags.Settings));
    }

    public void DeleteAllData()
    {
        Store.DeleteAllData();
        Store.LogOp("all", "delete_all_data", "删除全部数据", "");
        _coordinator.InvalidateMissedCache();
        _coordinator.RebuildToday();
        Queries.Invalidate();
        Bus.PublishCoalesced(new ConfigChanged(ConfigSectionFlags.Settings));
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_started && !_stopped) await StopAsync();
        }
        catch (InvalidOperationException)
        {
            // 未启动即释放：直接清理
        }
        _dayWatch.Dispose();
        _ticker.Dispose();
        _storeConcrete.Dispose();
        _configConcrete.Dispose();
        _sys.Dispose();
    }
}
