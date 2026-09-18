using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Storage;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Stats;

/// <summary>IAccumulator 契约（01-§2.3.3）。全部成员线程安全（一把锁护双桶）。</summary>
public interface IAccumulator
{
    /// <summary>累加一笔请求到 UTC 与 Local 两桶（内部按当前 EffectiveContext 报价并冻结）。返回 UTC 口径增量。永不抛异常。</summary>
    AccumulateResult AddUsage(UsageEvent evt);

    /// <summary>整桶替换 UTC 桶（"设置即完整替换"语义 [S1]）；行的 Mul*/Cost* 直接取 usage_daily 列，不再乘算。空列表 = 清空。</summary>
    void ReplaceUtcBucket(IReadOnlyList<ModelDailyAggregate> todayUtcRows);

    /// <summary>整桶替换 Local 桶：逐行按本地分数小时重新取倍率/成本冻结（与 AddUsage 的 Local 路径同口径）。</summary>
    void ReplaceLocalBucket(IReadOnlyList<LocalReplayRow> todayLocalRows);

    /// <summary>补 0 值骨架条目（历史出现过的模型，保证卡片渲染；已存在则跳过）。</summary>
    void SeedModelSkeleton(string provider, string model);

    bool HasModel(string provider, string model);

    /// <summary>构建不可变快照。missedByModel 由调用方注入（本地日窗口的漏抓计数）；
    /// 每模型 MissedCount 与全局 MissedCaptures（= 字典全部值求和 [S4]）由此填充。</summary>
    StatsSnapshot Snapshot(IReadOnlyDictionary<string, long> missedByModel);

    void ResetUtcBucket();
    void ResetLocalBucket();
    void ResetAllBuckets();
    void ResetModel(string modelKey);
}

