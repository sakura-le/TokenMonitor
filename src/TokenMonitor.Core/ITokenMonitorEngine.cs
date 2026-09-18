namespace TokenMonitor.Core;

/// <summary>App 唯一门面契约（01-§2.3.9）。StartAsync/StopAsync 全生命周期只允许调用一次（重复抛 InvalidOperationException）。</summary>
public interface ITokenMonitorEngine : IAsyncDisposable
{
    Config.IConfigService Config { get; }
    Pricing.IPricingEngine Pricing { get; }
    Storage.IStore Store { get; }
    Proxy.IProxyEngine Proxy { get; }
    Events.IEventBus Bus { get; }
    Stats.IStatsQueryService Queries { get; }
    Storage.IExportService Exporter { get; }
    Storage.ICalibrationService Calibration { get; }
    Storage.IImportService Import { get; }

    /// <summary>§7.1 启动序列（除 UI/托盘步骤）。</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>§7.2 退出序列（除 UI/托盘步骤）。</summary>
    Task StopAsync();

    // —— 以下命令均：重活后台/串行执行 + 完成后经总线广播；可在 UI 线程直接调用 ——

    /// <summary>存 settings → pricing.SetEffectiveContext → RebuildToday → DayRolledOver+ConfigChanged（[C5] 协议）。</summary>
    void SetTimezone(int offsetMin);

    /// <summary>同上 + store.RecalcDerived（生效日期选择变了，daily 需重算）。</summary>
    void SetEffectiveDateMode(string mode);

    /// <summary>ReloadProxy → 地址变更则 Stop/Start，否则热换路由表 → ConfigChanged(Proxy)。</summary>
    void ReloadProxyConfig();

    /// <summary>store.ResetToday → RebuildToday → ConfigChanged。</summary>
    void ResetToday(string? modelKey);

    /// <summary>store.DeleteModelData → RebuildToday → ConfigChanged。</summary>
    void DeleteModelData(string modelKey);

    /// <summary>store.DeleteAllData → RebuildToday → ConfigChanged。</summary>
    void DeleteAllData();
}

/// <summary>引擎可注入参数（测试确定性：目录/时间/各定时器间隔可注入）。</summary>
public sealed class MonitorEngineOptions
{
    /// <summary>数据目录（null → exe 旁 data/）。</summary>
    public string? DataDir { get; init; }

    /// <summary>时间源（IClock 抽象落点：.NET 内建 TimeProvider，测试可注入固定时钟）。</summary>
    public TimeProvider? TimeProvider { get; init; }

    public TimeSpan StoreFlushInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan DayWatchInterval { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan PanelTickInterval { get; init; } = TimeSpan.FromMilliseconds(200);
    public TimeSpan BallTickInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MissedCountTtl { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>SSE 读取上限（[C19-③]，默认 30 分钟；测试注入短时限）。</summary>
    public TimeSpan SseReadCap { get; init; } = TimeSpan.FromMinutes(30);
}
