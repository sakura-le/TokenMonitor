using System.Text;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>
/// "保存并生效"回归：修改计价/倍率配置并保存后，已显示的金额/数值是否立即变为新计算结果的。
/// 覆盖两条路径——(1) 引擎端到端（保存 → 全量重算 → 整桶重建 → ConfigChanged → 快照）；
/// (2) 重算委托与实时路径的生效日期口径一致性（mode=local 时 RecalcDerived 传 offsetMin=0）。
/// </summary>
public class PricingApplyTests
{
    private const string UsageJson =
        """{"model":"test-model","usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10,"completion_tokens":50,"total_tokens":150}}""";

    private static async Task PostAsync(TokenMonitor.Core.MonitorEngine engine)
    {
        using var client = new HttpClient();
        var resp = await client.PostAsync($"http://{engine.Proxy.ListenAddr}/v1/chat/completions",
            new StringContent("""{"model":"test-model"}""", Encoding.UTF8, "application/json"));
        Assert.True(resp.IsSuccessStatusCode);
        _ = await resp.Content.ReadAsStringAsync();
    }

    private static async Task<TokenMonitor.Core.MonitorEngine> StartAsync(TempDir dir, MockUpstream upstream)
    {
        var engine = TestHelpers.NewEngine(dir.Path);
        var port = TestHelpers.FreePort();
        engine.Config.SaveProxy(new TokenMonitor.Core.Config.ProxyConfig($"127.0.0.1:{port}", null,
        [
            new TokenMonitor.Core.Config.ProviderConfig("Up", upstream.BaseUrl, "sk-up", ["test-"], null, null),
        ]));
        await engine.StartAsync(CancellationToken.None);
        return engine;
    }

    [Fact]
    public async Task SaveNewPricingVersion_RecomputesDisplayedCost()
    {
        using var dir = new TempDir();
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse(UsageJson));
        var engine = await StartAsync(dir, upstream);
        try
        {
            var todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");

            // V1：input=2 cache=0.4 output=6
            engine.Pricing.UpdatePricingVersion("Up/test-model", todayUtc, "CNY",
                [new PriceRule(0, 24, null, 2, 0.4, 6)]);
            var costV1 = 90 * 0.4 / 1e6 + 10 * 2 / 1e6 + 50 * 6 / 1e6;

            await PostAsync(engine);
            var snap1 = await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcModels.Any(m => m.Model == "test-model" && m.RequestCount == 1));
            Assert.Equal(costV1, snap1.UtcModels[0].CostCNY, 10);

            // V2（同一生效日期 = 覆盖）：单价 ×10 → 显示金额应立即变为新值（两个口径都要变）
            engine.Pricing.UpdatePricingVersion("Up/test-model", todayUtc, "CNY",
                [new PriceRule(0, 24, null, 20, 4, 60)]);
            var costV2 = 90 * 4 / 1e6 + 10 * 20 / 1e6 + 50 * 60 / 1e6;

