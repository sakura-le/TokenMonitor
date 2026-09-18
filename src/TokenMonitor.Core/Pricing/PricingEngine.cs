using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Pricing;

/// <summary>
/// 倍率/计价引擎（01-§2.3.2 契约）。读线程安全（ReaderWriterLockSlim）；变更方法内部串行。
/// - 版本选取：EffectiveFrom 归一为 "0000-00-00"，自链尾倒序找第一个 effDate &lt;= 目标日期（最近保存优先）；
/// - 目标日期按 EffectiveContext（utc→UTC 日历 / local→UTC+offset 日历）从 ts 推导；
/// - 时段/星期匹配用分数小时 [C4]，左闭右开 [Start, End)；Local 口径先平移时段/规则表（按 (模型,版本,offset) 缓存 [S3]）；
/// - 成本 = hit/1e6*CachePer1M + miss/1e6*InputPer1M + completion/1e6*OutputPer1M，单规则命中即冻结；
/// - 实时路径与重算路径（RecalcDerived/AggregateLocal 经 QuoteRate/QuoteCost 委托）共用同一报价核心
///   与同一 TimeMath 函数 → RecalcDerived 幂等前提（02-§4.4 一致性硬约束）。
/// </summary>
public sealed class PricingEngine : IPricingEngine
{
    private readonly ReaderWriterLockSlim _rw = new(LockRecursionPolicy.NoRecursion);
    private readonly object _cacheGate = new();
    private readonly Dictionary<ShiftCacheKey, ShiftedTable> _shiftCache = new();
    private readonly string _path;
    private PricingDocument _doc;
    private string _effMode = "utc";
    private int _offsetMin;
    private long _revision = 1;
    private long _shiftBuildCount;

    public PricingEngine(string path)
    {
        _path = path;
        var (doc, migrated) = PricingDocumentIO.Load(path);
        _doc = doc;
        if (migrated)
        {
            // 载入即规范并回写（原 Load 的 migrated 重写语义）
            try { PricingDocumentIO.Save(path, doc); }
            catch (Exception ex) { Logger.Warn("Pricing", $"规范化回写 pricing.json 失败: {ex.Message}"); }
        }
    }

    // —— 接口契约（01-§2.3.2）——

    public MultiplierQuote GetMultiplier(string modelKey, long completedAtUnix, BucketScope scope)
    {
        var (mode, offset) = EffectiveContext;
        var date = EffectiveDate(completedAtUnix, mode, offset);
        var hour = scope == BucketScope.Utc
            ? TimeMath.UtcFractionalHour(completedAtUnix)
            : TimeMath.LocalFractionalHour(completedAtUnix, offset);
        return QuoteRateCore(modelKey, date, hour, scope, offset);
    }

    public CostQuote GetCost(string modelKey, long completedAtUnix,
                             long cacheHit, long cacheMiss, long completion, BucketScope scope)
    {
        var (mode, offset) = EffectiveContext;
        var date = EffectiveDate(completedAtUnix, mode, offset);
        var hour = scope == BucketScope.Utc
            ? TimeMath.UtcFractionalHour(completedAtUnix)
            : TimeMath.LocalFractionalHour(completedAtUnix, offset);
        return QuoteCostCore(modelKey, date, completedAtUnix, hour, scope, offset, cacheHit, cacheMiss, completion);
    }

    public PricingDocument Document
    {
        get
        {
            _rw.EnterReadLock();
            try { return _doc; }
            finally { _rw.ExitReadLock(); }
        }
    }

    public (string Mode, int OffsetMin) EffectiveContext
    {
        get
        {
            _rw.EnterReadLock();
            try { return (_effMode, _offsetMin); }
            finally { _rw.ExitReadLock(); }
        }
    }

    /// <summary>文档修订号（缓存键维度；接口外附加成员，Engine 供查询缓存使用）。</summary>
    public long Revision
    {
        get
        {
            _rw.EnterReadLock();
            try { return _revision; }
            finally { _rw.ExitReadLock(); }
        }
    }

    public void SetEffectiveContext(string mode, int offsetMin)
    {
        // mode 非 "local" 一律归 "utc"（原 SetEffectiveDateMode 防御保持）
        if (mode != "local") mode = "utc";
        _rw.EnterWriteLock();
        try
        {
            _effMode = mode;
            _offsetMin = offsetMin;
            InvalidateCacheNoLock(); // 口径变更 → 平移缓存整体失效 [S3]
            _revision++;
        }
        finally { _rw.ExitWriteLock(); }
    }

