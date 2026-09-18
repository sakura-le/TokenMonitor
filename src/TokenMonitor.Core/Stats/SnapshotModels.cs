using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Stats;

/// <summary>单个模型的实时快照条目（不可变；UTC/Local 两桶结构相同）。</summary>
public sealed record ModelSnapshot(
    string Provider, string Model,
    long RequestCount,
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,
    long MulTotal, long MulPrompt, long MulCacheHit,
    long MulCacheMiss, long MulCompletion, long MulReasoning,
    double CostCNY, double CostUSD,
    long DeltaTokens,
    long LastActiveUnix,
    long MissedCount);

/// <summary>面板/悬浮球消费的完整不可变快照。</summary>
public sealed record StatsSnapshot(
    IReadOnlyList<ModelSnapshot> UtcModels,
    ModelSnapshot UtcSummary,
    IReadOnlyList<ModelSnapshot> LocalModels,
    ModelSnapshot LocalSummary,
    long MissedCaptures,
    IReadOnlyDictionary<string, long> MissedByModel,
    int OffsetMin,
    long CreatedAtUnix);

/// <summary>AddUsage 的返回：本次请求对 UTC 桶的增量（即写入 usage_daily/upsert 的倍率与成本）。</summary>
public sealed record AccumulateResult(
    long MulTotal, long MulPrompt, long MulCacheHit, long MulCacheMiss, long MulCompletion, long MulReasoning,
    double CostCNY, double CostUSD);

/// <summary>Local 桶重建时的原始行（来自 usage_log；Usage.TotalTokens 已按铁律 1+S2 统一）。</summary>
public sealed record LocalReplayRow(long Ts, string Provider, string Model, UnifiedUsage Usage);

/// <summary>近 N 天/范围查询结果（逐日明细；不含汇总，由调用方累加）。</summary>
public sealed record RecentDaysResult(IReadOnlyList<ModelDailyAggregate> Rows, int RequestedDays);

/// <summary>面板 200ms 快照事件（合并式发布，仅保留最新）。</summary>
public sealed record PanelStatsTick(StatsSnapshot Snapshot) : TokenMonitor.Core.Events.IEvent;

/// <summary>悬浮球 1s 快照事件（与 PanelStatsTick 同一快照实例复用）。</summary>
public sealed record BallStatsTick(StatsSnapshot Snapshot) : TokenMonitor.Core.Events.IEvent;
