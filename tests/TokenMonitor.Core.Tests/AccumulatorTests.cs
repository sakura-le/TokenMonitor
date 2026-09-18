using TokenMonitor.Core.Events;
using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Stats;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>Accumulator/Coordinator 回归：C3/C5/S1/S4 + 原 stats_test 两个 Orchestrated 移植。</summary>
public class AccumulatorTests
{
    private static long Ts(int y, int m, int d, int hour, int minute = 0) =>
        new DateTimeOffset(y, m, d, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static PricingEngine NewEngine(TempDir tmp)
    {
        var e = new PricingEngine(Path.Combine(tmp.Path, "pricing.json"));
        return e;
    }

    // —— 原 stats_test TestCostPerViewOrchestrated 移植 ——

    [Fact]
    public void Stats_CostPerView_Orchestrated()
    {
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        pcfg.UpdatePricingVersion("P/m", null, "CNY",
        [
            new PriceRule(0, 8, null, 1, 0.1, 2),   // 闲时
            new PriceRule(8, 24, null, 3, 0.3, 6),  // 峰值
        ]);
        pcfg.SetEffectiveContext("utc", 480);

        var usage = new UnifiedUsage(1000, 800, 200, 100, 0, 1100);
        const double offPeak = 800 * 0.1 / 1e6 + 200 * 1 / 1e6 + 100 * 2 / 1e6;
        const double peak = 800 * 0.3 / 1e6 + 200 * 3 / 1e6 + 100 * 6 / 1e6;

        // ts=UTC 00:30 = 本地 08:30 → 两视图均落闲时段（UTC[0,8) 与 Local[8,16)）
        var acc1 = new Accumulator(pcfg);
        acc1.AddUsage(new UsageEvent("P", "m", usage, Ts(2026, 8, 19, 0, 30), CaptureSource.Json));
        var s1 = acc1.Snapshot(new Dictionary<string, long>());
        Assert.Equal(offPeak, s1.UtcModels[0].CostCNY, 12);
        Assert.Equal(offPeak, s1.LocalModels[0].CostCNY, 12);

        // ts=UTC 09:00 = 本地 17:00 → 两视图均落峰值段
        var acc2 = new Accumulator(pcfg);
        acc2.AddUsage(new UsageEvent("P", "m", usage, Ts(2026, 8, 19, 9, 0), CaptureSource.Json));
        var s2 = acc2.Snapshot(new Dictionary<string, long>());
        Assert.Equal(peak, s2.UtcModels[0].CostCNY, 12);
        Assert.Equal(peak, s2.LocalModels[0].CostCNY, 12);

        // 跨午夜回归：ts=UTC 20:00 = 本地 04:00 → Local 视图必须用平移后的 Local 时段
        var acc3 = new Accumulator(pcfg);
        acc3.AddUsage(new UsageEvent("P", "m", usage, Ts(2026, 8, 19, 20, 0), CaptureSource.Json));
        var s3 = acc3.Snapshot(new Dictionary<string, long>());
        Assert.Equal(peak, s3.UtcModels[0].CostCNY, 12);
        Assert.Equal(peak, s3.LocalModels[0].CostCNY, 12);
        Assert.NotEqual(offPeak, s3.LocalModels[0].CostCNY, 12); // 旧逻辑（本地小时配 UTC 时段）会误得闲时价
    }

    // —— 原 stats_test TestMultiplierPerViewOrchestrated 移植 ——

    [Fact]
    public void Stats_MultiplierPerView_Orchestrated()
    {
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        pcfg.UpdateMultiplierVersion("P/m", null,
        [
            new MultiplierPeriod(0, 14, 1.0),
            new MultiplierPeriod(14, 24, 0.5),
        ]);
        pcfg.SetEffectiveContext("utc", 480);

        var usage = new UnifiedUsage(1000, 0, 1000, 100, 0, 1100);
        var acc = new Accumulator(pcfg);
        // 本地 01:00 = UTC 17:00 → 两视图均 0.5
        acc.AddUsage(new UsageEvent("P", "m", usage, Ts(2026, 9, 1, 17, 0), CaptureSource.Json));
        // 本地 12:00 = UTC 04:00 → 两视图均 1.0
        acc.AddUsage(new UsageEvent("P", "m", usage, Ts(2026, 9, 1, 4, 0), CaptureSource.Json));

        var snap = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(1500, snap.UtcModels[0].MulPrompt);  // 500 + 1000
        Assert.Equal(1500, snap.LocalModels[0].MulPrompt); // 500 + 1000

        // 关键回归点：本地凌晨单笔 → Local 视图按本地小时匹配 Local 时段
        var acc2 = new Accumulator(pcfg);
        acc2.AddUsage(new UsageEvent("P", "m", usage, Ts(2026, 9, 1, 17, 0), CaptureSource.Json));
        var snap2 = acc2.Snapshot(new Dictionary<string, long>());
        Assert.Equal(500, snap2.LocalModels[0].MulPrompt);
        Assert.Equal(500, snap2.UtcModels[0].MulPrompt);
    }

    // —— [C3] 两桶 total 同源 ——

    [Fact]
    public void Stats_TotalUnification_BothBucketsUseSameTotal()
    {
        // [C3] total=999（≠ prompt+completion=1100）→ 两桶 TotalTokens 均 999（原 Local 桶用 p+c 被否定）
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        var acc = new Accumulator(pcfg);
        acc.AddUsage(new UsageEvent("P", "m", new UnifiedUsage(500, 100, 400, 600, 0, 999),
            Ts(2026, 9, 1, 10), CaptureSource.Json));
        var snap = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(999, snap.UtcModels[0].TotalTokens);
        Assert.Equal(999, snap.LocalModels[0].TotalTokens);
    }

    [Fact]
    public void Accumulator_DeltaTokens_LastRequestWins()
    {
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        var acc = new Accumulator(pcfg);
        acc.AddUsage(new UsageEvent("P", "m", new UnifiedUsage(100, 0, 100, 10, 0, 110),
            Ts(2026, 9, 1, 10), CaptureSource.Json));
        acc.AddUsage(new UsageEvent("P", "m", new UnifiedUsage(200, 0, 200, 20, 0, 220),
            Ts(2026, 9, 1, 11), CaptureSource.Json));
        var snap = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(220, snap.UtcModels[0].DeltaTokens); // 最近一笔的 total
        Assert.Equal(330, snap.UtcModels[0].TotalTokens); // 累计
        Assert.Equal(Ts(2026, 9, 1, 11), snap.UtcModels[0].LastActiveUnix);
    }

    // —— [S1] 整桶替换 ——

    [Fact]
    public void Accumulator_ReplaceUtcBucket_FullReplaceResetsDeltaAndLeavesLocal()
    {
        // [S1] 注入含 Delta 的两桶数据 + Local 值 → ReplaceUtcBucket(空) → ReplaceUtcBucket(行)：
        // Delta=0、UTC 值==行值（不重报价）、Local 桶不变
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        pcfg.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(0, 24, 2.0)]);
        var acc = new Accumulator(pcfg);
        acc.AddUsage(new UsageEvent("P", "m", new UnifiedUsage(100, 0, 100, 10, 0, 110),
            Ts(2026, 9, 1, 10), CaptureSource.Json));
        Assert.Equal(110, acc.Snapshot(new Dictionary<string, long>()).UtcModels[0].DeltaTokens);