    public void UpdateMultiplierVersion(string modelKey, string? effectiveFrom, IReadOnlyList<MultiplierPeriod>? periods)
    {
        periods ??= [new MultiplierPeriod(0, 24, 1.0)]; // periods=null → [{0,24,1.0}]
        _rw.EnterWriteLock();
        try
        {
            var mc = _doc.Multipliers.TryGetValue(modelKey, out var existing)
                ? existing
                : new MultiplierConfig([]);
            var history = Upsert(mc.History, new MultiplierVersion(effectiveFrom, periods));
            var dict = new Dictionary<string, MultiplierConfig>(_doc.Multipliers, StringComparer.Ordinal)
            {
                [modelKey] = new MultiplierConfig(history),
            };
            _doc = new PricingDocument(dict, _doc.Pricing);
            InvalidateCacheNoLock();
            _revision++;
        }
        finally { _rw.ExitWriteLock(); }
        SaveAndNotify();
    }

    public void UpdatePricingVersion(string modelKey, string? effectiveFrom, string currency, IReadOnlyList<PriceRule> rules)
    {
        currency = NormalizeCurrency(currency); // 仅接受 CNY/USD，其它 ArgumentException
        _rw.EnterWriteLock();
        try
        {
            var pc = _doc.Pricing.TryGetValue(modelKey, out var existing)
                ? existing
                : new PriceConfig([]);
            var history = Upsert(pc.History, new PriceVersion(effectiveFrom, currency, rules));
            var dict = new Dictionary<string, PriceConfig>(_doc.Pricing, StringComparer.Ordinal)
            {
                [modelKey] = new PriceConfig(history),
            };
            _doc = new PricingDocument(_doc.Multipliers, dict);
            InvalidateCacheNoLock();
            _revision++;
        }
        finally { _rw.ExitWriteLock(); }
        SaveAndNotify();
    }

    public void ReplaceDocument(PricingDocument doc)
    {
        var normalized = doc.Normalize(); // 导入/全量替换先经 Normalize
        _rw.EnterWriteLock();
        try
        {
            _doc = normalized;
            InvalidateCacheNoLock();
            _revision++;
        }
        finally { _rw.ExitWriteLock(); }
        SaveAndNotify();
    }

    public event EventHandler? Changed;

    // —— 委托出口（RateFunc/CostFunc 落点；hour/offset 由调用方显式传入）——

    /// <summary>RateFunc 委托出口：聚合器已按其口径算好分数小时（offsetMin=0 → UTC 口径匹配 UTC 表）。</summary>
    public double QuoteRate(string modelKey, long ts, double hour, int offsetMin)
    {
        var (mode, _) = EffectiveContext;
        var date = EffectiveDate(ts, mode, offsetMin);
        return offsetMin == 0
            ? QuoteRateCore(modelKey, date, hour, BucketScope.Utc, 0).Rate
            : QuoteRateCore(modelKey, date, hour, BucketScope.Local, offsetMin).Rate;
    }

    /// <summary>CostFunc 委托出口（同上；星期按 ts 与口径日历推导）。</summary>
    public (double Cny, double Usd) QuoteCost(string modelKey, long ts, double hour, int offsetMin,
                                              long cacheHit, long cacheMiss, long completion)
    {
        var (mode, _) = EffectiveContext;
        var date = EffectiveDate(ts, mode, offsetMin);
        var q = offsetMin == 0
            ? QuoteCostCore(modelKey, date, ts, hour, BucketScope.Utc, 0, cacheHit, cacheMiss, completion)
            : QuoteCostCore(modelKey, date, ts, hour, BucketScope.Local, offsetMin, cacheHit, cacheMiss, completion);
        return (q.CostCNY, q.CostUSD);
    }

    // —— 报价核心 ——

    private MultiplierQuote QuoteRateCore(string modelKey, string date, double hour, BucketScope scope, int offset)
    {
        var v = MultiplierVersionFor(modelKey, date);
        if (v is null || v.Periods.Count == 0)
            return new MultiplierQuote(1.0, false); // 兜底 1：无版本/空时段
        var table = scope == BucketScope.Utc ? v.Periods : Shifted(modelKey, v, offset).Periods!;
        foreach (var p in table)
            if (hour >= p.Start && hour < p.End)
                return new MultiplierQuote(p.Rate, true);
        return new MultiplierQuote(1.0, false); // 兜底 2：无匹配时段
    }

