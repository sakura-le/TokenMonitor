using TokenMonitor.Core.Pricing;

namespace TokenMonitor.Core.Storage;

/// <summary>usage_daily/聚合结果共用的日聚合行（列语义与 01-§4 DDL 一一对应）。</summary>
public sealed record ModelDailyAggregate(
    string Date, string Provider, string Model,
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,
    long RequestCount,
    long MulTotal, long MulPrompt, long MulCacheHit, long MulCacheMiss, long MulCompletion, long MulReasoning,
    double CostCNY, double CostUSD);

/// <summary>本地口径小时分段聚合（对齐官方 usage/cost 导出模板）。</summary>
public sealed record HourlyAggregate(
    string Date, int Hour, string Provider, string Model,
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,
    long RequestCount, double CostCNY, double CostUSD);

/// <summary>usage_log 原始行（写入与明细读取共用；Mul*/Cost* 仅写路径使用，usage_log 表不存这些列）。</summary>
public sealed record UsageLogRow(
    long Ts, string Provider, string Model,
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,
    long MulTotal = 0, long MulPrompt = 0, long MulCacheHit = 0,
    long MulCacheMiss = 0, long MulCompletion = 0, long MulReasoning = 0,
    double CostCNY = 0, double CostUSD = 0,
    bool Estimated = false, long? ImportBatch = null);

/// <summary>usage_missed 行。</summary>
public sealed record MissedLogRow(long Id, long Ts, string Provider, string Model, int Status, string Reason);

/// <summary>op_log 行。</summary>
public sealed record OpLogRow(long Id, long Ts, string Model, string Action, string Detail, string EffectiveFrom);

/// <summary>计价回调（offsetMin=0 → UTC 口径；&gt;0 → Local 口径）。hour 为分数小时 [C4]。</summary>
public delegate double RateFunc(string modelKey, long ts, double hour, int offsetMin);

/// <summary>计价回调（同上；返回 (cny, usd)）。</summary>
public delegate (double Cny, double Usd) CostFunc(string modelKey, long ts, double hour, int offsetMin,
                                                  long cacheHit, long cacheMiss, long completion);

/// <summary>data/calibrate_state.json（沿用原文件名与字段）。</summary>
public sealed record CalibrateState(bool HasCalibrated, string LastBackup);

/// <summary>导入模式（§6.5）：同源已导入时 MergeIfNew 拒绝、ReplaceAll 清四表重导。</summary>
public enum ImportMode { MergeIfNew, ReplaceAll }

/// <summary>旧数据导入选项（G1）。</summary>
public sealed record ImportOptions(string LegacyDataDir, bool MigrateApiKeys, ImportMode Mode);

/// <summary>导入预览（不修改任何文件）。</summary>
public sealed record ImportPreview(
    bool LegacyDbFound, long EstimatedUsageRows, long EstimatedMissedRows, long EstimatedOpRows,
    bool ConfigFound, bool PricingFound, bool SettingsFound, bool MultiplierStatesFound,
    bool AlreadyImported, string? SourceSha256, IReadOnlyList<string> Warnings);

/// <summary>导入结果。</summary>
public sealed record ImportResult(
    bool Success, string? Error,
    long ImportedUsageRows, long ImportedMissedRows, long ImportedOpRows,
    bool ConfigImported, bool PricingImported, bool SettingsImported,
    string BackupPath);

/// <summary>XLSX 导出请求（RangeScope 决定日期的日历解释 [C9]/[C19-①]）。</summary>
public sealed record ExportRequest(string StartDate, string EndDate, BucketScope RangeScope,
                                   string? ModelKey, bool IncludeHourly);

/// <summary>XLSX 导出结果。</summary>
public sealed record ExportResult(bool Success, string? Error, string FilePath, int UtcRows, int LocalRows, int HourlyRows);

/// <summary>SQLite 持久化契约（01-§2.3.4）。全部成员线程安全；内部单连接 + 单锁串行。</summary>
public interface IStore : IDisposable
{
    // —— 写路径 ——
    /// <summary>加入批量缓冲（攒 3 条或 5 秒落盘，WAL）。落盘失败→整批回队头部重试，本方法不抛。</summary>
    void AddUsage(IReadOnlyList<UsageLogRow> rows);

    /// <summary>同步刷出缓冲（退出/备份/重算前必须调用）。</summary>
    void Flush();

    /// <summary>同步写一条漏抓（provider/model 为空则忽略）。不抛。</summary>
    void AddMissed(MissedLogRow m);

    /// <summary>同步写一条操作日志，随后裁剪保留最近 5000 条（读取恒 500）。不抛。</summary>
    void LogOp(string model, string action, string detail, string effectiveFrom);

    /// <summary>事务直写补录行（estimated=1；total 缺失按 prompt+completion 推导）。失败抛 StorageException 并整批回滚。</summary>
    void InsertEstimatedUsage(IReadOnlyList<UsageLogRow> rows);

    // —— 查询（失败抛 StorageException）——
    IReadOnlyList<ModelDailyAggregate> GetTodayUtc();
    IReadOnlyList<ModelDailyAggregate> QueryDailyRange(string startUtc, string endUtc);
    IReadOnlyList<ModelDailyAggregate> GetRecentDaysUtc(int days);
    IReadOnlyList<UsageLogRow> GetLogsByRange(long startTs, long endExclusiveTs);
    IReadOnlyList<ModelDailyAggregate> AggregateLocal(long startTs, long endExclusiveTs, int offsetMin,
                                                      RateFunc? rateFn, CostFunc? costFn);
    IReadOnlyList<HourlyAggregate> AggregateHourly(long startTs, long endExclusiveTs, int offsetMin,
                                                   RateFunc? rateFn, CostFunc? costFn);
    IReadOnlyList<MissedLogRow> GetMissed(long startTs, long endExclusiveTs);
    IReadOnlyDictionary<string, long> GetMissedCountByModel(long startTs, long endExclusiveTs);
    IReadOnlyList<OpLogRow> GetOpLogs(string? modelKey);
    IReadOnlyList<(string Provider, string Model)> GetAllModels();

    // —— 维护/重算 ——
    void RecalcDerived(RateFunc? rateFn, CostFunc? costFn);
    void ResetToday(string? modelKey, int offsetMin);
    void DeleteModelData(string modelKey);
    void DeleteAllData();
    string Backup();
    void ReplaceDatabase(string backupPath);
    void CheckpointWal();
}