        acc.ReplaceUtcBucket([]); // 空列表 = 清空
        var afterClear = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(0, afterClear.UtcModels[0].TotalTokens);
        Assert.Equal(0, afterClear.UtcModels[0].DeltaTokens);
        Assert.Equal(110, afterClear.LocalModels[0].TotalTokens); // Local 桶不受影响
        Assert.Equal(200, afterClear.LocalModels[0].MulPrompt);   // Local 倍率也保留

        var row = new TokenMonitor.Core.Storage.ModelDailyAggregate(
            "2026-09-01", "P", "m", 300, 0, 300, 30, 0, 330, 3,
            660, 600, 0, 600, 60, 0, 1.5, 0.25);
        acc.ReplaceUtcBucket([row]); // 行值直接置入（不重新报价）
        var restored = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(330, restored.UtcModels[0].TotalTokens);
        Assert.Equal(660, restored.UtcModels[0].MulTotal);
        Assert.Equal(1.5, restored.UtcModels[0].CostCNY, 12);
        Assert.Equal(0, restored.UtcModels[0].DeltaTokens); // [S1] 恢复即归零
        Assert.Equal(110, restored.LocalModels[0].TotalTokens);
    }

    [Fact]
    public void Accumulator_ReplaceLocalBucket_RequotesPerRow()
    {
        // Local 桶重建：逐行按本地分数小时重新取倍率/成本（与 AddUsage 的 Local 路径同口径）
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        pcfg.UpdateMultiplierVersion("P/m", null, [new MultiplierPeriod(0, 6, 0.5), new MultiplierPeriod(6, 24, 2.0)]);
        pcfg.SetEffectiveContext("utc", 480);
        var acc = new Accumulator(pcfg);
        // UTC 22:00 = 本地 06:00（次日）→ Local 6.0 → 2.0 段（含边界）
        var rows = new List<LocalReplayRow>
        {
            new(Ts(2026, 9, 1, 22, 0), "P", "m", new UnifiedUsage(100, 0, 100, 10, 0, 110)),
        };
        acc.ReplaceLocalBucket(rows);
        var snap = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(200, snap.LocalModels[0].MulPrompt); // 100 × 2.0
        // UTC 02:00 = 本地 10:00 → 平移后 Local [8,14)=0.5 段（UTC 02:00 ∈ [0,6) 原段）
        acc.ReplaceLocalBucket([new LocalReplayRow(Ts(2026, 9, 1, 2, 0), "P", "m",
            new UnifiedUsage(100, 0, 100, 10, 0, 110))]);
        snap = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(50, snap.LocalModels[0].MulPrompt); // 整桶替换后仅此一笔 ×0.5
    }

    [Fact]
    public void Accumulator_ResetSemantics()
    {
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        var acc = new Accumulator(pcfg);
        acc.AddUsage(new UsageEvent("P", "m", new UnifiedUsage(100, 0, 100, 10, 0, 110),
            Ts(2026, 9, 1, 10), CaptureSource.Json));
        Assert.True(acc.HasModel("P", "m"));

        acc.ResetUtcBucket(); // 保留条目，UTC 清零
        var s1 = acc.Snapshot(new Dictionary<string, long>());
        Assert.Equal(0, s1.UtcModels[0].TotalTokens);
        Assert.Equal(110, s1.LocalModels[0].TotalTokens);

        acc.ResetModel("P/m"); // 两桶清零，条目保留
        var s2 = acc.Snapshot(new Dictionary<string, long>());
        Assert.Single(s2.UtcModels);
        Assert.True(acc.HasModel("P", "m"));

        acc.ResetAllBuckets(); // 条目删除
        Assert.False(acc.HasModel("P", "m"));
        Assert.Empty(acc.Snapshot(new Dictionary<string, long>()).UtcModels);

        acc.SeedModelSkeleton("P", "m"); // 骨架补回
        var s3 = acc.Snapshot(new Dictionary<string, long>());
        Assert.Single(s3.UtcModels);
        Assert.Equal(0, s3.UtcModels[0].TotalTokens);
    }

    // —— [S4] 漏抓求和 ——

    [Fact]
    public void Snapshot_MissedCaptures_SumsAllModelsIncludingCardless()
    {
        // [S4] 模型 X 无卡片（不在累积器）但有 3 条 missed → MissedCaptures 含 3
        using var tmp = new TempDir();
        var pcfg = NewEngine(tmp);
        var acc = new Accumulator(pcfg);
        acc.AddUsage(new UsageEvent("P", "m", new UnifiedUsage(100, 0, 100, 10, 0, 110),
            Ts(2026, 9, 1, 10), CaptureSource.Json));
        var missed = new Dictionary<string, long>
        {
            ["P/m"] = 2,
            ["Q/ghost"] = 3, // 无卡片模型
        };
        var snap = acc.Snapshot(missed);
        Assert.Equal(5, snap.MissedCaptures); // 全字典求和（原实现只数快照内模型 → 漏 3）
        Assert.Equal(2, snap.UtcModels[0].MissedCount);
        Assert.Equal(2, snap.LocalModels[0].MissedCount);
    }

    // —— [C5] 并发压测 ——

    [Fact]
    public async Task Coordinator_ConcurrentIngestAndRebuild_NoLossNoDeadlock()
    {
        // [C5] N 线程持续 Ingest，同时 K 次 RebuildToday（限时）：注入总量==快照累计、无死锁
        using var tmp = new TempDir();
        var pricing = new PricingEngine(Path.Combine(tmp.Path, "pricing.json"));
        var store = new Store(tmp.Path, flushInterval: TimeSpan.FromMilliseconds(50));
        var fileLog = new UsageFileLogger(tmp.Path, 0);
        var coordinator = new UsageCoordinator(new Accumulator(pricing), store, fileLog, pricing,
            new EventBus(), missedCacheTtl: TimeSpan.FromMilliseconds(20));
        const int threads = 8, perThread = 50;
        var total = threads * perThread;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var rebuildTask = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                coordinator.RebuildToday();
                await Task.Delay(5);
            }
        });
        Parallel.For(0, threads, t =>
        {
            for (var i = 0; i < perThread; i++)
            {
                coordinator.Ingest(new UsageEvent("P", $"m{t}", new UnifiedUsage(10, 0, 10, 1, 0, 11), now, CaptureSource.Json));
            }
        });
        await rebuildTask.WaitAsync(TimeSpan.FromSeconds(30)); // 限时完成 = 无死锁

        coordinator.RebuildToday(); // 最终一致性重建
        var snap = coordinator.BuildSnapshot();
        Assert.Equal(total, snap.UtcModels.Sum(m => m.RequestCount));
        Assert.Equal(total, snap.LocalModels.Sum(m => m.RequestCount));
        store.Dispose();
    }

    [Fact]
    public async Task Coordinator_IngestBlockedDuringRebuild_ReplayedToNewBuckets()
    {
        // [C5b] 钩子令 Ingest 停在中点 → RebuildToday 阻塞等待 → 事件计入新桶恰一次
        using var tmp = new TempDir();
        var pricing = new PricingEngine(Path.Combine(tmp.Path, "pricing.json"));
        var store = new Store(tmp.Path, flushInterval: TimeSpan.FromMilliseconds(50));
        var fileLog = new UsageFileLogger(tmp.Path, 0);
        var coordinator = new UsageCoordinator(new Accumulator(pricing), store, fileLog, pricing,
            new EventBus(), missedCacheTtl: TimeSpan.FromMilliseconds(20));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        coordinator.IngestMidpointHookForTest = () =>
        {
            entered.TrySetResult();
#pragma warning disable xUnit1031 // 测试钩子必须同步阻塞（模拟在途摄入中点）
            release.Task.Wait(TimeSpan.FromSeconds(10));
#pragma warning restore xUnit1031
        };

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var ingestTask = Task.Run(() => coordinator.Ingest(
            new UsageEvent("P", "m", new UnifiedUsage(100, 0, 100, 10, 0, 110), now, CaptureSource.Json)));
        await entered.Task; // Ingest 已持锁停在中点
        var rebuildTask = Task.Run(() => coordinator.RebuildToday()); // 应阻塞在锁上
        await Task.Delay(100);
        Assert.False(rebuildTask.IsCompleted, "RebuildToday 未等待在途 Ingest（锁未互斥）");
        release.TrySetResult();
        await Task.WhenAll(ingestTask, rebuildTask).WaitAsync(TimeSpan.FromSeconds(10));

        var snap = coordinator.BuildSnapshot();
        Assert.Equal(1, snap.UtcModels[0].RequestCount); // 恰一次
        Assert.Equal(110, snap.UtcModels[0].TotalTokens);
        var logs = store.GetLogsByRange(0, long.MaxValue);
        _ = logs; // 重建后 log 行与内存桶一致
        Assert.Single(logs);
        store.Dispose();
    }
}