    private CostQuote QuoteCostCore(string modelKey, string date, long ts, double hour, BucketScope scope, int offset,
                                    long cacheHit, long cacheMiss, long completion)
    {
        var v = PricingVersionFor(modelKey, date);
        if (v is null || v.Rules.Count == 0)
            return new CostQuote(0, 0, Priced: false); // "不计价/Coding Plan 套餐"
        // 星期按请求 ts 的口径日历推导（1=周一..7=周日；周日 0→7）
        var wd = scope == BucketScope.Utc ? TimeMath.IsoWeekdayUtc(ts) : TimeMath.IsoWeekdayLocal(ts, offset);
        var table = scope == BucketScope.Utc ? v.Rules : Shifted(modelKey, v, offset).Rules!;
        foreach (var r in table)
        {
            if (!DayMatch(r.Days, wd)) continue;
            if (hour >= r.Start && hour < r.End)
            {
                var cost = cacheHit / 1e6 * r.CachePer1M
                         + cacheMiss / 1e6 * r.InputPer1M
                         + completion / 1e6 * r.OutputPer1M;
                return v.Currency.Equals("CNY", StringComparison.Ordinal)
                    ? new CostQuote(cost, 0, Priced: true)
                    : new CostQuote(0, cost, Priced: true);
            }
        }
        // 版本在但无命中规则 → 0 成本且 Priced=true（与原数值行为一致，UI 显示 ¥0.0000）
        return new CostQuote(0, 0, Priced: true);
    }

    private static bool DayMatch(IReadOnlyList<int>? days, int iso)
    {
        if (days is null || days.Count == 0) return true; // 空/缺省 = 每天
        foreach (var d in days)
            if (d == iso) return true;
        return false;
    }

    // —— 版本链与日期 ——

    internal static string EffDate(string? ef) => string.IsNullOrEmpty(ef) ? "0000-00-00" : ef;

    internal MultiplierVersion? MultiplierVersionFor(string modelKey, string date)
    {
        var doc = Document;
        if (!doc.Multipliers.TryGetValue(modelKey, out var mc)) return null;
        for (var i = mc.History.Count - 1; i >= 0; i--)
            if (string.CompareOrdinal(EffDate(mc.History[i].EffectiveFrom), date) <= 0)
                return mc.History[i];
        return null;
    }

    internal PriceVersion? PricingVersionFor(string modelKey, string date)
    {
        var doc = Document;
        if (!doc.Pricing.TryGetValue(modelKey, out var pc)) return null;
        for (var i = pc.History.Count - 1; i >= 0; i--)
            if (string.CompareOrdinal(EffDate(pc.History[i].EffectiveFrom), date) <= 0)
                return pc.History[i];
        return null;
    }

    private static string EffectiveDate(long ts, string mode, int offsetMin) =>
        mode == "local" ? TimeMath.LocalDate(ts, offsetMin) : TimeMath.UtcDate(ts);

    // —— 平移缓存 [S3]（键含版本生效日期：不同生效日期的版本各建各表；文档/口径变更整体失效）——

    private readonly record struct ShiftCacheKey(string Model, string EffDate, int OffsetMin);

    private sealed class ShiftedTable
    {
        public IReadOnlyList<MultiplierPeriod>? Periods;
        public IReadOnlyList<PriceRule>? Rules;
    }

    private ShiftedTable Shifted(string modelKey, MultiplierVersion v, int offset)
    {
        var key = new ShiftCacheKey(modelKey, EffDate(v.EffectiveFrom), offset);
        lock (_cacheGate)
        {
            if (_shiftCache.TryGetValue(key, out var t) && t.Periods is not null) return t;
        }
        var periods = PricingShift.ShiftPeriodsToLocal(v.Periods, offset);
        lock (_cacheGate)
        {
            if (!_shiftCache.TryGetValue(key, out var t))
            {
                t = new ShiftedTable();
                _shiftCache[key] = t;
                _shiftBuildCount++;
            }
            t.Periods = periods;
            return t;
        }
    }

    private ShiftedTable Shifted(string modelKey, PriceVersion v, int offset)
    {
        var key = new ShiftCacheKey(modelKey, EffDate(v.EffectiveFrom), offset);
        lock (_cacheGate)
        {
            if (_shiftCache.TryGetValue(key, out var t) && t.Rules is not null) return t;
        }
        var rules = PricingShift.ShiftPriceRulesToLocal(v.Rules, offset);
        lock (_cacheGate)
        {
            if (!_shiftCache.TryGetValue(key, out var t))
            {
                t = new ShiftedTable();
                _shiftCache[key] = t;
                _shiftBuildCount++;
            }
            t.Rules = rules;
            return t;
        }
    }

