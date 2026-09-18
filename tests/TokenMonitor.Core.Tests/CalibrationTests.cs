using TokenMonitor.Core.Events;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>手动补录/回滚回归：C1 端到端 + §6.1 余数分配 + 重入防护。</summary>
public class CalibrationTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Store _store;
    private readonly EventBus _bus = new();

    public CalibrationTests()
    {
        _store = new Store(_dir.Path, flushInterval: TimeSpan.FromMilliseconds(50));
    }

    public void Dispose() => _store.Dispose();

    private CalibrationService NewService(Action? onRebuild = null, RateStub? rate = null)
    {
        RateFunc rateFn = rate ?? new RateStub();
        return new CalibrationService(_store, rateFn, (_, _, _, _, _, _, _) => (0.5, 0.25),
            onRebuild ?? (() => { }), _dir.Path, _bus);
    }

    private sealed class RateStub
    {
        public double Value = 2.0;
        public static implicit operator RateFunc(RateStub s) => (_, _, _, _) => s.Value;
    }

    [Fact]
    public void Calibration_SplitManualRows_RemainderToLast()
    {
        // §6.1 均分拆分（原 splitManualRows 逐字语义）：count=3, hit=10, miss=0, output=7
        // → 各分量 b=3/0/2 rem=1/0/1 → 行 h=[3,3,4]、m=[0,0,0]、o=[2,2,3]；prompt=h+m；total=prompt+o
        var rows = CalibrationService.SplitManualRows("P", "m", 3, 10, 0, 7);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(rows[0].Ts, r.Ts)); // 全部同一秒（原语义）

        Assert.Equal((3, 0, 2), (rows[0].CacheHitTokens, rows[0].CacheMissTokens, rows[0].CompletionTokens));
        Assert.Equal((3, 0, 2), (rows[1].CacheHitTokens, rows[1].CacheMissTokens, rows[1].CompletionTokens));
        Assert.Equal((4, 0, 3), (rows[2].CacheHitTokens, rows[2].CacheMissTokens, rows[2].CompletionTokens)); // 余数入最后一笔
        Assert.Equal(3, rows[0].PromptTokens);  // prompt = h + m
        Assert.Equal(5, rows[0].TotalTokens);   // total = prompt + o
        Assert.Equal(4, rows[2].PromptTokens);
        Assert.Equal(7, rows[2].TotalTokens);
        // 聚合守恒：Σ = 录入总量
        Assert.Equal(10, rows.Sum(r => r.CacheHitTokens));
        Assert.Equal(0, rows.Sum(r => r.CacheMissTokens));
        Assert.Equal(7, rows.Sum(r => r.CompletionTokens));
    }

    [Fact]
    public void Calibration_SplitManualRows_CountClampedToOne()
    {
        var rows = CalibrationService.SplitManualRows("P", "m", 0, 10, 0, 5);
        Assert.Single(rows);
        Assert.Equal(10, rows[0].CacheHitTokens);
    }

    [Fact]
    public void Calibration_ApplyIntoEmptyDay_VisibleInUtcView()
    {
        // [C1 端到端] 当日无 daily 行的模型补录 → usage_log 有 estimated=1 行、daily 行生成、
        // GetTodayUtc/快照可见、漏抓清零
        var rate = new RateStub { Value = 2.0 };
        // 预置漏抓 2 条（校准后清零）
        _store.AddMissed(new MissedLogRow(0, TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(), "P", "m", 200, "响应无 usage 块"));
        _store.AddMissed(new MissedLogRow(0, TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(), "P", "m", 200, "响应无 usage 块"));

        var rebuilt = false;
        var svc = NewService(() => rebuilt = true, rate);
        var backup = svc.ApplyManualCalibrate("P", "m", 3, 10, 0, 7);

        Assert.True(File.Exists(backup));                       // 备份已生成
        var logs = _store.GetLogsByRange(0, long.MaxValue);
        Assert.True(logs.Count == 3, "应恰好 3 笔补录行");                            // 3 笔 estimated 行
        var daily = _store.QueryDailyRange("2000-01-01", "2099-12-31").Single();
        Assert.Equal(10, daily.PromptTokens);                   // 3+3+4（prompt=hit+miss）
        Assert.Equal(7, daily.CompletionTokens);                // 2+2+3
        Assert.Equal(17, daily.TotalTokens);                    // 10+7（total=prompt+output）
        Assert.True(rebuilt);                                   // RebuildToday 已调用
        Assert.Empty(_store.GetMissed(0, long.MaxValue)); // 漏抓清零
        var state = svc.State;
        Assert.True(state.HasCalibrated);
        Assert.Equal(backup, state.LastBackup);
        // UTC 视图可见（daily 由 [C1] UPSERT 生成，倍率按 stub rate=2.0 冻结）
        Assert.Equal(20, daily.MulPrompt);      // Σ prompt×2.0 = (3+3+4)×2
        Assert.Equal(14, daily.MulCompletion);   // (2+2+3)×2
        Assert.Equal(34, daily.MulTotal);       // 分量和
    }

    [Fact]
    public async Task Calibration_Apply_PublishesCalibrateCompleted()
    {
        var svc = NewService();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = _bus.Subscribe<CalibrateCompleted>(e => tcs.TrySetResult(e.BackupPath));
        var backup = svc.ApplyManualCalibrate("P", "m", 1, 10, 0, 5);
        Assert.Equal(backup, await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Calibration_Validation()
    {
        var svc = NewService();
        Assert.Throws<ArgumentException>(() => svc.ApplyManualCalibrate("", "m", 1, 10, 0, 5));
        Assert.Throws<ArgumentException>(() => svc.ApplyManualCalibrate("P", "m", 1, 0, 0, 0)); // 全 0
        var rows = svc.ApplyManualCalibrate("P", "m", 0, 10, 0, 5); // count<1 → 按 1
        Assert.NotNull(rows);
    }

    [Fact]
    public void Calibration_Reentry_Throws()
    {
        // 重入防护：rebuild 回调内再次 Apply → InvalidOperationException
        CalibrationService? svcRef = null;
        var rethrown = false;
        var svc = NewService(() =>
        {
            try
            {
                svcRef!.ApplyManualCalibrate("P", "m2", 1, 1, 0, 1);
            }
            catch (InvalidOperationException)
            {
                rethrown = true;
            }
        });
        svcRef = svc;
        svc.ApplyManualCalibrate("P", "m", 1, 10, 0, 5);
        Assert.True(rethrown);
    }

    [Fact]
    public void Calibration_Rollback_RestoresDatabase()
    {
        // 回滚端到端：补录 → DB 有数据 → 回滚 → DB 回到备份内容 → 二次回滚抛异常
        var svc = NewService();
        var tsBefore = _store.GetLogsByRange(0, long.MaxValue).Count;
        var backup = svc.ApplyManualCalibrate("P", "m", 2, 10, 0, 5);
        Assert.True(_store.GetLogsByRange(0, long.MaxValue).Count > tsBefore);

        svc.RollbackLastCalibration();

        Assert.Empty(_store.GetLogsByRange(0, long.MaxValue)); // 回滚到补录前（空库）
        Assert.False(svc.State.HasCalibrated);                 // 该轮备份已消费
        Assert.Equal("", svc.State.LastBackup);
        Assert.Contains(_store.GetOpLogs(null), o => o.Action == "rollback_calibrate");

        var missing = Assert.Throws<InvalidOperationException>(() => svc.RollbackLastCalibration());
        Assert.Contains("尚无", missing.Message);
    }

    [Fact]
    public void Calibration_Rollback_MissingBackupFile_Throws()
    {
        var svc = NewService();
        svc.ApplyManualCalibrate("P", "m", 1, 10, 0, 5);
        var state = svc.State;
        File.Delete(state.LastBackup); // 模拟备份被删
        var ex = Assert.Throws<InvalidOperationException>(() => svc.RollbackLastCalibration());
        Assert.Contains("备份文件不存在", ex.Message);
    }

    [Fact]
    public void Calibration_Rollback_WithoutRecord_Throws()
    {
        var svc = NewService();
        Assert.Throws<InvalidOperationException>(() => svc.RollbackLastCalibration());
    }
}
