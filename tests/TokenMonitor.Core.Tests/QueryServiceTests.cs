using TokenMonitor.Core.Events;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Stats;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>查询服务回归：C7/C9/S7/C17。</summary>
public class QueryServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly Store _store;
    private readonly EventBus _bus = new();
    private readonly TestClock _queryClock = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));

    public QueryServiceTests() => _store = new Store(_dir.Path, _clock, flushInterval: TimeSpan.FromMilliseconds(50));

    public void Dispose() => _store.Dispose();

    private (StatsQueryService Svc, Func<int> OffsetGet, Action OffsetSet480) NewService()
    {
        // offset 可变上下文（模拟 SetTimezone 前后）
        var holder = new Holder { Offset = 0 };
        var svc = new StatsQueryService(_store, null, null,
            () => ("utc", holder.Offset), () => 1, _bus, _queryClock);
        return (svc, () => holder.Offset, () => holder.Offset = 480);
    }

    private sealed class Holder { public int Offset; }

    // —— [C9] Local 口径范围边界 ——

    [Fact]
    public void Query_GetRange_LocalScope_BoundaryAttributionUtcPlus8()
    {
        // [C9] UTC 2026-08-31 16:30 与 17:30 各一笔；GetRange("2026-09-01","2026-09-01",Local,offset=480)
        // → 两笔均落入 09-01（UTC 口径查 09-01 则不含任何一笔）
        var t1 = TimeMath.UtcDayStart("2026-08-31") + 16 * 3600 + 30 * 60;
        var t2 = TimeMath.UtcDayStart("2026-08-31") + 17 * 3600 + 30 * 60;
        _store.AddUsage(
        [
            new UsageLogRow(t1, "P", "m", 10, 0, 10, 5, 0, 15),
            new UsageLogRow(t2, "P", "m", 10, 0, 10, 5, 0, 15),
        ]);
        _store.Flush();
        var (svc, _, setOffset) = NewService();
        setOffset(); // offset=480

        var local = svc.GetRange("2026-09-01", "2026-09-01", BucketScope.Local);
        Assert.Single(local);      // 两笔同 (date,provider,model) 聚合为一行
        Assert.Equal("2026-09-01", local[0].Date);
        Assert.Equal(30, local[0].TotalTokens); // 两笔合计
        Assert.Equal(2, local[0].RequestCount);

        var utc = svc.GetRange("2026-09-01", "2026-09-01", BucketScope.Utc);
        Assert.Empty(utc); // 两笔 UTC 日期均为 08-31，不落入 09-01
    }

    [Fact]
    public void Query_GetRecentDays_LocalWindow_ExactlyDays()
    {
        // [C7] Local 口径恰 N 个日历日
        var holder = new Holder { Offset = 480 };
        var svc = new StatsQueryService(_store, null, null,
            () => ("utc", holder.Offset), () => 1, _bus, _queryClock);
        // 本地今日为 2026-09-18（12:00Z = 20:00 本地）；造 8 个本地日数据
        for (var k = 0; k < 8; k++)
        {
            var localDate = TimeMath.LocalDate(_queryClock.NowUnix - k * 86400, 480);
            var ts = TimeMath.LocalDayStart(localDate, 480) + 6 * 3600; // 本地 06:00
            _store.AddUsage([new UsageLogRow(ts, "P", $"m{k}", 10, 0, 10, 5, 0, 15)]);
        }
        _store.Flush();
        var result = svc.GetRecentDays(7, BucketScope.Local);
        Assert.Equal(7, result.Rows.Select(r => r.Date).Distinct().Count());
        Assert.Equal(7, result.RequestedDays);
    }

    // —— [S7] 查询缓存 ——

    [Fact]
    public async Task QueryCache_ResultsCached_InvalidatedByEvents()
    {
        // [S7] 同参两次查询命中缓存（查询计数=1）；Invalidate/事件后重查
        var counting = new CountingStore(_store);
        var svc = new StatsQueryService(counting, null, null, () => ("utc", 0), () => 1, _bus, _queryClock);

        _ = svc.GetRecentDays(7, BucketScope.Utc);
        _ = svc.GetRecentDays(7, BucketScope.Utc);
        Assert.Equal(1, counting.RecentDaysCalls); // 第二次命中缓存

        svc.Invalidate();
        _ = svc.GetRecentDays(7, BucketScope.Utc);
        Assert.Equal(2, counting.RecentDaysCalls);

        // 事件失效（CalibrateCompleted，BackgroundPool 异步分发 → 轮询等待生效）
        _bus.Publish(new CalibrateCompleted("x"));
        await TestHelpers.WaitForAsync(() =>
        {
            _ = svc.GetRecentDays(7, BucketScope.Utc);
            return counting.RecentDaysCalls >= 3;
        }, TimeSpan.FromSeconds(5), "事件失效未生效");
        Assert.True(counting.RecentDaysCalls >= 3);
    }

    [Fact]
    public async Task QueryCache_KeyIncludesOffsetAndRevision()
    {
        // 缓存键含 offsetMin/pricingRevision：offset 变化天然不命中旧键（正确性）
        var counting = new CountingStore(_store);
        var holder = new Holder { Offset = 0 };
        var svc = new StatsQueryService(counting, null, null,
            () => ("utc", holder.Offset), () => 1, _bus, _queryClock);
        _ = svc.GetRange("2026-09-01", "2026-09-30", BucketScope.Local);
        _ = svc.GetRange("2026-09-01", "2026-09-30", BucketScope.Local);
        Assert.Equal(1, counting.AggregateLocalCalls);
        holder.Offset = 480; // SetTimezone 落点
        _ = svc.GetRange("2026-09-01", "2026-09-30", BucketScope.Local);
        Assert.Equal(2, counting.AggregateLocalCalls); // 新键 → 重新查询
    }

    // —— [C17] 时区/口径变更失效 ——

    [Fact]
    public void Events_TimezoneChange_InvalidatesCaches()
    {
        // [C17] 查询缓存已填充 → offset 变更 → 再查返回新 offset 结果（事件广播断言在 EngineTests）
        var t1 = TimeMath.UtcDayStart("2026-08-31") + 16 * 3600 + 30 * 60; // 本地 09-01 00:30（+8）
        _store.AddUsage([new UsageLogRow(t1, "P", "m", 10, 0, 10, 5, 0, 15)]);
        _store.Flush();
        var (svc, _, setOffset) = NewService();

        var before = svc.GetRange("2026-09-01", "2026-09-01", BucketScope.Local);
        Assert.Empty(before); // offset=0：本地日=UTC 日 → 不含该行

        setOffset(); // offset=480（等价 SetTimezone 的上下文变更 + Invalidate）
        svc.Invalidate();

        var after = svc.GetRange("2026-09-01", "2026-09-01", BucketScope.Local);
        Assert.Single(after); // 新 offset 下该行归入 09-01
        Assert.Equal(15, after[0].TotalTokens);
    }

    private sealed class CountingStore : IStore
    {
        private readonly IStore _inner;
        public int RecentDaysCalls;
        public int AggregateLocalCalls;

        public CountingStore(IStore inner) => _inner = inner;

        public void AddUsage(IReadOnlyList<UsageLogRow> rows) => _inner.AddUsage(rows);
        public void Flush() => _inner.Flush();
        public void AddMissed(MissedLogRow m) => _inner.AddMissed(m);
        public void LogOp(string model, string action, string detail, string effectiveFrom) => _inner.LogOp(model, action, detail, effectiveFrom);
        public void InsertEstimatedUsage(IReadOnlyList<UsageLogRow> rows) => _inner.InsertEstimatedUsage(rows);
        public IReadOnlyList<ModelDailyAggregate> GetTodayUtc() => _inner.GetTodayUtc();
        public IReadOnlyList<ModelDailyAggregate> QueryDailyRange(string startUtc, string endUtc) => _inner.QueryDailyRange(startUtc, endUtc);

        public IReadOnlyList<ModelDailyAggregate> GetRecentDaysUtc(int days)
        {
            Interlocked.Increment(ref RecentDaysCalls);
            return _inner.GetRecentDaysUtc(days);
        }

        public IReadOnlyList<UsageLogRow> GetLogsByRange(long startTs, long endExclusiveTs) => _inner.GetLogsByRange(startTs, endExclusiveTs);

        public IReadOnlyList<ModelDailyAggregate> AggregateLocal(long startTs, long endExclusiveTs, int offsetMin, RateFunc? rateFn, CostFunc? costFn)
        {
            Interlocked.Increment(ref AggregateLocalCalls);
            return _inner.AggregateLocal(startTs, endExclusiveTs, offsetMin, rateFn, costFn);
        }

        public IReadOnlyList<HourlyAggregate> AggregateHourly(long startTs, long endExclusiveTs, int offsetMin, RateFunc? rateFn, CostFunc? costFn) => _inner.AggregateHourly(startTs, endExclusiveTs, offsetMin, rateFn, costFn);
        public IReadOnlyList<MissedLogRow> GetMissed(long startTs, long endExclusiveTs) => _inner.GetMissed(startTs, endExclusiveTs);
        public IReadOnlyDictionary<string, long> GetMissedCountByModel(long startTs, long endExclusiveTs) => _inner.GetMissedCountByModel(startTs, endExclusiveTs);
        public IReadOnlyList<OpLogRow> GetOpLogs(string? modelKey) => _inner.GetOpLogs(modelKey);
        public IReadOnlyList<(string Provider, string Model)> GetAllModels() => _inner.GetAllModels();
        public void RecalcDerived(RateFunc? rateFn, CostFunc? costFn) => _inner.RecalcDerived(rateFn, costFn);
        public void ResetToday(string? modelKey, int offsetMin) => _inner.ResetToday(modelKey, offsetMin);
        public void DeleteModelData(string modelKey) => _inner.DeleteModelData(modelKey);
        public void DeleteAllData() => _inner.DeleteAllData();
        public string Backup() => _inner.Backup();
        public void ReplaceDatabase(string backupPath) => _inner.ReplaceDatabase(backupPath);
        public void CheckpointWal() => _inner.CheckpointWal();
        public void Dispose() => _inner.Dispose();
    }
}