    /// <summary>文档/口径变更 → 平移缓存整体失效（文档修订号语义，避免逐一追踪，02-§4.5）。</summary>
    private void InvalidateCacheNoLock()
    {
        lock (_cacheGate)
        {
            _shiftCache.Clear();
        }
    }

    /// <summary>平移表构建次数（S3 测试观测点）。</summary>
    internal long ShiftBuildCount
    {
        get { lock (_cacheGate) { return _shiftBuildCount; } }
    }

    // —— upsert / 持久化 ——

    private static List<MultiplierVersion> Upsert(IReadOnlyList<MultiplierVersion> history, MultiplierVersion v)
    {
        // 同 EffectiveFrom 旧版本移除、新版本插到链尾（原 upsertMultiplierVersion：数组顺序即保存顺序）
        var outList = new List<MultiplierVersion>(history.Count + 1);
        foreach (var x in history)
            if (!string.Equals(x.EffectiveFrom, v.EffectiveFrom, StringComparison.Ordinal))
                outList.Add(x);
        outList.Add(v);
        return outList;
    }

    private static List<PriceVersion> Upsert(IReadOnlyList<PriceVersion> history, PriceVersion v)
    {
        var outList = new List<PriceVersion>(history.Count + 1);
        foreach (var x in history)
            if (!string.Equals(x.EffectiveFrom, v.EffectiveFrom, StringComparison.Ordinal))
                outList.Add(x);
        outList.Add(v);
        return outList;
    }

    private void SaveAndNotify()
    {
        try
        {
            PricingDocumentIO.Save(_path, Document);
        }
        catch (Exception ex)
        {
            Logger.Warn("Pricing", $"保存 pricing.json 失败: {ex.Message}");
        }
        // 写锁释放后才触发 Changed（§8.5：任何组件持锁时禁止发布事件）
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string NormalizeCurrency(string currency)
    {
        if (string.Equals(currency, "CNY", StringComparison.OrdinalIgnoreCase)) return "CNY";
        if (string.Equals(currency, "USD", StringComparison.OrdinalIgnoreCase)) return "USD";
        throw new ArgumentException($"currency 仅接受 CNY/USD，实际: {currency}", nameof(currency));
    }
}

/// <summary>IPricingEngine 契约（01-§2.3.2）。</summary>
public interface IPricingEngine
{
    /// <summary>指定时刻倍率（内部换算分数小时 [C4]；无版本/无匹配时段 → Rate=1.0）。</summary>
    MultiplierQuote GetMultiplier(string modelKey, long completedAtUnix, BucketScope scope);

    /// <summary>指定时刻成本（命中第一条规则后按 1e6 折算冻结；无版本/无规则 → Priced=false）。</summary>
    CostQuote GetCost(string modelKey, long completedAtUnix,
                      long cacheHit, long cacheMiss, long completion, BucketScope scope);

    /// <summary>当前文档不可变快照（对话框绑定用）。</summary>
    PricingDocument Document { get; }

    /// <summary>生效日期口径与偏移（来自 settings.json）。</summary>
    (string Mode, int OffsetMin) EffectiveContext { get; }

    /// <summary>生效日期口径与偏移变更（平移缓存全部失效 [S3]）。</summary>
    void SetEffectiveContext(string mode, int offsetMin);

    /// <summary>追加/覆盖倍率版本：同 EffectiveFrom 旧版本移除、新版本插到链尾；立即写 pricing.json 并触发 Changed。</summary>
    void UpdateMultiplierVersion(string modelKey, string? effectiveFrom, IReadOnlyList<MultiplierPeriod>? periods);

    /// <summary>同上（计价版本；currency 仅接受 CNY/USD）。</summary>
    void UpdatePricingVersion(string modelKey, string? effectiveFrom, string currency, IReadOnlyList<PriceRule> rules);

    /// <summary>导入/全量替换文档（先经 Normalize）。</summary>
    void ReplaceDocument(PricingDocument doc);

    /// <summary>文档变更通知（Engine 转发为 ConfigChanged(Pricing)）。</summary>
    event EventHandler? Changed;
}
