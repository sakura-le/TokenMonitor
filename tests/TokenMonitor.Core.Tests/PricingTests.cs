using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Pricing;

namespace TokenMonitor.Core.Tests;

/// <summary>Pricing 回归：C4/C11/C13/S3 + 原 pricing_test 全部移植（版本链/星期/去重/最近保存优先）。</summary>
public class PricingTests
{
    private static long Ts(int y, int m, int d, int hour, int minute = 0) =>
        new DateTimeOffset(y, m, d, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static PricingEngine NewEngine(string dir) => new(Path.Combine(dir, "pricing.json"));

    // —— [C4] 分数小时 ——

    [Fact]
    public void Pricing_FractionalHour_0929_FallbackRate_0930_NewRate()
    {
        // [C4a] 时段 [9.5,12.5) rate=2：09:29 → 前段 1.0；09:30 → 2.0（原整点匹配 9:00-9:59 全漏配）
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(9.5, 12.5, 2.0)]);
        Assert.Equal(1.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 9, 29), BucketScope.Utc).Rate);
        Assert.Equal(2.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 9, 30), BucketScope.Utc).Rate);
    }

    [Fact]
    public void Pricing_HourBoundaries_MidnightAndFullDay()
    {
        // [C4b] 00:00/00:29 命中 [0,0.5)；00:30 出段；23:59 命中 [23.5,24)
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("P/m", null,
        [
            new MultiplierPeriod(0, 0.5, 3.0),
            new MultiplierPeriod(23.5, 24, 4.0),
        ]);
        Assert.Equal(3.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 0, 0), BucketScope.Utc).Rate);
        Assert.Equal(3.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 0, 29), BucketScope.Utc).Rate);
        Assert.Equal(1.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 0, 30), BucketScope.Utc).Rate);
        Assert.Equal(4.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 23, 59), BucketScope.Utc).Rate);
    }

    [Fact]
    public void Pricing_HalfHourTimezone_Utc530_MatchesShiftedBoundary()
    {
        // [C4c] offset=330（UTC+5:30）：本地视角时段 [9.5,12.5) 经 localToUtc 落库为 [4.0,7.0)；
        // Local 视图平移 +5.5 还原 [9.5,12.5) → UTC 04:00（本地 09:30）→ 2.0；UTC 03:59 → 1.0
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(4.0, 7.0, 2.0)]);
        e.SetEffectiveContext("utc", 330);
        Assert.Equal(2.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 4, 0), BucketScope.Local).Rate);
        Assert.Equal(1.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 3, 59), BucketScope.Local).Rate);
    }

    [Fact]
    public void Pricing_QuarterHourTimezone_Utc545_ShiftBoundary()
    {
        // 02-§4.5 回归：UTC+5:45 下 Start=9.0 平移为 [14.75,…) 边界匹配
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(9.0, 12.0, 2.0)]);
        e.SetEffectiveContext("utc", 345);
        // UTC 09:00 → 本地 14:45 = 14.75 → 命中 [14.75,17.75)
        Assert.Equal(2.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 9, 0), BucketScope.Local).Rate);
        Assert.Equal(1.0, e.GetMultiplier("P/m", Ts(2026, 9, 1, 8, 59), BucketScope.Local).Rate);
    }

    // —— [C11] 倍率取整 ——

    [Fact]
    public void Pricing_MulRounding_MulTotalEqualsSumOfComponents()
    {
        // [C11] rate=0.55、prompt=101、completion=1：mulPrompt=56、mulCompletion=1、mulTotal=57（原截断 56 被否定）
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(0, 24, 0.55)]);
        var acc = new TokenMonitor.Core.Stats.Accumulator(e);
        var result = acc.AddUsage(new TokenMonitor.Core.Parser.UsageEvent("P", "m",
            new TokenMonitor.Core.Parser.UnifiedUsage(101, 0, 101, 1, 0, 102), Ts(2026, 9, 1, 10), CaptureSource.Json));
        Assert.Equal(56, result.MulPrompt);
        Assert.Equal(1, result.MulCompletion);
        Assert.Equal(57, result.MulTotal); // == 分量和
        Assert.Equal(56, result.MulCacheMiss);
    }

    [Fact]
    public void Pricing_MulRounding_PropertyHoldsOverRandomInputs()
    {
        // [C11] 性质断言：任意 (x, rate) → mulTotal == mulPrompt + mulCompletion
        var rng = new Random(20260918);
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        var acc = new TokenMonitor.Core.Stats.Accumulator(e);
        for (var i = 0; i < 100; i++)
        {
            var rate = rng.NextDouble() * 3;
            e.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(0, 24, rate)]);
            var prompt = rng.Next(0, 100000);
            var comp = rng.Next(0, 100000);
            var result = acc.AddUsage(new TokenMonitor.Core.Parser.UsageEvent("P", "m",
                new TokenMonitor.Core.Parser.UnifiedUsage(prompt, 0, prompt, comp, 0, prompt + comp),
                Ts(2026, 9, 1, 10), CaptureSource.Json));
            Assert.Equal(result.MulPrompt + result.MulCompletion, result.MulTotal);
        }
    }

    // —— [C13] 加载迁移 ——

    [Fact]
    public void Pricing_Normalize_LegacyFlat_Per1K_DuplicateEf()
    {
        // [C13] 旧平铺 + per-1K ×1000 + 同 effective_from 去重（留后值占首位）+ 规范回写
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "pricing.json");
        const string legacy = """
            {"multipliers":{"m":{"periods":[{"start":0,"end":24,"rate":2}]}},"pricing":{"m":{"currency":"CNY","rules":[{"start":0,"end":24,"days":[1,2],"input_per_1k":0.004,"cache_per_1m":0,"output_per_1m":2}]}}}
            """;
        File.WriteAllText(path, legacy);
        var (doc, migrated) = PricingDocumentIO.Load(path);
        Assert.True(migrated);
        var mc = Assert.Single(doc.Multipliers["m"].History); // 平铺 → history 化
        Assert.Null(mc.EffectiveFrom);
        Assert.Single(doc.Pricing["m"].History);
        var rule = doc.Pricing["m"].History[0].Rules[0];
        Assert.Equal(4.0, rule.InputPer1M); // 0.004 × 1000
        Assert.Equal(2.0, rule.OutputPer1M);
        Assert.Equal([1, 2], rule.Days);
        // 规范回写由 PricingEngine 构造完成（Load 返回 migrated 标记，引擎负责落盘）
        _ = new PricingEngine(path);
        var rewritten = File.ReadAllText(path);
        Assert.Contains("\"history\"", rewritten);
        // 二次加载无迁移
        var (_, migrated2) = PricingDocumentIO.Load(path);
        Assert.False(migrated2);
    }

    [Fact]
    public void Pricing_DedupeVersions_KeepsLastValueAtFirstPosition()
    {
        // 原 pricing_test TestDedupeVersions：同 ef 重复 → 1 月取 9.0（后值），3 月取 3.0
        var history = PricingDocumentIO.DedupeMultiplier(
        [
            new MultiplierVersion("2026-01-01", [new MultiplierPeriod(0, 24, 2.0)]),
            new MultiplierVersion("2026-01-01", [new MultiplierPeriod(0, 24, 9.0)]),
            new MultiplierVersion("2026-02-01", [new MultiplierPeriod(0, 24, 3.0)]),
        ]);
        Assert.Equal(2, history.Count);
        Assert.Equal(9.0, history[0].Periods[0].Rate); // 首位置、后值
        Assert.Equal(3.0, history[1].Periods[0].Rate);
    }

    // —— 版本链选取（原 pricing_test 移植）——

    [Fact]
    public void Pricing_VersionSelection_EffectiveDate()
    {
        // 原 TestVersionedRateSelection
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("m", "2026-01-01", [new MultiplierPeriod(0, 24, 2.0)]);
        e.UpdateMultiplierVersion("m", "2026-02-01", [new MultiplierPeriod(0, 24, 3.0)]);
        Assert.Equal(2.0, e.GetMultiplier("m", Ts(2026, 1, 15, 10), BucketScope.Utc).Rate);
        Assert.Equal(3.0, e.GetMultiplier("m", Ts(2026, 3, 15, 10), BucketScope.Utc).Rate);
        Assert.Equal(2.0, e.GetMultiplier("m", Ts(2026, 1, 31, 23), BucketScope.Utc).Rate); // 生效日前一天
    }

    [Fact]
    public void Pricing_EmptyEffectiveAlways()
    {
        // 原 TestEmptyEffectiveAlways：空 effective_from = 一直生效
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("m", null, [new MultiplierPeriod(0, 24, 1.5)]);
        Assert.Equal(1.5, e.GetMultiplier("m", Ts(1999, 1, 1, 0), BucketScope.Utc).Rate);
    }

    [Fact]
    public void Pricing_CostVersioned()
    {
        // 原 TestCostVersioned
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdatePricingVersion("m", "2026-01-01", "CNY",
            [new PriceRule(0, 24, null, 2, 1, 3)]);
        e.UpdatePricingVersion("m", "2026-06-01", "CNY",
            [new PriceRule(0, 24, null, 20, 10, 30)]);
        var q1 = e.GetCost("m", Ts(2026, 5, 1, 10), 1_000_000, 0, 0, BucketScope.Utc);
        Assert.Equal(1.0, q1.CostCNY);
        Assert.True(q1.Priced);
        var q2 = e.GetCost("m", Ts(2026, 7, 1, 10), 1_000_000, 0, 0, BucketScope.Utc);
        Assert.Equal(10.0, q2.CostCNY);
    }

    [Fact]
    public void Pricing_Weekday_CostByWeekday()
    {
        // 原 TestCostByWeekday：2026-08-24 周一 1.0；周六/周日 2.0
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdatePricingVersion("m", null, "CNY",
        [
            new PriceRule(0, 24, [1, 2, 3, 4, 5], 1, 1, 1),
            new PriceRule(0, 24, [6, 7], 2, 2, 2),
        ]);
        Assert.Equal(1.0, e.GetCost("m", Ts(2026, 8, 24, 10), 1_000_000, 0, 0, BucketScope.Utc).CostCNY);
        Assert.Equal(2.0, e.GetCost("m", Ts(2026, 8, 22, 10), 1_000_000, 0, 0, BucketScope.Utc).CostCNY);
        Assert.Equal(2.0, e.GetCost("m", Ts(2026, 8, 23, 10), 1_000_000, 0, 0, BucketScope.Utc).CostCNY);
    }

    [Fact]
    public void Pricing_Weekday_DefaultAll()
    {
        // 原 TestCostWeekdayDefaultAll：空 days = 每天生效
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdatePricingVersion("m", null, "CNY", [new PriceRule(0, 24, null, 5, 5, 5)]);
        for (var day = 17; day <= 23; day++) // 2026-08-17(周一)..08-23(周日)
            Assert.Equal(5.0, e.GetCost("m", Ts(2026, 8, day, 10), 1_000_000, 0, 0, BucketScope.Utc).CostCNY);
    }

    [Fact]
    public void Pricing_VersionSelection_LatestSavedWins()
    {
        // 原 TestLatestSavedVersionWins：最近保存优先（含空日期版本兜底）
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdatePricingVersion("m", "2026-08-17", "CNY", [new PriceRule(0, 24, null, 1, 1, 1)]);
        e.UpdatePricingVersion("m", null, "CNY", [new PriceRule(0, 24, null, 9, 9, 9)]);
        Assert.Equal(9.0, e.GetCost("m", Ts(2026, 8, 20, 10), 1_000_000, 0, 0, BucketScope.Utc).CostCNY);

        var e2dir = Path.Combine(tmp.Path, "sub");
        Directory.CreateDirectory(e2dir);
        var e3 = new PricingEngine(Path.Combine(e2dir, "pricing.json"));
        e3.UpdatePricingVersion("m", null, "CNY", [new PriceRule(0, 24, null, 5, 5, 5)]);
        e3.UpdatePricingVersion("m", "2026-08-17", "CNY", [new PriceRule(0, 24, null, 7, 7, 7)]);
        Assert.Equal(7.0, e3.GetCost("m", Ts(2026, 8, 20, 10), 1_000_000, 0, 0, BucketScope.Utc).CostCNY);
        Assert.Equal(5.0, e3.GetCost("m", Ts(2026, 8, 10, 10), 1_000_000, 0, 0, BucketScope.Utc).CostCNY); // 空日期兜底
    }

    [Fact]
    public void Pricing_UpsertMovesToEnd()
    {
        // 原 TestUpsertMovesToEnd：同日期覆盖 → 移到链尾
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdatePricingVersion("m", "2026-08-17", "CNY", [new PriceRule(0, 24, null, 1, 1, 1)]);
        e.UpdatePricingVersion("m", null, "CNY", [new PriceRule(0, 24, null, 2, 2, 2)]);
        e.UpdatePricingVersion("m", null, "CNY", [new PriceRule(0, 24, null, 3, 3, 3)]); // 同日期再设
        var history = e.Document.Pricing["m"].History;
        Assert.Equal(2, history.Count);
        Assert.Null(history[^1].EffectiveFrom); // 空日期版本在末尾（最近保存）
        Assert.Equal(3.0, history[^1].Rules[0].InputPer1M);
    }

    [Fact]
    public void Pricing_UpdatePricingVersion_InvalidCurrency_Throws()
    {
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        Assert.Throws<ArgumentException>(() =>
            e.UpdatePricingVersion("m", null, "EUR", [new PriceRule(0, 24, null, 1, 1, 1)]));
    }

    // —— Local 平移（02-§4.5 例证）——

    [Fact]
    public void Pricing_Shift_DayOffsetRules()
    {
        // 正偏移整段越日：Start=20,End=24,shift=+8 → [4,8) days+1
        var shifted = PricingShift.ShiftPriceRulesToLocal(
            [new PriceRule(20, 24, [5], 1, 1, 1)], 480);
        var r = Assert.Single(shifted);
        Assert.Equal(4, r.Start);
        Assert.Equal(8, r.End);
        Assert.Equal([6], r.Days); // 周五 → 周六

        // 负偏移跨午夜：Start=2,End=10,shift=-8 → [18,24) days−1 与 [0,2) days+0
        var shifted2 = PricingShift.ShiftPriceRulesToLocal(
            [new PriceRule(2, 10, [3], 1, 1, 1)], -480);
        Assert.Equal(2, shifted2.Count);
        // 排序后 [0,2) 在前、[18,24) 在后（02 §4.5 例证：days−1 与 days+0 两段）
        Assert.Equal(0, shifted2[0].Start);
        Assert.Equal(2, shifted2[0].End);
        Assert.Equal([3], shifted2[0].Days);
        Assert.Equal(18, shifted2[1].Start);
        Assert.Equal(24, shifted2[1].End);
        Assert.Equal([2], shifted2[1].Days); // 周三 → 周二

        // 全天平移：[0,24)+8 → [8,24) days+0 与 [0,8) days+1（两段 days 不同不合并）
        var shifted3 = PricingShift.ShiftPriceRulesToLocal(
            [new PriceRule(0, 24, [1], 1, 1, 1)], 480);
        Assert.Equal(2, shifted3.Count);
        // 排序后 [0,8)（次日，days+1）在前、[8,24)（当日）在后（days 不同不合并）
        Assert.Equal(0, shifted3[0].Start);
        Assert.Equal(8, shifted3[0].End);
        Assert.Equal([2], shifted3[0].Days);
        Assert.Equal(8, shifted3[1].Start);
        Assert.Equal(24, shifted3[1].End);
        Assert.Equal([1], shifted3[1].Days);
    }

    [Fact]
    public void Pricing_Shift_PeriodsCrossMidnightSplitAndMerge()
    {
        // 倍率时段跨午夜拆分 + 同值合并：[0,8)+8 → [8,16)；[8,24)+8 → [16,32) 拆 [16,24)+[0,8)
        // 配置 [0,8)=1.0、[8,24)=0.5 → Local [0,8)=0.5、[8,16)=1.0、[16,24)=0.5
        var shifted = PricingShift.ShiftPeriodsToLocal(
        [
            new MultiplierPeriod(0, 8, 1.0),
            new MultiplierPeriod(8, 24, 0.5),
        ], 480);
        Assert.Equal(3, shifted.Count);
        Assert.Equal((0, 8, 0.5), (shifted[0].Start, shifted[0].End, shifted[0].Rate));
        Assert.Equal((8, 16, 1.0), (shifted[1].Start, shifted[1].End, shifted[1].Rate));
        Assert.Equal((16, 24, 0.5), (shifted[2].Start, shifted[2].End, shifted[2].Rate));
    }

    // —— [S3] 平移缓存 ——

    [Fact]
    public void Pricing_ShiftCache_InvalidatedOnOffsetAndConfigChange()
    {
        // [S3] 同 (model,版本,offset) 二次报价零构建；offset/Update*/SetEffectiveContext 后重建；不同生效日期各建各表
        using var tmp = new TempDir();
        var e = NewEngine(tmp.Path);
        e.UpdateMultiplierVersion("P/m", "2026-01-01", [new MultiplierPeriod(0, 24, 2.0)]);
        e.UpdateMultiplierVersion("P/m", "2026-06-01", [new MultiplierPeriod(0, 24, 3.0)]);
        e.SetEffectiveContext("utc", 480);
        var tsJan = Ts(2026, 1, 15, 10);
        var tsJul = Ts(2026, 7, 15, 10);

        var build0 = e.ShiftBuildCount;
        _ = e.GetMultiplier("P/m", tsJan, BucketScope.Local);
        var build1 = e.ShiftBuildCount;
        Assert.Equal(build0 + 1, build1);
        // 同键二次 → 命中缓存，零新增
        _ = e.GetMultiplier("P/m", Ts(2026, 1, 16, 10), BucketScope.Local); // 同生效日期(1 月版本)
        Assert.Equal(build1, e.ShiftBuildCount);
        // 不同生效日期版本 → 各建各表
        _ = e.GetMultiplier("P/m", tsJul, BucketScope.Local);
        Assert.Equal(build1 + 1, e.ShiftBuildCount);
        // 口径变更 → 缓存整体失效 → 重建
        e.SetEffectiveContext("utc", 0);
        _ = e.GetMultiplier("P/m", tsJan, BucketScope.Local);
        Assert.Equal(build1 + 2, e.ShiftBuildCount);
        // 版本更新 → 失效重建
        e.UpdateMultiplierVersion("P/m", "2026-01-01", [new MultiplierPeriod(0, 24, 5.0)]);
        _ = e.GetMultiplier("P/m", tsJan, BucketScope.Local);
        Assert.Equal(build1 + 3, e.ShiftBuildCount);
    }
}

/// <summary>临时目录包装（using 结束时清理）。</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir() => Path = TestHelpers.TempDir();

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
