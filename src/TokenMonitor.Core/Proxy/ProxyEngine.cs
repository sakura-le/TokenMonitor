using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TokenMonitor.Core.Config;
using TokenMonitor.Core.Parser;

using Logger = TokenMonitor.Core.SysUtil.Logger;
using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Proxy;

/// <summary>
/// IProxyEngine 默认实现（Kestrel 传输，已批准偏差）。
/// 请求整备（按 provider 的 strip_params 剥离 [C15]、max_tokens 钳制、仅流式注入 include_usage [C2]）、
/// 未知模型拒绝 [C14]、SSE 逐行边转发边解析且客户端断开后继续读完上游（30 分钟上限 [C19-③]）、
/// JSON 缓冲解析回放、漏抓判定与登记（02-§1.7 判定表）、/v1/models 合成、/health。
/// 单请求异常全捕获（500/502），不拖垮进程 [A8]。捕获结果经 IUsageIngestor 交给 Core.Stats。
/// HTTP 面只保留 /health、/v1/models、/v1/*（其余 404，D1-1 收窄攻击面 [C10]）。
/// </summary>
public sealed class ProxyEngine : IProxyEngine
{
    private static readonly string[] HopByHopHeaders =
    [
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "TE", "Trailer", "Trailers",
        "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "Host", "Content-Length",
    ];

    private readonly Func<ProxyConfig> _configAccessor;
    private readonly IUsageIngestor _ingestor;
    private readonly IUsageParser _parser;
    private readonly ProxyOptions _options;
    private readonly TimeProvider _clock;
    private readonly object _lifeGate = new();

    private WebApplication? _app;
    private HttpClient? _upstream;
    private CancellationTokenSource? _captureCts;
    private volatile bool _started;
    private int _inFlight;
    private string _listenAddr = string.Empty;

    public ProxyEngine(Func<ProxyConfig> configAccessor, IUsageIngestor ingestor,
                       IUsageParser? parser = null, ProxyOptions? options = null, TimeProvider? clock = null)
    {
        _configAccessor = configAccessor;
        _ingestor = ingestor;
        _parser = parser ?? new UsageParser();
        _options = options ?? new ProxyOptions();
        _clock = clock ?? TimeProvider.System;
    }

    public bool IsListening => _started;
    public string ListenAddr => _listenAddr;
    public int InFlightCaptures => Volatile.Read(ref _inFlight);
    public event EventHandler<ProxyStateChangedEventArgs>? StateChanged;

    // —— 启动 / 停止 ——

    public void Start(string listenAddr)
    {
        lock (_lifeGate)
        {
            if (_started)
                throw new InvalidOperationException("代理已启动");
            var (ip, port) = ParseLoopback(listenAddr);
            // [C19-④] 绑定前预检端口占用（预检+绑定失败即报，消除 TOCTOU 的静默面）
            try
            {
                var probe = new TcpListener(ip, port);
                probe.Start();
                probe.Stop();
            }
            catch (Exception ex)
            {
                var error = $"端口 {ip}:{port} 预检失败（可能已被占用）: {ex.Message}";
                Logger.Error("Proxy", error);
                StateChanged?.Invoke(this, new ProxyStateChangedEventArgs(false, listenAddr, error));
                throw new ProxyStartException(error, ex);
            }

            _captureCts = new CancellationTokenSource();
            _upstream = CreateUpstreamClient();
            try
            {
                _app = BuildApp(ip, port);
                _app.StartAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                var error = $"代理监听启动失败（{listenAddr}）: {ex.Message}";
                Logger.Error("Proxy", error, ex);
                StateChanged?.Invoke(this, new ProxyStateChangedEventArgs(false, listenAddr, error));
                DisposeAppQuiet();
                throw new ProxyStartException(error, ex);
            }
            _listenAddr = listenAddr;
            _started = true;
            Logger.Info("Proxy", $"代理服务器启动: http://{listenAddr}");
            StateChanged?.Invoke(this, new ProxyStateChangedEventArgs(true, listenAddr, null));
        }
    }

