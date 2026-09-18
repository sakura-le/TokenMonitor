using System.Text.Json;
using System.Text.Json.Serialization;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Storage;

/// <summary>手动补录（校准）与回滚契约（01-§2.3.8）。方法级串行；重入抛 InvalidOperationException。</summary>
public interface ICalibrationService
{
    /// <summary>data/calibrate_state.json 当前状态（回滚项置灰依据）。</summary>
    CalibrateState State { get; }

    /// <summary>补录：Backup → InsertEstimatedUsage(均分 count 份、余数入最后一笔) → ClearMissedByModel →
    /// RecalcDerived → RebuildToday → 持久化 HasCalibrated → 触发 CalibrateCompleted。返回备份路径。</summary>
    string ApplyManualCalibrate(string provider, string model, int requestCount,
                                long cacheHit, long cacheMiss, long output);

    /// <summary>回滚：无记录/备份缺失 → InvalidOperationException；成功后 HasCalibrated=false（该轮备份已消费）。</summary>
    void RollbackLastCalibration();
}

/// <summary>补录成功事件（托盘启用"回滚上一轮数据"）。</summary>
public sealed record CalibrateCompleted(string BackupPath) : TokenMonitor.Core.Events.IEvent;

/// <summary>
/// ICalibrationService 默认实现（02-§6）：
/// 均分拆分（余数入最后一笔，原 splitManualRows 逐字保持）→ 备份 → 事务直写 estimated=1 →
/// 清该模型漏抓 → [C1] UPSERT 重算 → 整桶热重载（不重启监听，D6-1）→ 状态持久化 → 事件。
/// </summary>
public sealed class CalibrationService : ICalibrationService
{
    private readonly Store _store;
    private readonly RateFunc? _rateFn;
    private readonly CostFunc? _costFn;
    private readonly Action _rebuildToday;
    private readonly string _statePath;
    private readonly TokenMonitor.Core.Events.IEventBus _bus;
    private readonly TimeProvider _clock;

    public CalibrationService(Store store, RateFunc? rateFn, CostFunc? costFn, Action rebuildToday,
                              string dataDir, TokenMonitor.Core.Events.IEventBus bus, TimeProvider? clock = null)
    {
        _store = store;
        _rateFn = rateFn;
        _costFn = costFn;
        _rebuildToday = rebuildToday;
        _statePath = Path.Combine(dataDir, "calibrate_state.json");
        _bus = bus;
        _clock = clock ?? TimeProvider.System;
    }

    public CalibrateState State => LoadState();

    public string ApplyManualCalibrate(string provider, string model, int requestCount,
                                       long cacheHit, long cacheMiss, long output)
    {
        if (Monitor.IsEntered(this))
            throw new InvalidOperationException("补录流程不支持重入");
        lock (this)
        {
            // 入口校验（原 handleCalibrateApply）：provider/model 非空；count&lt;1 按 1；全 0 → ArgumentException
            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(model))
                throw new ArgumentException("provider/model 不能为空");
            if (requestCount < 1) requestCount = 1;
            if (cacheHit == 0 && cacheMiss == 0 && output == 0)
                throw new ArgumentException("token 数值均为 0，无需补录");

            var rows = SplitManualRows(provider, model, requestCount, cacheHit, cacheMiss, output);
            var backup = _store.Backup(); // 先 Flush + VACUUM INTO
            _store.InsertEstimatedUsage(rows); // 事务直写 estimated=1
            _store.ClearMissedByModel(provider, model); // 清该模型漏抓 → 红框消失（原语义）
            _store.RecalcDerived(_rateFn, _costFn); // [C1] UPSERT：当日无 daily 行也会生成
            _rebuildToday(); // 整桶热重载（不重启监听；D6-1）
            _store.LogOp(provider + "/" + model, "manual_calibrate",
                $"补录 count={requestCount} hit={cacheHit} miss={cacheMiss} output={output} 备份={backup}", "");
            SaveState(new CalibrateState(true, backup));
            _bus.Publish(new CalibrateCompleted(backup));
            return backup;
        }
    }

    public void RollbackLastCalibration()
    {
        if (Monitor.IsEntered(this))
            throw new InvalidOperationException("回滚流程不支持重入");
        lock (this)
        {
            var state = LoadState();
            if (!state.HasCalibrated || string.IsNullOrEmpty(state.LastBackup))
                throw new InvalidOperationException("尚无上一轮校准记录");
            if (!File.Exists(state.LastBackup))
                throw new InvalidOperationException("备份文件不存在: " + state.LastBackup);
            _store.ReplaceDatabase(state.LastBackup); // [C6]：关连接→删侧车→覆盖→同 DSN 重开
            _rebuildToday();
            SaveState(new CalibrateState(false, "")); // 该轮备份已消费；再次回滚前需新校准（原语义）
            _store.LogOp("all", "rollback_calibrate", "回滚到 " + state.LastBackup, "");
        }
    }

    /// <summary>均分拆分（02-§6.1，原 splitManualRows 逐字保持）：各分量整数均分，余数全部记入最后一笔；prompt=hit+miss；total=prompt+output。</summary>
    internal static IReadOnlyList<UsageLogRow> SplitManualRows(string provider, string model,
                                                               int count, long hit, long miss, long output)
    {
        if (count < 1) count = 1;
        var bH = hit / count; var rH = hit % count;
        var bM = miss / count; var rM = miss % count;
        var bO = output / count; var rO = output % count;
        var ts = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds(); // 全部同一秒（原语义）
        var rows = new List<UsageLogRow>(count);
        for (var i = 0; i < count; i++)
        {
            var last = i == count - 1;
            var h = bH + (last ? rH : 0);
            var m = bM + (last ? rM : 0);
            var o = bO + (last ? rO : 0);
            var prompt = h + m; // prompt=hit+miss 恒等式
            rows.Add(new UsageLogRow(ts, provider, model, prompt, h, m, o, 0, prompt + o));
        }
        return rows;
    }

    private CalibrateState LoadState()
    {
        try
        {
            if (!File.Exists(_statePath)) return new CalibrateState(false, "");
            var state = JsonSerializer.Deserialize<StateDto>(File.ReadAllText(_statePath), JsonOpts);
            return state is null ? new CalibrateState(false, "") : new CalibrateState(state.HasCalibrated, state.LastBackup ?? "");
        }
        catch (Exception ex)
        {
            Logger.Warn("Calibrate", $"读取校准状态失败: {ex.Message}");
            return new CalibrateState(false, "");
        }
    }

    private void SaveState(CalibrateState s)
    {
        try
        {
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _statePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new StateDto(s.HasCalibrated, s.LastBackup), JsonOpts),
                new System.Text.UTF8Encoding(false));
            if (File.Exists(_statePath)) File.Replace(tmp, _statePath, null);
            else File.Move(tmp, _statePath);
        }
        catch (Exception ex)
        {
            Logger.Warn("Calibrate", $"持久化校准状态失败: {ex.Message}");
        }
    }

    private sealed record StateDto(
        [property: JsonPropertyName("has_calibrated")] bool HasCalibrated,
        [property: JsonPropertyName("last_backup")] string? LastBackup);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
}
