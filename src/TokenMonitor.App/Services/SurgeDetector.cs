using TokenMonitor.Core.Stats;

namespace TokenMonitor.App.Services;

/// <summary>
/// 流量激增检测（03-ui-spec §5 映射与克制规则）。
/// 触发：60s 滑窗速率 > 基线(近10分钟 60s 速率样本)均值 + 3σ，或速率 > 50K tokens/s；
/// 解除：速率回落至均值 + 1σ 以下并持续 30s（滞回）。
/// 供环境动效加速：S4 雷达 6s→2.5s / S2 光标 1.1s→0.45s / S1·S3 增量一次性脉冲（≤1.2Hz 由消费方限频）。
/// </summary>
public sealed class SurgeDetector
{
    private readonly Queue<(long Ts, double Rate)> _rateHistory = new();   // 近10分钟的 60s 速率样本（每 20s 采一个）
    private readonly Queue<(long Ts, long Cum)> _cumSamples = new();       // 近 70s 的累计值样本
    private long _lastCum;
    private bool _surge;
    private long _belowSince;      // 速率回落起始时刻（Unix 秒）
    private long _lastHistorySample;

    public bool IsSurging => _surge;

    /// <summary>每秒（BallStatsTick）喂入当前累计 token 总数。</summary>
    public void Feed(long totalTokens)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_lastCum == 0)
        {
            _lastCum = totalTokens;
            _cumSamples.Enqueue((now, totalTokens));
            return;
        }

        _cumSamples.Enqueue((now, totalTokens));
        while (_cumSamples.Count > 0 && now - _cumSamples.Peek().Ts > 70) _cumSamples.Dequeue();

        // 60s 滑窗速率
        var oldest = _cumSamples.Peek();
        var windowSec = Math.Max(1, now - oldest.Ts);
        var windowTokens = Math.Max(0, totalTokens - oldest.Cum);
        var rate = windowTokens / (double)windowSec;

        // 20s 采一个基线样本，保留 10 分钟（30 个）
        if (now - _lastHistorySample >= 20)
        {
            _lastHistorySample = now;
            _rateHistory.Enqueue((now, rate));
            while (_rateHistory.Count > 30) _rateHistory.Dequeue();
        }

        if (!_surge)
        {
            var (mean, sigma) = Stats(_rateHistory);
            var threshold = mean + 3 * sigma;
            if (rate > 50_000 || (_rateHistory.Count >= 10 && sigma > 0 && rate > threshold && rate > 2_000))
                SetSurge(true);
            _belowSince = 0;
        }
        else
        {
            var (mean, sigma) = Stats(_rateHistory);
            if (rate <= mean + Math.Max(sigma, mean * 0.2))
            {
                if (_belowSince == 0) _belowSince = now;
                if (now - _belowSince >= 30) SetSurge(false);   // 滞回：连续 30s 回落
            }
            else _belowSince = 0;
        }

        _lastCum = totalTokens;
    }

    private static (double Mean, double Sigma) Stats(IEnumerable<(long, double)> samples)
    {
        var arr = samples.Select(s => s.Item2).ToArray();
        if (arr.Length == 0) return (0, 0);
        var mean = arr.Average();
        var var = arr.Length > 1 ? arr.Sum(v => (v - mean) * (v - mean)) / (arr.Length - 1) : 0;
        return (mean, Math.Sqrt(var));
    }

    private void SetSurge(bool on)
    {
        if (_surge == on) return;
        _surge = on;
        _belowSince = 0;
        Infrastructure.SkinFlags.Instance.Surge = on;
        Infrastructure.SkinFlags.Instance.NotifySurgeChanged();
        Core.SysUtil.Logger.Info("App", "流量激增环境动效: " + (on ? "加速" : "恢复"));
    }
}