/// <summary>
/// 双口径线程安全累积器（02-§3）。UTC 桶与 Local(UTC+offset) 桶互不干扰同时累计；
/// total 遵铁律 1（[C3] 两桶同源）；倍率分量 Round + MulTotal=分量和（铁律 2，[C11]）；
/// DeltaTokens = 最近一笔请求的 TotalTokens（卡片"+N"；整桶替换/重置后为 0 [S1]）。
/// </summary>
public sealed class Accumulator : IAccumulator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ModelAccumulator> _index = new(StringComparer.Ordinal);
    private readonly List<ModelAccumulator> _order = new();
    private readonly IPricingEngine _pricing;

    public Accumulator(IPricingEngine pricing)
    {
        _pricing = pricing;
    }

    public AccumulateResult AddUsage(UsageEvent evt)
    {
        try
        {
            var key = evt.Key.ToString();
            var u = evt.Usage;
            // —— 报价在锁外（叶子锁 Pricing 先取即放；§8.5 顺序 Coordinator → Accumulator → Pricing）——
            var rateU = _pricing.GetMultiplier(key, evt.CompletedAtUnix, BucketScope.Utc).Rate;
            var costU = _pricing.GetCost(key, evt.CompletedAtUnix, u.CacheHitTokens, u.CacheMissTokens,
                                         u.CompletionTokens, BucketScope.Utc);
            var rateL = _pricing.GetMultiplier(key, evt.CompletedAtUnix, BucketScope.Local).Rate;
            var costL = _pricing.GetCost(key, evt.CompletedAtUnix, u.CacheHitTokens, u.CacheMissTokens,
                                         u.CompletionTokens, BucketScope.Local);
            lock (_gate)
            {
                var ma = GetOrCreate(evt.Provider, evt.Model);
                Add(ma.Utc, u, rateU, costU, evt.CompletedAtUnix);
                Add(ma.Local, u, rateL, costL, evt.CompletedAtUnix);
                var (mulP, mulH, mulM, mulC, mulR) = MulFields(u, rateU);
                return new AccumulateResult(mulP + mulC, mulP, mulH, mulM, mulC, mulR, costU.CostCNY, costU.CostUSD);
            }
        }
        catch (Exception ex)
        {
            // 永不抛异常（契约）：内部异常 → Warn、事件丢弃
            Logger.Warn("Stats", $"AddUsage 失败（事件丢弃）: {ex.Message}");
            return new AccumulateResult(0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    private static void Add(BucketValues b, UnifiedUsage u, double rate, CostQuote cost, long ts)
    {
        b.RequestCount++;
        b.LastActiveUnix = ts;
        b.Prompt += u.PromptTokens;
        b.CacheHit += u.CacheHitTokens;
        b.CacheMiss += u.CacheMissTokens;
        b.Completion += u.CompletionTokens;
        b.Reasoning += u.ReasoningTokens;
        b.Total += u.TotalTokens; // [C3] 两桶同源（原：UTC 用厂商 total、Local 用 p+c）
        var (mulP, mulH, mulM, mulC, mulR) = MulFields(u, rate);
        b.MulPrompt += mulP;
        b.MulCacheHit += mulH;
        b.MulCacheMiss += mulM;
        b.MulCompletion += mulC;
        b.MulReasoning += mulR;
        b.MulTotal += mulP + mulC; // [C11] 分量和（原：int((p+c)*rate) 截断）
        b.CostCNY += cost.CostCNY;
        b.CostUSD += cost.CostUSD;
        b.DeltaTokens = u.TotalTokens; // 语义：最近一笔的原始 total（非倍率、非累计）
    }

    private static (long MulP, long MulH, long MulM, long MulC, long MulR) MulFields(UnifiedUsage u, double rate) =>
        (TimeMath.Round(u.PromptTokens * rate),
         TimeMath.Round(u.CacheHitTokens * rate),
         TimeMath.Round(u.CacheMissTokens * rate),
         TimeMath.Round(u.CompletionTokens * rate),
         TimeMath.Round(u.ReasoningTokens * rate));

    public void ReplaceUtcBucket(IReadOnlyList<ModelDailyAggregate> todayUtcRows)
    {
        lock (_gate)
        {
            foreach (var ma in _order) ZeroUtc(ma); // 先清全部 UTC 侧值（[S1] 完整替换语义）
            foreach (var r in todayUtcRows)
            {
                var ma = GetOrCreate(r.Provider, r.Model);
                var b = ma.Utc;
                b.RequestCount = r.RequestCount;
                b.Prompt = r.PromptTokens;
                b.CacheHit = r.CacheHitTokens;
                b.CacheMiss = r.CacheMissTokens;
                b.Completion = r.CompletionTokens;
                b.Reasoning = r.ReasoningTokens;
                b.Total = r.TotalTokens;
                b.MulTotal = r.MulTotal;
                b.MulPrompt = r.MulPrompt;
                b.MulCacheHit = r.MulCacheHit;
                b.MulCacheMiss = r.MulCacheMiss;
                b.MulCompletion = r.MulCompletion;
                b.MulReasoning = r.MulReasoning;
                b.CostCNY = r.CostCNY;
                b.CostUSD = r.CostUSD;
                b.DeltaTokens = 0; // [S1] 恢复即归零
                b.LastActiveUnix = 0;
            }
        }
    }

    public void ReplaceLocalBucket(IReadOnlyList<LocalReplayRow> rows)
    {
        lock (_gate)
        {
            foreach (var ma in _order) ZeroLocal(ma);
            foreach (var r in rows)
            {
                var key = r.Provider + "/" + r.Model;
                var u = r.Usage;
                // 逐行按本地分数小时重新报价（与 AddUsage 的 Local 路径同口径）
                var rateL = _pricing.GetMultiplier(key, r.Ts, BucketScope.Local).Rate;
                var costL = _pricing.GetCost(key, r.Ts, u.CacheHitTokens, u.CacheMissTokens,
                                             u.CompletionTokens, BucketScope.Local);
                var ma = GetOrCreate(r.Provider, r.Model);
                Add(ma.Local, u, rateL, costL, r.Ts);
            }
        }
    }

    public void SeedModelSkeleton(string provider, string model)
    {
        lock (_gate)
        {
            GetOrCreate(provider, model); // 0 值条目（历史模型卡片保持渲染，原 seedHistoricalModels 语义）
        }
    }

    public bool HasModel(string provider, string model)
    {
        lock (_gate)
        {
            return _index.ContainsKey(provider + "/" + model);
        }
    }

    public StatsSnapshot Snapshot(IReadOnlyDictionary<string, long> missedByModel)
    {
        var missed = missedByModel ?? new Dictionary<string, long>();
        lock (_gate)
        {
            var utcModels = new List<ModelSnapshot>(_order.Count);
            var localModels = new List<ModelSnapshot>(_order.Count);
            var utcSummary = new SummaryAcc();
            var localSummary = new SummaryAcc();
            foreach (var ma in _order)
            {
                var utc = ToSnapshot(ma.Provider, ma.Model, ma.Utc, missed.GetValueOrDefault(ma.Key));
                var local = ToSnapshot(ma.Provider, ma.Model, ma.Local, missed.GetValueOrDefault(ma.Key));
                utcModels.Add(utc);
                localModels.Add(local);
                utcSummary.Add(utc);
                localSummary.Add(local);
            }
            // [S4] 全局漏抓合计 = 字典全部键求和（原只数快照内模型 → 纯漏抓新模型不计入）
            var missedTotal = missed.Values.Sum();
            var now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
            return new StatsSnapshot(
                utcModels, utcSummary.ToSnapshot(), localModels, localSummary.ToSnapshot(),
                missedTotal, new Dictionary<string, long>(missed, StringComparer.Ordinal),
                0, now);
        }
    }

    private static ModelSnapshot ToSnapshot(string provider, string model, BucketValues b, long missed) =>
        new(provider, model, b.RequestCount,
            b.Prompt, b.CacheHit, b.CacheMiss, b.Completion, b.Reasoning, b.Total,
            b.MulTotal, b.MulPrompt, b.MulCacheHit, b.MulCacheMiss, b.MulCompletion, b.MulReasoning,
            b.CostCNY, b.CostUSD, b.DeltaTokens, b.LastActiveUnix, missed);

    public void ResetUtcBucket()
    {
        lock (_gate)
        {
            foreach (var ma in _order) ZeroUtc(ma); // 保留模型条目，UTC 侧清零（原 ResetKeepModels）
        }
    }

    public void ResetLocalBucket()
    {
        lock (_gate)
        {
            foreach (var ma in _order) ZeroLocal(ma); // 原 ResetKeepModelsLocal
        }
    }

    public void ResetAllBuckets()
    {
        lock (_gate)
        {
            _index.Clear(); // 删除全部条目（原 Reset）
            _order.Clear();
        }
    }

    public void ResetModel(string modelKey)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue(modelKey, out var ma)) return;
            ZeroUtc(ma);
            ZeroLocal(ma); // 单模型两桶清零，条目保留（原 ResetModel）
        }
    }

    private static void ZeroUtc(ModelAccumulator ma) => ma.Utc = new BucketValues();
    private static void ZeroLocal(ModelAccumulator ma) => ma.Local = new BucketValues();

    private ModelAccumulator GetOrCreate(string provider, string model)
    {
        var key = provider + "/" + model;
        if (_index.TryGetValue(key, out var ma)) return ma;
        ma = new ModelAccumulator(provider, model);
        _index[key] = ma;
        _order.Add(ma);
        return ma;
    }

    private sealed class ModelAccumulator(string provider, string model)
    {
        public string Provider => provider;
        public string Model => model;
        public string Key => provider + "/" + model;
        public BucketValues Utc = new();
        public BucketValues Local = new();
    }

    private sealed class BucketValues
    {
        public long RequestCount, Prompt, CacheHit, CacheMiss, Completion, Reasoning, Total;
        public long MulTotal, MulPrompt, MulCacheHit, MulCacheMiss, MulCompletion, MulReasoning;
        public double CostCNY, CostUSD;
        public long DeltaTokens;
        public long LastActiveUnix;
    }

    private sealed class SummaryAcc
    {
        public long RequestCount, Prompt, CacheHit, CacheMiss, Completion, Reasoning, Total;
        public long MulTotal, MulPrompt, MulCacheHit, MulCacheMiss, MulCompletion, MulReasoning;
        public double CostCNY, CostUSD;

        public void Add(ModelSnapshot m)
        {
            RequestCount += m.RequestCount;
            Prompt += m.PromptTokens;
            CacheHit += m.CacheHitTokens;
            CacheMiss += m.CacheMissTokens;
            Completion += m.CompletionTokens;
            Reasoning += m.ReasoningTokens;
            Total += m.TotalTokens;
            MulTotal += m.MulTotal;
            MulPrompt += m.MulPrompt;
            MulCacheHit += m.MulCacheHit;
            MulCacheMiss += m.MulCacheMiss;
            MulCompletion += m.MulCompletion;
            MulReasoning += m.MulReasoning;
            CostCNY += m.CostCNY;
            CostUSD += m.CostUSD;
        }

        public ModelSnapshot ToSnapshot() =>
            new("合计", "ALL", RequestCount, Prompt, CacheHit, CacheMiss, Completion, Reasoning, Total,
                MulTotal, MulPrompt, MulCacheHit, MulCacheMiss, MulCompletion, MulReasoning,
                CostCNY, CostUSD, 0, 0, 0);
    }
}
