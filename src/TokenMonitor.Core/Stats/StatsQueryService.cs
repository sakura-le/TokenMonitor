using TokenMonitor.Core.Events;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Stats;

/// <summary>读侧门面契约（01-§2.3.8）：按需查询 + 结果缓存 [S7]（缓存键含参数与 scope；事件失效）。</summary>
public interface IStatsQueryService
{
    /// <summary>近 N 天逐日聚合。两口径均取恰好 days 个日历日 [C7]；Local 用 offset 换算日界。</summary>
    RecentDaysResult GetRecentDays(int days, BucketScope scope);

    /// <summary>日期范围逐日聚合。[C9]：startDate/endDate 按 scope 口径解释——Local 时换算为 ts 窗口（本地 00:00-offset）再查。</summary>
    IReadOnlyList<ModelDailyAggregate> GetRange(string startDate, string endDate, BucketScope scope);

    /// <summary>本地日历某日的小时分段（offset 当前值）。</summary>
    IReadOnlyList<HourlyAggregate> GetHourly(string localDate);

    IReadOnlyList<OpLogRow> GetOpLogs(string? modelKey);

    IReadOnlyList<(string Provider, string Model)> GetAllModels();

    /// <summary>查询缓存失效（数据变更后由 Engine 同步调用，保证确定性；事件订阅为补充）。</summary>
    void Invalidate();
}

/// <summary>
/// IStatsQueryService 默认实现。缓存键含 (方法, 参数, scope, offsetMin, pricingRevision, utcToday)
/// ——offset/口径/文档变更天然不命中旧键（正确性），事件订阅负责清空字典（内存上界）[S7]。
/// 替代原版每 5 秒全年聚合重扫。
/// </summary>
public sealed class StatsQueryService : IStatsQueryService
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, object> _cache = new(StringComparer.Ordinal);
    private readonly IStore _store;
    private readonly RateFunc? _rateFn;
    private readonly CostFunc? _costFn;
    private readonly Func<(string Mode, int OffsetMin)> _effContext;
    private readonly Func<long> _pricingRevision;
    private readonly TimeProvider _clock;
    private readonly IDisposable[] _subscriptions;

    public StatsQueryService(IStore store, RateFunc? rateFn, CostFunc? costFn,
                             Func<(string Mode, int OffsetMin)> effContext, Func<long> pricingRevision,
                             IEventBus bus, TimeProvider? clock = null)
    {
        _store = store;
        _rateFn = rateFn;
        _costFn = costFn;
        _effContext = effContext;
        _pricingRevision = pricingRevision;
        _clock = clock ?? TimeProvider.System;
        _subscriptions =
        [
            bus.Subscribe<ConfigChanged>(_ => Invalidate()),
            bus.Subscribe<DayRolledOver>(_ => Invalidate()),
            bus.Subscribe<CalibrateCompleted>(_ => Invalidate()),
            bus.Subscribe<ImportCompleted>(_ => Invalidate()),
        ];
    }

    public RecentDaysResult GetRecentDays(int days, BucketScope scope)
    {
        if (days <= 0) days = 1;
        var cacheKey = $"recent:{days}:{scope}:{CacheContextKey()}";
        if (GetCache<RecentDaysResult>(cacheKey) is { } cached) return cached;
        IReadOnlyList<ModelDailyAggregate> rows;
        if (scope == BucketScope.Utc)
        {
            rows = _store.GetRecentDaysUtc(days);
        }
        else
        {
            var now = _clock.GetUtcNow().ToUnixTimeSeconds();
            var (_, offset) = _effContext();
            var localToday = TimeMath.LocalDate(now, offset);
            var startDate = TimeMath.LocalDate(TimeMath.LocalDayStart(localToday, offset) - (days - 1) * 86400L, offset);
            var startTs = TimeMath.LocalDayStart(startDate, offset);
            var endEx = TimeMath.LocalDayStart(localToday, offset) + 86400;
            rows = _store.AggregateLocal(startTs, endEx, offset, _rateFn, _costFn);
        }
        var result = new RecentDaysResult(rows, days);
        SetCache(cacheKey, result);
        return result;
    }

    public IReadOnlyList<ModelDailyAggregate> GetRange(string startDate, string endDate, BucketScope scope)
    {
        var cacheKey = $"range:{startDate}:{endDate}:{scope}:{CacheContextKey()}";
        if (GetCache<IReadOnlyList<ModelDailyAggregate>>(cacheKey) is { } cached) return cached;
        IReadOnlyList<ModelDailyAggregate> rows;
        if (scope == BucketScope.Utc)
        {
            // 直接按 UTC 日期列，闭区间（usage_daily 即 UTC 桶）
            rows = _store.QueryDailyRange(startDate, endDate);
        }
        else
        {
            // [C9] Local 口径：本地 00:00 − offset 换算 ts 窗口再聚合（原 UI 标 "(UTC)" 实按本地换算的错位根因）
            var (_, offset) = _effContext();
            var startTs = TimeMath.LocalDayStart(startDate, offset);
            var endEx = TimeMath.LocalDayStart(endDate, offset) + 86400;
            rows = _store.AggregateLocal(startTs, endEx, offset, _rateFn, _costFn);
        }
        SetCache(cacheKey, rows);
        return rows;
    }

    public IReadOnlyList<HourlyAggregate> GetHourly(string localDate)
    {
        var cacheKey = $"hourly:{localDate}:{CacheContextKey()}";
        if (GetCache<IReadOnlyList<HourlyAggregate>>(cacheKey) is { } cached) return cached;
        var (_, offset) = _effContext();
        var startTs = TimeMath.LocalDayStart(localDate, offset);
        var rows = _store.AggregateHourly(startTs, startTs + 86400, offset, _rateFn, _costFn);
        SetCache(cacheKey, rows);
        return rows;
    }

    public IReadOnlyList<OpLogRow> GetOpLogs(string? modelKey) => _store.GetOpLogs(modelKey);

    public IReadOnlyList<(string Provider, string Model)> GetAllModels() => _store.GetAllModels();

    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _cache.Clear();
        }
    }

    private string CacheContextKey() => $"{_effContext().OffsetMin}:{_pricingRevision()}";

    private T? GetCache<T>(string key)
    {
        lock (_cacheLock)
        {
            return _cache.TryGetValue(key, out var v) && v is T typed ? typed : default;
        }
    }

    private void SetCache<T>(string key, T value)
    {
        lock (_cacheLock)
        {
            _cache[key] = value!;
        }
    }
}