    public void Stop(TimeSpan gracefulTimeout)
    {
        lock (_lifeGate)
        {
            if (!_started && _app is null) return; // 幂等
            _started = false;
        }
        Logger.Info("Proxy", "代理停止中（优雅等待在途捕获）");
        try
        {
            // 1. 停止接受新连接（短排空窗口；在途捕获为分离任务，不依赖响应存续 [S5]）
            var drain = TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(50, gracefulTimeout.TotalMilliseconds)));
            _app?.StopAsync(drain).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn("Proxy", $"停止接受连接时异常: {ex.Message}");
        }
        try
        {
            // 2. 等待在途捕获归零或超时
            var deadline = _clock.GetUtcNow() + gracefulTimeout;
            while (Volatile.Read(ref _inFlight) > 0 && _clock.GetUtcNow() < deadline)
            {
                Thread.Sleep(10);
            }
            // 3. 取消捕获 CTS（在途 SSE 续读任务随上限 CTS 链路中止）
            _captureCts?.Cancel();
            _app?.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Warn("Proxy", $"等待在途捕获时异常: {ex.Message}");
        }
        finally
        {
            DisposeAppQuiet();
            _upstream?.Dispose();
            _upstream = null;
            _captureCts?.Dispose();
            _captureCts = null;
            StateChanged?.Invoke(this, new ProxyStateChangedEventArgs(false, _listenAddr, null));
            Logger.Info("Proxy", "代理已停止");
        }
    }

    private void DisposeAppQuiet()
    {
        try { _app?.DisposeAsync().GetAwaiter().GetResult(); } catch { }
        _app = null;
    }

    private static (IPAddress Ip, int Port) ParseLoopback(string listenAddr)
    {
        // 红线：只听回环（127.0.0.1 / localhost / ::1）
        var idx = listenAddr.LastIndexOf(':');
        if (idx <= 0 || !int.TryParse(listenAddr[(idx + 1)..], out var port) || port is < 1 or > 65535)
            throw new ProxyStartException($"listen_addr 非法: {listenAddr}");
        var host = listenAddr[..idx];
        var ip = host switch
        {
            "127.0.0.1" => IPAddress.Loopback,
            "localhost" => IPAddress.Loopback,
            "::1" => IPAddress.IPv6Loopback,
            _ => null,
        };
        if (ip is null)
            throw new ProxyStartException($"listen_addr 非回环地址（安全红线）: {listenAddr}");
        return (ip, port);
    }

    private static HttpClient CreateUpstreamClient()
    {
        // 对齐原 newUpstreamTransport 调优（降低长生成被 RST 概率）
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(30),
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 100,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(90),
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = true,
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private WebApplication BuildApp(IPAddress ip, int port)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            ApplicationName = typeof(ProxyEngine).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders(); // 日志统一走 Core.SysUtil.Logger，禁用宿主控制台输出
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, ""); // 端口仅由下方显式 Listen 决定
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(ip, port);
            o.AddServerHeader = false;
            o.Limits.MaxRequestBodySize = _options.MaxRequestBodyBytes * 2; // 请求体上限由管线自身执行（32MB → 413）
        });
        var app = builder.Build();
        app.Run(HandleHttpContextAsync);
        return app;
    }

    // —— HTTP 管线 ——

    private async Task HandleHttpContextAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "/";
        try
        {
            if (path == "/health")
            {
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"status\":\"ok\"}");
                return;
            }
            if (path == "/v1/models")
            {
                await HandleModelsAsync(ctx);
                return;
            }
            if (path.StartsWith("/v1/", StringComparison.Ordinal) || path == "/v1")
            {
                await HandleProxyAsync(ctx, path);
                return;
            }
            ctx.Response.StatusCode = StatusCodes.Status404NotFound; // D1-1：未知路径一律 404（[C10]）
        }
        catch (Exception ex)
        {
            // 单请求级兜底：500（转发前）/502（转发中），进程照常 [A8]
            Logger.Error("Proxy", $"请求处理异常: {ctx.Request.Method} {path} body={ctx.Items["BodyBytes"]} {ex.Message}", ex);
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = ctx.Items.ContainsKey("Forwarding") ? 502 : 500;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync("{\"error\":{\"message\":\"internal proxy error\",\"type\":\"api_error\"}}");
            }
            else
            {
                ctx.Abort();
            }
        }
    }

    private async Task HandleModelsAsync(HttpContext ctx)
    {
        var cfg = _configAccessor();
        var data = new JsonArray();
        foreach (var provider in cfg.Providers)
        {
            foreach (var prefix in provider.ModelPrefix)
            {
                data.Add(new JsonObject
                {
                    ["id"] = prefix,
                    ["object"] = "model",
                    ["owned_by"] = provider.Name, // api_key 绝不出现在响应
                });
            }
        }
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString());
    }

    private async Task HandleProxyAsync(HttpContext ctx, string path)
    {
        // 1) 全量读入请求体（内存上限 32MB，超限 413）
        byte[] body;
        try
        {
            body = await ReadAllWithCapAsync(ctx.Request.Body, _options.MaxRequestBodyBytes, ctx.RequestAborted);
        }
        catch (InternalBodyTooLargeException)
        {
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"error\":{\"message\":\"request body too large\",\"type\":\"invalid_request_error\"}}");
            return;
        }
        ctx.Items["BodyBytes"] = body.Length;
        Logger.Info("Proxy", $"{ctx.Request.Method} {path} body={body.Length} bytes");

        // 2) 是否"可捕获 JSON 请求"？[C2]
        var contentType = ctx.Request.ContentType;
        var isCaptureJson = _parser.IsJsonRequestBody(contentType, body.Length > 0 ? body.AsSpan(0, Math.Min(body.Length, 64)) : default);

        ProviderConfig? provider;
        byte[] newBody;
        string model;

        if (isCaptureJson)
        {
            // 3) body 解析为顶层 JSON 对象；失败 → 400
            JsonObject? obj;
            try
            {
                obj = JsonNode.Parse(body) as JsonObject;
            }
            catch (JsonException)
            {
                obj = null;
            }
            if (obj is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "解析请求体失败");
                return;
            }
            // 4) 取 model（字符串，缺失/空 → 400）
            model = obj["model"]?.GetValueKind() == JsonValueKind.String ? obj["model"]!.GetValue<string>() ?? "" : "";
            if (model.Length == 0)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status400BadRequest, "请求体缺少 model 字段");
                return;
            }
            // 5) 路由匹配 provider → 无匹配 → 404 [C14]
            provider = MatchProvider(_configAccessor(), model);
            if (provider is null)
            {
                Logger.Warn("Proxy", $"未匹配到 provider 且无 default_provider: model={model}");
                await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, $"unknown model: {model}", "invalid_request_error");
                return;
            }
            // 6) 请求体整备（§1.3）
            newBody = PrepareBody(obj, provider);
        }
        else
        {
            // 纯透传：不解析、不整备、不捕获、不计漏抓（/v1/audio/* multipart、GET /v1/files 等）[C2]
            model = ctx.Request.Query["model"].ToString();
            var cfg = _configAccessor();
            provider = model.Length > 0
                ? MatchProvider(cfg, model)
                : ResolveDefaultProvider(cfg);
            if (provider is null)
            {
                await WriteErrorAsync(ctx, StatusCodes.Status404NotFound, "unknown target provider", "invalid_request_error");
                return;
            }
            newBody = body;
        }

        // 7) 转发 + 响应拦截（可捕获 JSON 请求）；纯透传请求原样转发，不解析不捕获 [C2]
        ctx.Items["Forwarding"] = true;
        Interlocked.Increment(ref _inFlight);
        try
        {
            if (isCaptureJson)
                await ForwardWithCaptureAsync(ctx, path, provider, model, newBody);
            else
                await ForwardPassthroughAsync(ctx, path, provider, newBody, contentType);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task ForwardPassthroughAsync(HttpContext ctx, string inboundPath, ProviderConfig provider,
                                               byte[] body, string? inboundContentType)
    {
        if (_upstream is null || _captureCts is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, "代理未初始化");
            return;
        }
        var target = new Uri(provider.BaseUrl);
        var incomingPath = inboundPath.StartsWith("/v1", StringComparison.Ordinal) ? inboundPath[3..] : inboundPath;
        if (incomingPath.Length == 0) incomingPath = "/";
        var targetPath = target.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? target.AbsolutePath + incomingPath[1..]
            : target.AbsolutePath + incomingPath;
        var ub = new UriBuilder(target) { Path = targetPath, Query = ctx.Request.QueryString.Value ?? "" };
        using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(_captureCts.Token);
        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), ub.Uri);
        foreach (var (name, values) in ctx.Request.Headers)
        {
            if (HopByHopHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey);
        request.Headers.Host = target.Authority;
        request.Content = new ByteArrayContent(body);
        try
        {
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(
                inboundContentType ?? "application/octet-stream");
        }
        catch
        {
            request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/octet-stream");
        }

        HttpResponseMessage up;
        try
        {
            up = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamCts.Token);
        }
        catch (Exception ex)
        {
            Logger.Warn("Proxy", $"透传请求上游失败: {ex.Message}");
            if (!ctx.Response.HasStarted)
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }
        using var upstreamResponse = up;
        // 原样回传：不解析响应、不捕获、不计漏抓
        ctx.Response.StatusCode = (int)upstreamResponse.StatusCode;
        CopyResponseHeaders(upstreamResponse, ctx.Response);
        ctx.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        await using var src = await upstreamResponse.Content.ReadAsStreamAsync(upstreamCts.Token);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await src.ReadAsync(buffer, upstreamCts.Token)) > 0)
        {
            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, read), ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }

    private async Task ForwardWithCaptureAsync(HttpContext ctx, string inboundPath, ProviderConfig provider,
                                               string model, byte[] newBody)
    {
        if (_upstream is null || _captureCts is null)
        {
            await WriteErrorAsync(ctx, StatusCodes.Status502BadGateway, "代理未初始化");
            return;
        }
        var target = new Uri(provider.BaseUrl);
        // 路径 = base_url.Path + (入站路径剥 "/v1")，Query 原样（原 Director 语义）
        var incomingPath = inboundPath.StartsWith("/v1", StringComparison.Ordinal) ? inboundPath[3..] : inboundPath;
        if (incomingPath.Length == 0) incomingPath = "/";
        var targetPath = target.AbsolutePath.EndsWith("/", StringComparison.Ordinal)
            ? target.AbsolutePath + incomingPath[1..]
            : target.AbsolutePath + incomingPath;
        var ub = new UriBuilder(target) { Path = targetPath, Query = ctx.Request.QueryString.Value ?? "" };

        using var upstreamCts = CancellationTokenSource.CreateLinkedTokenSource(_captureCts.Token);
        upstreamCts.CancelAfter(_options.SseReadCap); // 30 分钟上限（与客户端取消解耦，[A3]/[C19-③]）

        using var request = new HttpRequestMessage(new HttpMethod(ctx.Request.Method), ub.Uri);
        foreach (var (name, values) in ctx.Request.Headers)
        {
            // 剥离 hop-by-hop 头（D1-4 显式化）
            if (HopByHopHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase))
                continue;
            request.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", provider.ApiKey); // 明文不进日志
        request.Headers.Host = target.Authority;
        var content = new ByteArrayContent(newBody);
        // Content-Type：JSON 整备路径固定 application/json（原行为）
        content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
        request.Content = content;

        HttpResponseMessage up;
        try
        {
            up = await _upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, upstreamCts.Token);
        }
        catch (Exception ex)
        {
            // 上游错误分类（原 upstreamRequestSent）：未送达 → 不计漏抓；送达后中断 → 保守计入 [A6]
            if (UpstreamRequestSent(ex))
            {
                Logger.Warn("Proxy", $"连接上游失败: {ex.Message}；该请求已登记漏抓（厂商可能已计费）");
                _ingestor.ReportMissed(new MissedCapture(NowUnix(), provider.Name, model, 0,
                    $"上游连接中断: {ex.GetBaseException().Message}"));
            }
            else
            {
                Logger.Warn("Proxy", $"请求未送达上游，不计入漏抓: {ex.Message}");
            }
            if (!ctx.Response.HasStarted)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                ctx.Response.ContentType = "application/json";
                await ctx.Response.WriteAsync($"{{\"error\":{{\"message\":\"连接上游服务失败: {JsonEncodedText.Encode(ex.GetBaseException().Message)}\",\"type\":\"api_error\"}}}}");
            }
            return;
        }

        using var upstreamResponse = up;
        var upstreamCt = upstreamCts.Token;
        if ((upstreamResponse.Content.Headers.ContentType?.ToString() ?? "").Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
        {
            await InterceptSseAsync(ctx, upstreamResponse, provider, model, upstreamCt);
        }
        else
        {
            await InterceptJsonAsync(ctx, upstreamResponse, provider, model, upstreamCt);
        }
    }
    // —— SSE 逐行解析状态机（02-§1.5）——

    private async Task InterceptSseAsync(HttpContext ctx, HttpResponseMessage up,
                                         ProviderConfig provider, string model, CancellationToken upstreamCt)
    {
        ctx.Response.StatusCode = (int)up.StatusCode;
        CopyResponseHeaders(up, ctx.Response);
        ctx.Response.ContentType = up.Content.Headers.ContentType?.ToString() ?? "text/event-stream";
        var clientAborted = ctx.RequestAborted;

        var lastUsage = default(UnifiedUsage?);
        bool clientGone = false, overCap = false;
        Exception? scanErr = null;
        await using var upstreamStream = await up.Content.ReadAsStreamAsync(upstreamCt);
        using var reader = new LineStreamReader(upstreamStream, _options.MaxSseLineBytes);
        try
        {
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(upstreamCt);
                }
                catch (OperationCanceledException) when (_captureCts is { IsCancellationRequested: false })
                {
                    overCap = true; // 30 分钟上限到期（客户端未断开、代理未停止）[C19-③]
                    break;
                }
                if (line is null) break; // 上游流尾
                if (!clientGone)
                {
                    try
                    {
                        await ctx.Response.WriteAsync(line + "\n", clientAborted);
                        await ctx.Response.Body.FlushAsync(clientAborted);
                    }
                    catch (Exception)
                    {
                        // 客户端断开：停止转发，继续读 [A3]（请求 CT 与客户端解除关联）
                        clientGone = true;
                        Logger.Info("Proxy", "客户端已断开，停止转发但继续读取上游以捕获 usage");
                    }
                }
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue; // event:/注释/空行仅转发
                var data = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]; // "data: x"|"data:x"（D1-5 宽容无空格）
                if (data == "[DONE]") continue;
                var r = _parser.ParseSseDataLine(data, provider.Name);
                if (r.Usage is not null)
                {
                    lastUsage = r.Usage; // "最后一个 usage 块胜出"（原 lastEvent = ev 覆盖语义）
                }
            }
        }
        catch (Exception ex)
        {
            scanErr = ex; // 上游中断/超长行（>8MB）/代理停止中止
            Logger.Warn("Proxy", $"读取上游 SSE 响应中断: {ex.Message}（已读部分继续解析）");
        }

        // 归属 ts = 读流结束后的当前时刻（原 processEvent 语义，铁律 4）
        var completedAt = NowUnix();
        if (lastUsage is { } usage)
        {
            Logger.Info("Proxy", $"SSE usage: {provider.Name}/{model} total={usage.TotalTokens} prompt={usage.PromptTokens} completion={usage.CompletionTokens}");
            _ingestor.Ingest(new UsageEvent(provider.Name, model, usage, completedAt, CaptureSource.Sse));
        }
        else
        {
            ReportMissedGuarded(provider.Name, model, (int)up.StatusCode, overCap, scanErr);
        }
    }

    // —— 非流式 JSON 缓冲回放（02-§1.6）——

    private async Task InterceptJsonAsync(HttpContext ctx, HttpResponseMessage up,
                                          ProviderConfig provider, string model, CancellationToken upstreamCt)
    {
        // 整包缓冲（容忍部分读：读中断取已读部分继续解析，原实现语义）
        await using var src = up.Content.ReadAsStream(upstreamCt);
        var body = await ReadResilientAsync(src, _options.MaxJsonResponseBytes, upstreamCt);
        var r = _parser.ParseJsonResponse(Encoding.UTF8.GetString(body), provider.Name);
        if (r.Usage is { } usage)
        {
            Logger.Info("Proxy", $"JSON usage: {provider.Name}/{model} total={usage.TotalTokens} prompt={usage.PromptTokens} completion={usage.CompletionTokens}");
            _ingestor.Ingest(new UsageEvent(provider.Name, model, usage, NowUnix(), CaptureSource.Json));
        }
        else
        {
            ReportMissedGuarded(provider.Name, model, (int)up.StatusCode,
                overCap: false,
                err: r.FailureReason is not null ? new Exception(r.FailureReason) : null);
        }

        // 原样回放：以内存缓冲替换响应体，ContentLength = body.Length，头不动
        ctx.Response.StatusCode = (int)up.StatusCode;
        CopyResponseHeaders(up, ctx.Response);
        ctx.Response.ContentType = up.Content.Headers.ContentType?.ToString() ?? "application/json";
        ctx.Response.ContentLength = body.Length;
        if (body.Length > 0) await ctx.Response.Body.WriteAsync(body, ctx.RequestAborted);
    }

    // —— 漏抓记账（02-§1.7 判定表）——

    private void ReportMissedGuarded(string provider, string model, int status, bool overCap, Exception? err)
    {
        var reason = BuildMissedReason(overCap, err, status);
        // 判定式对齐原 recordMissed：billed = status==0 || (200<=status<300)
        var billed = status == 0 || (status >= 200 && status < 300);
        if (billed)
        {
            _ingestor.ReportMissed(new MissedCapture(NowUnix(), provider, model, status, reason));
        }
        // 429 限流/其它 4xx/5xx 未计费 → 只记运行日志不入表（D1-7 文档化）
        Logger.Info("Proxy", $"未捕获 usage: status={status} reason={reason} billed={billed}");
    }

    private static string BuildMissedReason(bool overCap, Exception? err, int status)
    {
        if (overCap) return "超过 30 分钟读取上限"; // [C19-③] D1-6 显式文案
        if (err is not null) return err.Message;   // 具体错误文本（上游中断/解析失败）
        return status >= 200 && status < 300 ? "响应无 usage 块" : "非 2xx 响应";
    }

    /// <summary>上游错误分类（对齐原 upstreamRequestSent，原 proxy_test 五例逐一移植）：
    /// DNS 失败/拨号被拒/网络不可达/主机不可达 → 请求未送达（不计漏抓）；其余（建立连接后读写中断）→ 已送达。</summary>
    internal static bool UpstreamRequestSent(Exception ex)
    {
        ex = ex.GetBaseException();
        if (ex is SocketException se)
        {
            switch (se.SocketErrorCode)
            {
                case SocketError.HostNotFound:      // DNS 失败 → 请求未送达，不会计费
                case SocketError.ConnectionRefused: // 拨号被拒
                case SocketError.NetworkUnreachable:
                case SocketError.HostUnreachable:
                    return false;
            }
        }
        return true; // 连接建立后的读/写中断 → 厂商可能已计费
    }

    // —— 路由与整备 ——

    /// <summary>模型前缀路由（对齐原 config.MatchProvider 匹配方式，仅改"未匹配"分支 [C14]）。</summary>
    internal static ProviderConfig? MatchProvider(ProxyConfig cfg, string model)
    {
        var lower = model.ToLowerInvariant();
        foreach (var p in cfg.Providers)                     // 配置声明序
        {
            foreach (var prefix in p.ModelPrefix)            // 前缀声明序
            {
                if (lower.StartsWith(prefix.ToLowerInvariant(), StringComparison.Ordinal))
                    return p;                                // 首个命中即返回
            }
        }
        return ResolveDefaultProvider(cfg);
    }

    private static ProviderConfig? ResolveDefaultProvider(ProxyConfig cfg) =>
        string.IsNullOrEmpty(cfg.DefaultProvider)
            ? null
            : cfg.Providers.FirstOrDefault(p => p.Name == cfg.DefaultProvider);

    /// <summary>请求体整备（02-§1.3）：仅 stream==true 注入 include_usage（绝不改写 stream [C2]）；
    /// max_tokens 数值超限钳制；strip_params 顶层剥离 [C15]。JsonNode 就地增删，不丢未知字段。</summary>
    internal static byte[] PrepareBody(JsonObject body, ProviderConfig provider)
    {
        var isStream = body["stream"] is JsonValue sv && sv.TryGetValue<bool>(out var b) && b;
        if (isStream)
        {
            if (body["stream_options"] is not JsonObject so)
            {
                so = new JsonObject();
                body["stream_options"] = so;
            }
            so["include_usage"] = true; // 已存在时合并写入该键，其余键保留
        }
        if (provider.MaxTokens is > 0 && body["max_tokens"] is JsonValue mv && mv.TryGetValue<double>(out var mt)
            && mt > provider.MaxTokens)
        {
            Logger.Info("Proxy", $"max_tokens {mt} 超出 {provider.Name} 限制 {provider.MaxTokens}，已钳制");
            body["max_tokens"] = provider.MaxTokens.Value; // 整数替换；字符串型值不动（原类型断言语义）
        }
        // 缺省（null）→ 默认剥离项（原全局剥离行为）；显式 [] = 不剥离 [C15]
        var stripParams = provider.StripParams ?? (IReadOnlyList<string>)["enable_thinking", "reasoning_effort"];
        foreach (var key in stripParams)
        {
            if (body.Remove(key))
                Logger.Info("Proxy", $"过滤不支持的参数: {key} (厂商: {provider.Name})");
        }
        return JsonSerializer.SerializeToUtf8Bytes(body);
    }

    // —— 基元 ——

    private sealed class InternalBodyTooLargeException : Exception;

    private static async Task<byte[]> ReadAllWithCapAsync(Stream stream, long cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > cap) throw new InternalBodyTooLargeException();
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>容忍部分读的整包缓冲（原 io.ReadAll 中断取已读部分语义）。</summary>
    private static async Task<byte[]> ReadResilientAsync(Stream stream, long cap, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long total = 0;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > cap)
                {
                    Logger.Warn("Proxy", $"上游响应超过 {cap} 字节上限，按已读部分处理");
                    break;
                }
                ms.Write(buffer, 0, read);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Proxy", $"读取上游 JSON 响应中断: {ex.Message}，已读 {ms.Length} 字节，尝试从已读数据解析 usage");
        }
        return ms.ToArray();
    }

    private static void CopyResponseHeaders(HttpResponseMessage up, HttpResponse response)
    {
        foreach (var (name, values) in up.Headers)
        {
            if (HopByHopHeaders.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                name.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Server", StringComparison.OrdinalIgnoreCase))
                continue;
            response.Headers[name] = values.ToArray();
        }
        foreach (var (name, values) in up.Content.Headers)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            response.Headers[name] = values.ToArray();
        }
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string message, string type = "invalid_request_error")
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(
            $"{{\"error\":{{\"message\":\"{JsonEncodedText.Encode(message)}\",\"type\":\"{type}\"}}}}");
    }

    private long NowUnix() => _clock.GetUtcNow().ToUnixTimeSeconds();

    public void Dispose()
    {
        if (_started) Stop(TimeSpan.FromSeconds(2));
        DisposeAppQuiet();
        _upstream?.Dispose();
        _captureCts?.Dispose();
    }
}
