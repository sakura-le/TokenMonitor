using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>Store 回归：C1/C4d/C6/C7/C8/C12/S2 + upsert 冲突累加移植。</summary>
public class StoreTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly Store _store;

    public StoreTests()
    {
        _store = new Store(_dir.Path, _clock, flushInterval: TimeSpan.FromMilliseconds(50));
    }

    public void Dispose() => _store.Dispose();

    private static RateFunc? Rate(double r) => (key, ts, hour, offsetMin) => r;
    private static CostFunc? Cost(double cny, double usd) => (key, ts, hour, offsetMin, hit, miss, comp) => (cny, usd);

    private static readonly RateFunc Rate2 = (_, _, _, _) => 2.0;
    private static readonly CostFunc Cost12 = (_, _, _, _, _, _, _) => (1.0, 2.0);

    // —— [C1] RecalcDerived UPSERT ——

    [Fact]
    public void Storage_RecalcDerived_UpsertsRowsMissingFromDaily()
    {
        // [C1] 仅写 usage_log（无对应 daily 行，含跨多日多模型）→ RecalcDerived → daily 行生成且数值正确
        var day1 = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        var day2 = TimeMath.UtcDayStart("2026-09-11") + 13 * 3600;
        // 用 InsertEstimatedUsage 直写 log 层（绕过 daily upsert），模拟"日志有、daily 无"
        _store.InsertEstimatedUsage(
        [
            new UsageLogRow(day1, "P", "m1", 100, 60, 40, 50, 20, 150),
            new UsageLogRow(day2, "P", "m2", 10, 0, 10, 5, 0, 15),
        ]);
        Assert.Empty(_store.GetTodayUtc()); // daily 无行（重置/未恢复场景）

        _store.RecalcDerived(Rate2, Cost12);

        var rows = _store.QueryDailyRange("2026-09-01", "2026-09-30");
        Assert.Equal(2, rows.Count);
        var r1 = rows.Single(x => x.Model == "m1");
        Assert.Equal(100, r1.PromptTokens);
        Assert.Equal(150, r1.TotalTokens);
        Assert.Equal(1, r1.RequestCount);
        Assert.Equal(200, r1.MulPrompt);  // 100 × 2.0
        Assert.Equal(120, r1.MulCacheHit);  // 60 × 2.0
        Assert.Equal(80, r1.MulCacheMiss);  // 40 × 2.0
        Assert.Equal(100, r1.MulCompletion); // 50 × 2.0
        Assert.Equal(300, r1.MulTotal);   // 分量和（200+100）
        Assert.Equal(1.0, r1.CostCNY, 12);
        Assert.Equal(2.0, r1.CostUSD, 12);
        Assert.Equal(20, rows.Single(x => x.Model == "m2").MulPrompt); // 10 × 2.0
    }

    [Fact]
    public void Storage_RecalcDerived_Idempotent_RemovesOrphans()
    {
        // 幂等：重算两次值不变；孤儿 daily 行（log 已无）被清除（铁律 5）
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.AddUsage([new UsageLogRow(ts, "P", "m", 10, 0, 10, 5, 0, 15,
            MulTotal: 30, MulPrompt: 20, MulCacheHit: 0, MulCacheMiss: 20, MulCompletion: 10, MulReasoning: 0,
            CostCNY: 1.0, CostUSD: 2.0)]);
        _store.Flush();
        _store.RecalcDerived(Rate2, Cost12);
        var once = _store.QueryDailyRange("2026-09-01", "2026-09-30").Single();
        _store.RecalcDerived(Rate2, Cost12);
        var twice = _store.QueryDailyRange("2026-09-01", "2026-09-30").Single();
        Assert.Equal(once, twice); // 逐列相等（record 值语义）

        // 孤儿行：直接写一条 log 中不存在的 daily 行 → 重算后消失
        _store.TestExecuteNonQuery("INSERT INTO usage_daily (date, provider, model) VALUES ('2026-09-12', 'X', 'orphan')");
        _store.RecalcDerived(Rate2, Cost12);
        Assert.DoesNotContain(_store.QueryDailyRange("2026-09-01", "2026-09-30"), r => r.Model == "orphan");
    }

    // —— [C4d] 聚合侧分数小时 ——

    [Fact]
    public void Aggregators_UseFractionalHour()
    {
        // [C4d] 09:29/09:30 各一行 log + [9.5,12.5) 时段：RecalcDerived 与 AggregateLocal 分属 1.0/2.0 段
        var pricing = new PricingEngine(Path.Combine(_dir.Path, "p.json"));
        pricing.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(9.5, 12.5, 2.0)]);
        RateFunc rateFn = (key, ts, hour, offsetMin) => pricing.QuoteRate(key, ts, hour, offsetMin);
        CostFunc costFn = (key, ts, hour, offsetMin, hit, miss, comp) => pricing.QuoteCost(key, ts, hour, offsetMin, hit, miss, comp);

        var day = TimeMath.UtcDayStart("2026-09-10");
        _store.InsertEstimatedUsage(
        [
            new UsageLogRow(day + 9 * 3600 + 29 * 60, "P", "m", 100, 0, 100, 10, 0, 110), // 09:29
            new UsageLogRow(day + 9 * 3600 + 30 * 60, "P", "m", 100, 0, 100, 10, 0, 110), // 09:30
        ]);
        _store.RecalcDerived(rateFn, costFn);

        var daily = _store.QueryDailyRange("2026-09-10", "2026-09-10").Single();
        // UTC 口径：09:29 → rate 1.0；09:30 → rate 2.0 → mulPrompt = 100 + 200 = 300；mulTotal = 110 + 220 = 330
        Assert.Equal(300, daily.MulPrompt);
        Assert.Equal(330, daily.MulTotal);
        Assert.Equal(220, daily.TotalTokens); // 110+110（usage_daily 即 UTC 桶）

        // Local 口径（UTC+8）：09:29Z → 本地 17:29 → 17.483 < 17.5（平移段 [17.5,20.5)）→ 1.0；09:30Z → 2.0
        var local = _store.AggregateLocal(day, day + 86400, 480, rateFn, costFn).Single();
        Assert.Equal(300, local.MulPrompt);
        Assert.Equal(330, local.MulTotal);
    }

    // —— [C6] 回滚侧车 ——

    [Fact]
    public void Storage_ReplaceDatabase_RemovesWalShm_ReopensWithSameDsn()
    {
        // [C6] 写数据不 checkpoint 制造 -wal → Backup → 变更 → ReplaceDatabase：数据==备份、journal_mode==wal、可继续写
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.AddUsage([new UsageLogRow(ts, "P", "m", 10, 0, 10, 5, 0, 15)]);
        _store.Flush();
        var backup = _store.Backup();

        // 破坏现场：删除全部数据（写入新 WAL 页）
        _store.DeleteAllData();
        Assert.Empty(_store.QueryDailyRange("2026-09-01", "2026-09-30"));
        Assert.True(File.Exists(_dir.Path + "/token_monitor.db-wal") || File.Exists(_dir.Path + "/token_monitor.db"));

        _store.ReplaceDatabase(backup);

        // 数据恢复（陈旧 WAL 未被错误回放 = 侧车已处理的语义证据）
        var row = _store.QueryDailyRange("2026-09-01", "2026-09-30").Single();
        Assert.Equal(15, row.TotalTokens);
        // 同参重开：journal_mode 保持 wal
        Assert.Equal("wal", _store.TestJournalMode());
        // 重开后可继续写
        _store.AddUsage([new UsageLogRow(ts, "P", "m2", 1, 0, 1, 1, 0, 2)]);
        _store.Flush();
        Assert.Equal(2, _store.QueryDailyRange("2026-09-01", "2026-09-30").Count());
    }

    // —— [C7] 近 N 天 ——

    [Fact]
    public void Storage_GetRecentDaysUtc_ExactlySevenRows()
    {
        // [C7] 插 8 个 UTC 日数据 → GetRecentDays(7) 恰 7 个日期（最早一日为 today-6）
        var today = TimeMath.UtcDayStart("2026-09-18");
        for (var k = 0; k < 8; k++)
        {
            var ts = today - k * 86400 + 12 * 3600;
            _store.AddUsage([new UsageLogRow(ts, "P", $"m{k}", 10, 0, 10, 5, 0, 15)]);
        }
        _store.Flush();
        var rows = _store.GetRecentDaysUtc(7);
        var dates = rows.Select(r => r.Date).Distinct().ToList();
        Assert.Equal(7, dates.Count);
        Assert.Equal("2026-09-12", dates.Min()); // today-6
        Assert.Equal("2026-09-18", dates.Max());
    }

    [Fact]
    public void Storage_GetRecentDays_BothScopesSameWindow()
    {
        // [C7b] UTC+8 本地日错位数据 → Local 侧亦恰 7 个本地日
        var pricing = new PricingEngine(Path.Combine(_dir.Path, "p.json"));
        var localToday = TimeMath.LocalDate(TimeMath.UtcDayStart("2026-09-18"), 480); // 本地 09-18
        for (var k = 0; k < 8; k++)
        {
            var localDate = TimeMath.LocalDate(TimeMath.UtcDayStart(localToday) - k * 86400, 480);
            // 本地日 12:00（UTC 04:00）
            var ts = TimeMath.LocalDayStart(localDate, 480) + 12 * 3600;
            _store.AddUsage([new UsageLogRow(ts, "P", $"m{k}", 10, 0, 10, 5, 0, 15)]);
        }
        _store.Flush();
        var start = TimeMath.LocalDate(TimeMath.UtcDayStart(localToday) - 6 * 86400, 480);
        var end = localToday;
        var rows = _store.AggregateLocal(
            TimeMath.LocalDayStart(start, 480), TimeMath.LocalDayStart(end, 480) + 86400, 480, null, null);
        var dates = rows.Select(r => r.Date).Distinct().ToList();
        Assert.Equal(7, dates.Count);
    }

    // —— [C8] 重置/删除含 usage_missed ——

    [Fact]
    public void Storage_DeleteModelData_ClearsUsageMissed()
    {
        // [C8a] daily/log/missed 三表数据 → DeleteModelData → 三表该模型全空、其余模型不受影响
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.AddUsage([new UsageLogRow(ts, "P", "m1", 10, 0, 10, 5, 0, 15)]);
        _store.AddUsage([new UsageLogRow(ts, "P", "m2", 20, 0, 20, 5, 0, 25)]);
        _store.Flush();
        _store.AddMissed(new MissedLogRow(0, ts, "P", "m1", 200, "响应无 usage 块"));
        _store.AddMissed(new MissedLogRow(0, ts, "P", "m2", 200, "响应无 usage 块"));

        _store.DeleteModelData("P/m1");

        var logs = _store.GetLogsByRange(0, long.MaxValue);
        Assert.Single(logs);
        Assert.Equal("m2", logs[0].Model);
        Assert.DoesNotContain(_store.QueryDailyRange("2026-09-01", "2026-09-30"), r => r.Model == "m1");
        var missed = _store.GetMissed(0, long.MaxValue);
        Assert.Single(missed);
        Assert.Equal("m2", missed[0].Model);
    }

    [Fact]
    public void Storage_DeleteAllData_ClearsUsageMissed()
    {
        // [C8b] 三表全清
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.AddUsage([new UsageLogRow(ts, "P", "m", 10, 0, 10, 5, 0, 15)]);
        _store.Flush();
        _store.AddMissed(new MissedLogRow(0, ts, "P", "m", 200, "x"));
        _store.DeleteAllData();
        Assert.Empty(_store.GetLogsByRange(0, long.MaxValue));
        Assert.Empty(_store.QueryDailyRange("2026-09-01", "2026-09-30"));
        Assert.Empty(_store.GetMissed(0, long.MaxValue));
    }

    [Fact]
    public void Storage_ResetToday_ClearsMissedDoubleWindow()
    {
        // [C8c] UTC 今日 05:00 与本地今日跨 UTC 日界（UTC 前一日 20:00 = 本地 04:00）各造 log+missed
        // → ResetToday 双窗口 log/missed 清零、daily 当日清零、RecalcDerived 不复活
        var utcToday = TimeMath.UtcDayStart("2026-09-18");
        var inBoth = utcToday + 5 * 3600;             // UTC 今日 05:00 = 本地 13:00（双窗口交集）
        var localOnly = utcToday - 4 * 3600;          // UTC 前一日 20:00 = 本地今日 04:00（仅本地窗口）
        _store.AddUsage(
        [
            new UsageLogRow(inBoth, "P", "m", 10, 0, 10, 5, 0, 15),
            new UsageLogRow(localOnly, "P", "m", 10, 0, 10, 5, 0, 15),
        ]);
        _store.Flush();
        _store.AddMissed(new MissedLogRow(0, inBoth, "P", "m", 200, "x"));
        _store.AddMissed(new MissedLogRow(0, localOnly, "P", "m", 200, "x"));

        _store.ResetToday(null, 480);

        Assert.Empty(_store.GetLogsByRange(0, long.MaxValue));  // 双窗口全清
        Assert.Empty(_store.GetMissed(0, long.MaxValue));       // missed 双窗口全清 [C8]
        Assert.Empty(_store.QueryDailyRange("2026-09-18", "2026-09-18"));
        _store.RecalcDerived(Rate2, Cost12);
        Assert.Empty(_store.QueryDailyRange("2026-09-01", "2026-09-30")); // 铁律 5：log 已删 → 不复活
    }

    [Fact]
    public void Storage_ResetToday_ModelNameWithSlash_FiltersCorrectly()
    {
        // [C19-②] 模型名含 '/'：SplitN(…,2) 语义，三表过滤正确
        var ts = TimeMath.UtcDayStart("2026-09-18") + 5 * 3600;
        _store.AddUsage(
        [
            new UsageLogRow(ts, "P", "openai/gpt-4", 10, 0, 10, 5, 0, 15),
            new UsageLogRow(ts, "P", "other", 10, 0, 10, 5, 0, 15),
        ]);
        _store.Flush();
        _store.ResetToday("P/openai/gpt-4", 0);
        var logs = _store.GetLogsByRange(0, long.MaxValue);
        Assert.Single(logs);
        Assert.Equal("other", logs[0].Model);
    }

    // —— [C12] flush 失败行 ——

    [Fact]
    public void Storage_Flush_RowFailure_SkippedEntirely_OthersCommit()
    {
        // [C12] 注入一行失败：失败行不入 log 也不入 daily、其余行提交
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.InsertFailureHook = r => r.Model == "fail" ? new InvalidOperationException("注入失败") : null;
        _store.AddUsage(
        [
            new UsageLogRow(ts, "P", "ok1", 10, 0, 10, 5, 0, 15),
            new UsageLogRow(ts, "P", "fail", 99, 0, 99, 9, 0, 108),
            new UsageLogRow(ts, "P", "ok2", 20, 0, 20, 5, 0, 25),
        ]);
        _store.Flush();

        var logs = _store.GetLogsByRange(0, long.MaxValue);
        Assert.True(logs.Count == 2, "应恰好 2 条合法行");
        Assert.DoesNotContain(logs, l => l.Model == "fail");
        var daily = _store.QueryDailyRange("2026-09-01", "2026-09-30");
        Assert.Equal(2, daily.Count);
        Assert.DoesNotContain(daily, d => d.Model == "fail");
        Assert.Equal(1, daily.Single(d => d.Model == "ok1").RequestCount);
    }

    [Fact]
    public void Storage_Flush_CommitFailure_RequeuesWholeBatch_NoDuplicatesOnRetry()
    {
        // [C12] Commit 失败路径：整批回队且重试成功后无重复行
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        var batch = new List<UsageLogRow>
        {
            new(ts, "P", "m", 10, 0, 10, 5, 0, 15),
            new(ts, "P", "m", 10, 0, 10, 5, 0, 15),
        };
        var failOnce = true;
        _store.CommitFailureHook = () => failOnce && (failOnce = false); // 首次提交失败
        _store.AddUsage(batch); // 2 条 → 未达 3 → 等定时；直接 Flush 触发
        _store.Flush();
        Assert.Equal(2, _store.GetLogsByRange(0, long.MaxValue).Count); // 回队重试后恰 2 行（无重复）
        _store.Flush();
        Assert.Equal(2, _store.GetLogsByRange(0, long.MaxValue).Count);
    }

    // —— upsert 冲突累加（§9.2 新增移植）——

    [Fact]
    public void Storage_UpsertDaily_ConflictAccumulates()
    {
        // 两笔同键 → 各数值列累加、request_count+1（SQL 与原 upsertDaily 逐列一致）
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.AddUsage(
        [
            new UsageLogRow(ts, "P", "m", 10, 4, 6, 5, 0, 15,
                MulTotal: 30, MulPrompt: 20, MulCacheHit: 8, MulCacheMiss: 12, MulCompletion: 10, MulReasoning: 0,
                CostCNY: 0.5, CostUSD: 0.25),
            new UsageLogRow(ts + 60, "P", "m", 7, 1, 6, 3, 0, 10,
                MulTotal: 20, MulPrompt: 14, MulCacheHit: 2, MulCacheMiss: 12, MulCompletion: 6, MulReasoning: 0,
                CostCNY: 0.25, CostUSD: 0.5),
        ]);
        _store.Flush();
        var row = _store.QueryDailyRange("2026-09-10", "2026-09-10").Single();
        Assert.Equal(17, row.PromptTokens);
        Assert.Equal(5, row.CacheHitTokens);
        Assert.Equal(12, row.CacheMissTokens);
        Assert.Equal(8, row.CompletionTokens);
        Assert.Equal(25, row.TotalTokens);
        Assert.Equal(2, row.RequestCount);
        Assert.Equal(50, row.MulTotal);
        Assert.Equal(34, row.MulPrompt);
        Assert.Equal(10, row.MulCacheHit);
        Assert.Equal(24, row.MulCacheMiss);
        Assert.Equal(16, row.MulCompletion);
        Assert.Equal(0.75, row.CostCNY, 12);
        Assert.Equal(0.75, row.CostUSD, 12);
    }

    // —— [S2] total=0 回退 ——

    [Fact]
    public void Storage_RecalcDerived_ZeroTotal_FallsBackPromptPlusCompletion()
    {
        // [S2a] log 行 total=0（旧库迁移遗留）→ daily total=prompt+completion、mul/cost 正常
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.AddUsage([new UsageLogRow(ts, "P", "m", 100, 60, 40, 50, 20, 0)]); // total=0
        _store.Flush();
        _store.RecalcDerived(Rate2, Cost12);
        var row = _store.QueryDailyRange("2026-09-10", "2026-09-10").Single();
        Assert.Equal(150, row.TotalTokens); // 100 + 50
        Assert.Equal(200, row.MulPrompt); // 100 × 2.0
    }

    [Fact]
    public void Storage_AggregateLocal_ZeroTotal_FallsBack()
    {
        // [S2b] Local 聚合侧 total=0 旧行回退
        var ts = TimeMath.UtcDayStart("2026-09-10") + 12 * 3600;
        _store.AddUsage([new UsageLogRow(ts, "P", "m", 100, 60, 40, 50, 20, 0)]);
        _store.Flush();
        var rows = _store.AggregateLocal(0, long.MaxValue, 480, null, null);
        Assert.Equal(150, rows.Single().TotalTokens);
    }

    // —— 辅助 ———

    [Fact]
    public void Storage_Backup_DedupesSameMillisecondNames()
    {
        var b1 = _store.Backup();
        var b2 = _store.Backup();
        Assert.NotEqual(b1, b2);
        Assert.True(File.Exists(b1));
        Assert.True(File.Exists(b2));
    }

    [Fact]
    public void Storage_OpLog_TrimmedAndQueried()
    {
        for (var i = 0; i < 5100; i++) _store.LogOp("m", "a", "d", "");
        Assert.Equal(500, _store.GetOpLogs(null).Count);       // 读取恒 500
        Assert.Equal(500, _store.GetOpLogs("m").Count);
        Assert.True(_store.GetOpLogs(null).Count <= 5000);     // 写侧裁剪至 5000（内部）
    }
}
