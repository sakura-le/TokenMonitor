using TokenMonitor.Core.Events;
using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Proxy;
using TokenMonitor.Core.Storage;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Stats;

/// <summary>
/// 摄入协调器（[C5] 修复的落点，01-§2.3.9）：一把串行锁保护"报价→累加→落库→文件日志"
/// 与"整桶重建"。重建期间 Ingest 因锁排队，恢复后自然落新桶——无间隙、无重复、无死锁。
/// </summary>
public sealed class UsageCoordinator : IUsageIngestor
{
    private readonly object _gate = new();
    private readonly IAccumulator _acc;
    private readonly IStore _store;
    private readonly IUsageFileLogger _fileLog;
    private readonly IPricingEngine _pricing;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _missedCacheTtl;
    private readonly object _missedCacheGate = new();
    private IReadOnlyDictionary<string, long>? _missedCache;
    private long _missedCacheAt; // 单调时间戳（TTL 判定）

    public UsageCoordinator(IAccumulator acc, IStore store, IUsageFileLogger fileLog,
                            IPricingEngine pricing, IEventBus bus, TimeProvider? clock = null,
                            TimeSpan? missedCacheTtl = null)
    {
        _acc = acc;
        _store = store;
        _fileLog = fileLog;
        _pricing = pricing;
        _clock = clock ?? TimeProvider.System;
        _missedCacheTtl = missedCacheTtl ?? TimeSpan.FromSeconds(1);
        _ = bus; // 事件发布职责归 Engine（RebuildToday 返回后由调用方广播，§8.5 禁止持锁发布）
    }

    /// <summary>锁内：acc.AddUsage（内部报价）→ store.AddUsage(单行) → fileLog.Log。
    /// 全捕获：内部异常 → Warn 日志 + store.AddMissed("ingest error")，绝不向代理线程抛出。</summary>
    public void Ingest(UsageEvent evt)
    {
        lock (_gate)
        {
            try
            {
                var key = evt.Key.ToString();
                // 首次出现的模型 → 记录操作日志（原 processEvent 语义）
                if (!_acc.HasModel(evt.Provider, evt.Model))
                    _store.LogOp(key, "model_created", $"首次出现模型 {evt.Model}", "");
                var result = _acc.AddUsage(evt);
                IngestMidpointHookForTest?.Invoke(); // 测试钩子：锁内中点（C5b 确定性交错）
                var u = evt.Usage;
                _store.AddUsage([new UsageLogRow(
                    evt.CompletedAtUnix, evt.Provider, evt.Model,
                    u.PromptTokens, u.CacheHitTokens, u.CacheMissTokens,
                    u.CompletionTokens, u.ReasoningTokens, u.TotalTokens,
                    result.MulTotal, result.MulPrompt, result.MulCacheHit,
                    result.MulCacheMiss, result.MulCompletion, result.MulReasoning,
                    result.CostCNY, result.CostUSD)]);
                _fileLog.Log(evt, result.CostCNY, result.CostUSD);
            }
            catch (Exception ex)
            {
                Logger.Warn("Stats", $"摄入失败: {ex.Message}");
                try
                {
                    _store.AddMissed(new MissedLogRow(0, _clock.GetUtcNow().ToUnixTimeSeconds(),
                        evt.Provider, evt.Model, 0, "ingest error"));
                }
                catch (Exception ex2)
                {
                    Logger.Warn("Stats", $"漏抓登记失败: {ex2.Message}");
                }
            }
        }
    }

    /// <summary>锁外直写 store.AddMissed（小量、独立）。</summary>
    public void ReportMissed(MissedCapture capture)
    {
        _store.AddMissed(new MissedLogRow(0, capture.Ts, capture.Provider, capture.Model, capture.Status, capture.Reason));
    }

    /// <summary>
    /// 整桶重建（§3.6 协议）：Flush → UTC 今日（GetTodayUtc）整桶替换 → 本地今日（GetLogsByRange）
    /// 重放 Local 桶 → 补历史模型骨架。重建期间 Ingest 在锁上排队 [C5]。
    /// </summary>
    public void RebuildToday()
    {
        lock (_gate)
        {
            _store.Flush(); // 把缓冲落库，恢复窗口数据完整
            var now = _clock.GetUtcNow().ToUnixTimeSeconds();
            var (_, offset) = _pricing.EffectiveContext;
            var utcRows = _store.GetTodayUtc();
            _acc.ReplaceUtcBucket(utcRows);
            var localTodayStart = TimeMath.LocalDayStart(TimeMath.LocalDate(now, offset), offset);
            var logs = _store.GetLogsByRange(localTodayStart, localTodayStart + 86400);
            _acc.ReplaceLocalBucket(logs.Select(l => new LocalReplayRow(
                l.Ts, l.Provider, l.Model,
                new UnifiedUsage(l.PromptTokens, l.CacheHitTokens, l.CacheMissTokens,
                    l.CompletionTokens, l.ReasoningTokens,
                    l.TotalTokens > 0 ? l.TotalTokens : l.PromptTokens + l.CompletionTokens))).ToList());
            foreach (var (provider, model) in _store.GetAllModels())
                _acc.SeedModelSkeleton(provider, model);
        }
    }

    /// <summary>快照 = acc.Snapshot(missedByModel)；missedByModel 经 TTL 缓存查询（本地日窗口漏抓计数）[S7 同向]。</summary>
    public StatsSnapshot BuildSnapshot()
    {
        var missed = GetMissedByModelCached();
        var (_, offset) = _pricing.EffectiveContext;
        var snapshot = _acc.Snapshot(missed);
        return snapshot with { OffsetMin = offset, CreatedAtUnix = _clock.GetUtcNow().ToUnixTimeSeconds() };
    }

    private IReadOnlyDictionary<string, long> GetMissedByModelCached()
    {
        var nowMonotonic = Environment.TickCount64;
        lock (_missedCacheGate)
        {
            if (_missedCache is not null && nowMonotonic - _missedCacheAt < _missedCacheTtl.TotalMilliseconds)
                return _missedCache;
        }
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var (_, offset) = _pricing.EffectiveContext;
        IReadOnlyDictionary<string, long> fresh;
        long start = 0;
        try
        {
            start = TimeMath.LocalDayStart(TimeMath.LocalDate(now, offset), offset);
            fresh = _store.GetMissedCountByModel(start, start + 86400);
        }
        catch (Exception ex)
        {
            Logger.Warn("Stats", $"查询漏抓计数失败: {ex.Message}");
            fresh = new Dictionary<string, long>();
        }
        lock (_missedCacheGate)
        {
            _missedCache = fresh;
            _missedCacheAt = nowMonotonic;
        }
        return fresh;
    }

    /// <summary>重置/删除/补录/导入后使漏抓计数缓存立即失效（数据变更必须即时可见）。</summary>
    public void InvalidateMissedCache()
    {
        lock (_missedCacheGate)
        {
            _missedCache = null;
        }
    }

    /// <summary>测试钩子（C5b 确定性交错）：Ingest 中点（锁内、AddUsage 与落库之间）回调。</summary>
    internal Action? IngestMidpointHookForTest { get; set; }

}
