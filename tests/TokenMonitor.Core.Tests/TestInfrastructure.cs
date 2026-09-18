using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TokenMonitor.Core;
using TokenMonitor.Core.Config;
using TokenMonitor.Core.Events;
using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Proxy;
using TokenMonitor.Core.Stats;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>测试专用可编程时钟（IClock 抽象落点：.NET 内建 TimeProvider 子类化）。</summary>
internal sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan delta) => Now += delta;

    public long NowUnix => Now.ToUnixTimeSeconds();
}

/// <summary>测试用 IUsageIngestor 捕获桩。</summary>
internal sealed class TestIngestor : IUsageIngestor
{
    private readonly object _gate = new();
    public List<UsageEvent> Events { get; } = new();
    public List<MissedCapture> Missed { get; } = new();

    public void Ingest(UsageEvent evt)
    {
        lock (_gate) Events.Add(evt);
    }

    public void ReportMissed(MissedCapture capture)
    {
        lock (_gate) Missed.Add(capture);
    }
}

internal static class TestHelpers
{
    private static int _portCounter = 41300;
    /// <summary>独立临时目录（每测试一个）。</summary>
    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tmcfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 测试专用端口：从 41000-48000 专用区间原子分配（避开 OS 动态端口段），并用 TcpListener 验证可用。
    /// 并发测试下保证互不相同，避免"取号后未绑定被并发测试复用"的竞态。
    /// </summary>
    public static int FreePort()
    {
        while (true)
        {
            var port = Interlocked.Add(ref _portCounter, 3);
            if (port > 48000) Interlocked.CompareExchange(ref _portCounter, 41300, port);
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            try
            {
                l.Start();
                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // 被其它进程占用 → 取下一个
            }
            finally
            {
                l.Stop();
            }
        }
    }


    /// <summary>事件驱动等待：以固定间隔轮询条件直至满足或超时（不依赖碰运气式固定 sleep）。</summary>
    public static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null, string? message = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new TimeoutException(message ?? "等待条件超时");
    }

    /// <summary>同 WaitForAsync 但不抛异常：返回条件是否在时限内满足（"预期不满足"断言用）。</summary>
    public static async Task<bool> TryWaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>等待面板快照满足条件（快照由 StatsTicker 以注入间隔发布，事件驱动）。</summary>
    public static async Task<StatsSnapshot> WaitForSnapshotAsync(IEventBus bus,
        Func<StatsSnapshot, bool> predicate, TimeSpan? timeout = null)
    {
        var tcs = new TaskCompletionSource<StatsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticks = 0;
        StatsSnapshot? last = null;
        using var sub = bus.Subscribe<PanelStatsTick>(e =>
        {
            Interlocked.Increment(ref ticks);
            last = e.Snapshot;
            if (predicate(e.Snapshot)) tcs.TrySetResult(e.Snapshot);
        });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout ?? TimeSpan.FromSeconds(15)));
        if (done != tcs.Task)
        {
            var l = last;
            var diag = l is null ? "无快照" : $"模型数={l.UtcModels.Count} 摘要req={l.UtcSummary.RequestCount} 首模型={l.UtcModels.FirstOrDefault()?.Provider}/{l.UtcModels.FirstOrDefault()?.Model} req={l.UtcModels.FirstOrDefault()?.RequestCount} missed={l.UtcModels.FirstOrDefault()?.MissedCount} 全局漏抓={l.MissedCaptures}";
            throw new TimeoutException($"等待快照条件超时（收到 {Volatile.Read(ref ticks)} 次快照；{diag}）");
        }
        return await tcs.Task;
    }



    /// <summary>构造测试引擎（临时目录 + 快定时器注入）。</summary>
    public static MonitorEngine NewEngine(string dataDir, Action<MonitorEngineOptions>? customize = null)
    {
        var options = new MonitorEngineOptions
        {
            DataDir = dataDir,
            StoreFlushInterval = TimeSpan.FromMilliseconds(100),
            DayWatchInterval = TimeSpan.FromHours(1), // 测试中禁用跨午夜守望
            PanelTickInterval = TimeSpan.FromMilliseconds(20),
            BallTickInterval = TimeSpan.FromMilliseconds(50),
            MissedCountTtl = TimeSpan.FromMilliseconds(50),
            SseReadCap = TimeSpan.FromSeconds(5),
        };
        customize?.Invoke(options);
        return new MonitorEngine(options);
    }

    /// <summary>为引擎配置 providers（StartAsync 前调用）。</summary>
    public static void ConfigureProvider(MonitorEngine engine, params ProviderConfig[] providers)
    {
        var port = FreePort();
        engine.Config.SaveProxy(new ProxyConfig($"127.0.0.1:{port}", null, providers));
    }
}

/// <summary>Kestrel mock 上游：随机空闲端口 + 可编程响应处理器 + 请求捕获（供断言转发热文）。</summary>
internal sealed class MockUpstream : IDisposable
{
    private WebApplication? _app;
    private readonly object _gate = new();

    public int Port { get; private set; }
    public List<(string Method, string Path, string Query, string? ContentType, string? Authorization, string Body)> Requests { get; } = new();
    public required Func<MockUpstream, HttpContext, byte[], Task> Handler { get; init; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public static Task<MockUpstream> StartAsync(Func<MockUpstream, HttpContext, byte[], Task> handler)
        => StartAsync(TestHelpers.FreePort(), handler);

    public static async Task<MockUpstream> StartAsync(int port, Func<MockUpstream, HttpContext, byte[], Task> handler)
    {
        var mock = new MockUpstream { Handler = handler, Port = port };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = "MockUpstream",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting("urls", "");
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(System.Net.IPAddress.Loopback, port);
            o.AddServerHeader = false;
        });
        var app = builder.Build();
        app.Run(async ctx =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            var body = ms.ToArray();
            lock (mock._gate)
            {
                mock.Requests.Add((ctx.Request.Method, ctx.Request.Path.Value ?? "/", ctx.Request.QueryString.Value ?? "",
                    ctx.Request.ContentType, ctx.Request.Headers.Authorization.ToString(), System.Text.Encoding.UTF8.GetString(body)));
            }
            await mock.Handler(mock, ctx, body);
        });
        mock._app = app;
        await app.StartAsync();
        return mock;
    }

    public int RequestCount
    {
        get { lock (_gate) return Requests.Count; }
    }

    public void Dispose()
    {
        try { _app?.StopAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(500)).Token).GetAwaiter().GetResult(); } catch { }
        try { _app?.DisposeAsync().GetAwaiter().GetResult(); } catch { }
    }
}

/// <summary>SSE/JSON 响应的常用 mock 构造器。</summary>
internal static class MockResponses
{
    public const string UsageJson =
        """{"model":"test-model","usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10,"completion_tokens":50,"completion_tokens_details":{"reasoning_tokens":20},"total_tokens":150}}""";

    public static readonly UnifiedUsage ExpectedUsage = new(100, 90, 10, 50, 20, 150);

    public static Func<MockUpstream, HttpContext, byte[], Task> JsonResponse(string body, string contentType = "application/json") =>
        (_, ctx, _) =>
        {
            ctx.Response.ContentType = contentType;
            return ctx.Response.WriteAsync(body);
        };

    public static Func<MockUpstream, HttpContext, byte[], Task> SseResponse(params string[] lines) =>
        async (_, ctx, _) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            foreach (var line in lines)
            {
                await ctx.Response.WriteAsync(line + "\n");
                await ctx.Response.Body.FlushAsync();
            }
        };
}
