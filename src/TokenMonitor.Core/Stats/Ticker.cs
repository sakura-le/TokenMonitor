using TokenMonitor.Core.Events;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Stats;

/// <summary>双频快照发布器：200ms 推 PanelStatsTick、1s 推 BallStatsTick（合并式发布=仅保留最新，§3.3）。
/// 替代原 200ms HTTP 轮询 /api/stats（C10 一部分）。间隔可注入（测试提速）。</summary>
public sealed class StatsTicker : IDisposable
{
    private readonly UsageCoordinator _coordinator;
    private readonly IEventBus _bus;
    private readonly TimeSpan _panelInterval;
    private readonly TimeSpan _ballInterval;
    private CancellationTokenSource? _cts;
    private Task? _panelLoop;
    private Task? _ballLoop;
    private volatile bool _started;

    public StatsTicker(UsageCoordinator coordinator, IEventBus bus,
                       TimeSpan? panelInterval = null, TimeSpan? ballInterval = null)
    {
        _coordinator = coordinator;
        _bus = bus;
        _panelInterval = panelInterval ?? TimeSpan.FromMilliseconds(200);
        _ballInterval = ballInterval ?? TimeSpan.FromSeconds(1);
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _cts = new CancellationTokenSource();
        _panelLoop = Task.Run(() => Loop(_coordinator, _panelInterval, snap => _bus.PublishCoalesced(new PanelStatsTick(snap)), _cts!.Token));
        _ballLoop = Task.Run(() => Loop(_coordinator, _ballInterval, snap => _bus.PublishCoalesced(new BallStatsTick(snap)), _cts!.Token));
    }

    private static async Task Loop(UsageCoordinator coordinator, TimeSpan interval, Action<StatsSnapshot> publish, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    publish(coordinator.BuildSnapshot());
                }
                catch (Exception ex)
                {
                    Logger.Warn("Stats", $"快照构建/发布失败: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _cts?.Cancel();
        try { Task.WaitAll([_panelLoop!, _ballLoop!], TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _cts = null;
    }
}

/// <summary>
/// 跨午夜守望（30s 周期，原 DayWatch 语义保持）：UTC 或本地日期变化 → RebuildToday + DayRolledOver。
/// offset 变化由 SetTimezone 主动处理，守望器仅更新基准并跳过一轮（原守望线程语义）。
/// </summary>
public sealed class DayWatch : IDisposable
{
    private readonly UsageCoordinator _coordinator;
    private readonly Action<string, string, int> _onRollover;
    private readonly Func<int> _offsetProvider;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private volatile bool _started;

    public DayWatch(UsageCoordinator coordinator, Action<string, string, int> onRollover,
                    Func<int> offsetProvider, TimeSpan? interval = null, TimeProvider? clock = null)
    {
        _coordinator = coordinator;
        _onRollover = onRollover;
        _offsetProvider = offsetProvider;
        _interval = interval ?? TimeSpan.FromSeconds(30);
        _clock = clock ?? TimeProvider.System;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _cts = new CancellationTokenSource();
        var lastOffset = _offsetProvider();
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var lastUtc = TimeMath.UtcDate(now);
        var lastLocal = TimeMath.LocalDate(now, lastOffset);
        _loop = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(_interval);
                while (await timer.WaitForNextTickAsync(_cts!.Token))
                {
                    try
                    {
                        var offset = _offsetProvider();
                        var ts = _clock.GetUtcNow().ToUnixTimeSeconds();
                        var utcToday = TimeMath.UtcDate(ts);
                        var localToday = TimeMath.LocalDate(ts, offset);
                        // 时区切换时仅更新基准、不重置（SetTimezone 已负责重建两桶）
                        if (offset != lastOffset)
                        {
                            lastOffset = offset;
                            lastUtc = utcToday;
                            lastLocal = localToday;
                            continue;
                        }
                        if (utcToday != lastUtc || localToday != lastLocal)
                        {
                            Logger.Info("Stats", $"日期变化 UTC:{lastUtc}→{utcToday} Local:{lastLocal}→{localToday}，从数据库回填两套计数");
                            _coordinator.RebuildToday();
                            _coordinator.InvalidateMissedCache();
                            _onRollover(utcToday, localToday, offset);
                            lastUtc = utcToday;
                            lastLocal = localToday;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Stats", $"跨午夜守望失败: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException) { }
        }, _cts.Token);
    }

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        _cts = null;
    }
}
