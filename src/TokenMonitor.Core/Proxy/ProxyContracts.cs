using TokenMonitor.Core.Parser;

namespace TokenMonitor.Core.Proxy;

/// <summary>漏抓登记（usage_missed 行）。仅"上游可能已计费"的请求进入（status==0 未知 或 2xx）。</summary>
public sealed record MissedCapture(long Ts, string Provider, string Model, int Status, string Reason);

/// <summary>代理监听状态变化参数。</summary>
public sealed record ProxyStateChangedEventArgs(bool IsListening, string ListenAddr, string? Error);

/// <summary>捕获结果唯一出口（UsageCoordinator 实现）。代理线程调用；实现必须线程安全且永不抛。</summary>
public interface IUsageIngestor
{
    void Ingest(UsageEvent evt);
    void ReportMissed(MissedCapture capture);
}

/// <summary>本地 OpenAI 兼容反向代理引擎契约（01-§2.3.7）。</summary>
public interface IProxyEngine : IDisposable
{
    bool IsListening { get; }
    string ListenAddr { get; }

    /// <summary>在途捕获计数（诊断/退出等待）。</summary>
    int InFlightCaptures { get; }

    /// <summary>启动监听。绑定前 TcpListener 预检端口占用 [C19-④]；失败抛 ProxyStartException 并已发出 StateChanged(false, error)。</summary>
    void Start(string listenAddr);

    /// <summary>停止接受新连接→等待在途捕获至多 gracefulTimeout→强制中止 [S5]。可重复调用（幂等）。</summary>
    void Stop(TimeSpan gracefulTimeout);

    event EventHandler<ProxyStateChangedEventArgs>? StateChanged;
}

/// <summary>代理引擎可注入参数（测试确定性：SSE 上限等按需缩短）。</summary>
public sealed class ProxyOptions
{
    /// <summary>SSE 读取上限（默认 30 分钟，[C19-③]；超限按漏抓登记）。</summary>
    public TimeSpan SseReadCap { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>请求体内存上限（字节；超限 413）。</summary>
    public long MaxRequestBodyBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>JSON 响应缓冲上限（字节；保护性限制，超出按漏抓处理）。</summary>
    public long MaxJsonResponseBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>SSE 行缓冲上限（字节；对齐原 scanner.Buffer 8MB）。</summary>
    public int MaxSseLineBytes { get; init; } = 8 << 20;
}