            var snap2 = await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcModels.Count > 0 && Math.Abs(s.UtcModels[0].CostCNY - costV2) < 1e-12,
                TimeSpan.FromSeconds(8));
            Assert.Equal(costV2, snap2.LocalModels[0].CostCNY, 10);
        }
        finally
        {
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task SaveNewMultiplierVersion_RecomputesDisplayedMultiplierTokens()
    {
        using var dir = new TempDir();
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse(UsageJson));
        var engine = await StartAsync(dir, upstream);
        try
        {
            var todayUtc = DateTime.UtcNow.ToString("yyyy-MM-dd");
            engine.Pricing.UpdateMultiplierVersion("Up/test-model", todayUtc,
                [new MultiplierPeriod(0, 24, 1.0)]);

            await PostAsync(engine);
            var snap1 = await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcModels.Any(m => m.Model == "test-model" && m.RequestCount == 1));
            Assert.Equal(150, snap1.UtcModels[0].MulTotal); // 150 × 1.0

            // 同一生效日期覆盖为 ×2 → 倍率视图数值应立即变为 300
            engine.Pricing.UpdateMultiplierVersion("Up/test-model", todayUtc,
                [new MultiplierPeriod(0, 24, 2.0)]);

            var snap2 = await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcModels.Count > 0 && s.UtcModels[0].MulTotal == 300,
                TimeSpan.FromSeconds(8));
            Assert.Equal(300, snap2.LocalModels[0].MulTotal);
        }
        finally
        {
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public void SaveNewPricingVersion_RePricesAlreadyPersistedPastDays()
    {
        // "修改之前显示的旧数值"：不只今天，已落库的历史日 daily 行也要按新版本重算（RecalcDerived 全量 UPSERT）。
        using var dir = new TempDir();
        using var store = new TokenMonitor.Core.Storage.Store(dir.Path, TimeProvider.System,
            TimeSpan.FromMilliseconds(50));
        var e = new PricingEngine(Path.Combine(dir.Path, "pricing.json"));
        CostFunc costFn = (key, ts, hour, offsetMin, hit, miss, comp) =>
            e.QuoteCost(key, ts, hour, offsetMin, hit, miss, comp);
        var past = TimeMath.UtcDayStart("2026-09-01") + 10 * 3600; // 历史日 10:00Z
        store.InsertEstimatedUsage(
            [new TokenMonitor.Core.Storage.UsageLogRow(past, "P", "m", 1_000_000, 0, 1_000_000, 0, 0, 1_000_000)]);

        e.UpdatePricingVersion("P/m", "2026-08-01", "CNY", [new PriceRule(0, 24, null, 1, 0, 0)]);
        store.RecalcDerived(null, costFn);
        Assert.Equal(1.0, store.QueryDailyRange("2026-09-01", "2026-09-01").Single().CostCNY, 10);

        // 保存生效日期落在该历史日的新版（覆盖）→ 重算 → 历史行立即变为新价
        e.UpdatePricingVersion("P/m", "2026-09-01", "CNY", [new PriceRule(0, 24, null, 7, 0, 0)]);
        store.RecalcDerived(null, costFn);
        Assert.Equal(7.0, store.QueryDailyRange("2026-09-01", "2026-09-01").Single().CostCNY, 10);
    }

    private static readonly IReadOnlyList<int> Weekdays = [1, 2, 3, 4, 5];
    private static readonly IReadOnlyList<int> Weekend = [6, 7];

    /// <summary>用户按本地时钟填写的一套规则（晚间 18–24 为优惠段；与厂商按北京时间计费同形）。</summary>
    private static readonly IReadOnlyList<PriceRule> LocalRules =
    [
        new(0, 9, Weekdays, 1, 0.02, 4),
        new(9, 12, Weekdays, 2, 0.04, 8),
        new(12, 14, Weekdays, 1, 0.02, 4),
        new(14, 18, Weekdays, 2, 0.04, 8),
        new(18, 24, Weekdays, 1, 0.02, 4),
        new(9, 24, Weekend, 1, 0.02, 4),
    ];

    [Fact]
    public void DialogRoundTrip_LocalBasisEditSave_AgreesWithLocalIntentInBothViews()
    {
        // 用户在"基准 LOCAL"下按本地时间填规则：晚间 18–24 是优惠段（收 cache 0.02/input 1/output 4）。
        // 对话框职责（02-§4.5）= 显示 +offset、落库 −offset；缺了换算就会被引擎当成 UTC 时段匹配，
        // 本地晚间落到峰时窗 → 金额比厂商后台高（实测 3.78 vs 2.2）。
        using var tmp = new TempDir();
        var e = new PricingEngine(Path.Combine(tmp.Path, "pricing.json"));
        e.SetEffectiveContext("local", 480);
        e.UpdatePricingVersion("P/m", "2026-09-01", "CNY", PricingShift.ShiftPriceRulesToLocal(LocalRules, -480));

        // 本地 2026-09-22（周二）22:00 = UTC 14:00 → 命中 18–24 优惠段
        var ts = new DateTimeOffset(2026, 9, 22, 14, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var expect = 1 * 0.02 + 1 * 1 + 1 * 4; // hit/miss/comp 各 1M 的折算

        Assert.Equal(expect, e.GetCost("P/m", ts, 1_000_000, 1_000_000, 1_000_000, BucketScope.Local).CostCNY, 6);
        Assert.Equal(expect, e.GetCost("P/m", ts, 1_000_000, 1_000_000, 1_000_000, BucketScope.Utc).CostCNY, 6);

        // 反证：不换算直接落库（旧行为）→ 同一笔被算成峰时 1×0.04+1×2+1×8 = 10.04
        using var tmp2 = new TempDir();
        var buggy = new PricingEngine(Path.Combine(tmp2.Path, "pricing.json"));
        buggy.SetEffectiveContext("local", 480);
        buggy.UpdatePricingVersion("P/m", "2026-09-01", "CNY", LocalRules);
        Assert.Equal(10.04,
            buggy.GetCost("P/m", ts, 1_000_000, 1_000_000, 1_000_000, BucketScope.Local).CostCNY, 6);
    }

    [Fact]
    public void DialogRoundTrip_IsPriceStableOverAFullWeek()
    {
        // 往返稳定性：存量（UTC）→(+off) 显示 →(−off) 落库 后，两个口径在整周每个半小时格上都应等价。
        using var tmpA = new TempDir();
        using var tmpB = new TempDir();
        var stored = PricingShift.ShiftPriceRulesToLocal(LocalRules, -480);
        var roundTripped = PricingShift.ShiftPriceRulesToLocal(
            PricingShift.ShiftPriceRulesToLocal(stored, 480), -480);

        var eA = new PricingEngine(Path.Combine(tmpA.Path, "pricing.json"));
        var eB = new PricingEngine(Path.Combine(tmpB.Path, "pricing.json"));
        foreach (var e in new[] { eA, eB })
        {
            e.SetEffectiveContext("local", 480);
            e.UpdatePricingVersion("P/m", "2026-09-01", "CNY", e == eA ? stored : roundTripped);
        }

        var baseTs = new DateTimeOffset(2026, 9, 14, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(); // 周一
        for (var i = 0; i < 7 * 48; i++)
        {
            var ts = baseTs + i * 1800L;
            foreach (var scope in new[] { BucketScope.Utc, BucketScope.Local })
            {
                var a = eA.GetCost("P/m", ts, 700_000, 300_000, 100_000, scope).CostCNY;
                var b = eB.GetCost("P/m", ts, 700_000, 300_000, 100_000, scope).CostCNY;
                Assert.Equal(a, b, 9);
            }
        }
    }

    [Fact]
    public void QuoteDelegates_SelectVersionOnModeCalendar_NotOnCallerScopeOffset()
    {
        // mode=local + UTC+8：UTC 09-21 20:00 = 本地 09-22 04:00 → 版本按"本地 09-22"选取。
        // RecalcDerived 以 (UTC 分数小时, offsetMin=0) 调委托；若用调用方 offset 推生效日期，
        // 会退回 UTC 日历选中旧版本 → 与实时路径不一致（重算后金额变成另一个数）。
        using var tmp = new TempDir();
        var e = new PricingEngine(Path.Combine(tmp.Path, "pricing.json"));
        e.SetEffectiveContext("local", 480);
        e.UpdatePricingVersion("P/m", "2026-09-01", "CNY", [new PriceRule(0, 24, null, 1, 0, 0)]);
        e.UpdatePricingVersion("P/m", "2026-09-22", "CNY", [new PriceRule(0, 24, null, 10, 0, 0)]);

        var ts = new DateTimeOffset(2026, 9, 21, 20, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        var live = e.GetCost("P/m", ts, 0, 1_000_000, 0, BucketScope.Utc).CostCNY; // 实时路径（UTC 口径）
        var (recalc, _) = e.QuoteCost("P/m", ts, TimeMath.UtcFractionalHour(ts), 0, 0, 1_000_000, 0); // 重算路径

        Assert.Equal(10.0, live, 10);
        Assert.Equal(live, recalc, 10);
    }
}
