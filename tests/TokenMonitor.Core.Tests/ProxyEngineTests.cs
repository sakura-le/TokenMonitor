using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using TokenMonitor.Core.Config;
using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Proxy;

namespace TokenMonitor.Core.Tests;

/// <summary>代理引擎回归：C2/C10/C14/C15/C19③④/S5 + 原 proxy_test SSE/upstreamRequestSent 移植 + 端到端集成。</summary>
public class ProxyEngineTests
{
    private static ProxyConfig Config(int port, string? defaultProvider = null, Action<List<ProviderConfig>>? tweak = null)
    {
        var providers = new List<ProviderConfig>
        {
            new("Up", $"http://127.0.0.1:{port}", "sk-up-key", ["test-", "alpha-"], null, null),
        };
        tweak?.Invoke(providers);
        return new ProxyConfig($"127.0.0.1:{TestHelpers.FreePort()}", defaultProvider, providers);
    }

    private static (ProxyEngine Proxy, TestIngestor Ingestor, string ListenAddr) NewProxy(ProxyConfig cfg, ProxyOptions? options = null)
    {
        var ingestor = new TestIngestor();
        var proxy = new ProxyEngine(() => cfg, ingestor, new UsageParser(), options);
        return (proxy, ingestor, cfg.ListenAddr);
    }

    private static string ProxyUrl(string listenAddr, string path) => $"http://{listenAddr}{path}";

    private static readonly string ChatBody =
        """{"model":"test-model","stream":false,"messages":[{"role":"user","content":"hi"}]}""";

    // —— [C2a] stream:false → JSON 原样回放且捕获 ——

    [Fact]
    public async Task Proxy_StreamFalse_RespondsJson_AndCapturesUsage()
    {
        // [C2a] 上游返回 JSON+usage；stream:false 请求 → 客户端收到 application/json 原文、
        // usage 已捕获、上游收到的 body 不含 stream_options（请求体未被改写为流式）
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse(MockResponses.UsageJson));
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent(ChatBody, Encoding.UTF8, "application/json"));
        var text = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("application/json", resp.Content.Headers.ContentType!.ToString());
        Assert.Contains("\"total_tokens\":150", text); // 原文回放

        // usage 已捕获（轮询等待摄入完成）
        await TestHelpers.WaitForAsync(() => ingestor.Events.Count == 1, message: "JSON usage 未捕获");
        Assert.Equal(MockResponses.ExpectedUsage, ingestor.Events[0].Usage);
        Assert.Equal(CaptureSource.Json, ingestor.Events[0].Source);
        Assert.Equal("test-model", ingestor.Events[0].Model);
        Assert.Empty(ingestor.Missed);

        // 上游收到的 body 未被改写（无 stream_options 注入）
        await TestHelpers.WaitForAsync(() => upstream.RequestCount == 1);
        Assert.DoesNotContain("stream_options", upstream.Requests[0].Body);
        Assert.Equal("application/json", upstream.Requests[0].ContentType);
        Assert.Equal("Bearer sk-up-key", upstream.Requests[0].Authorization);
        proxy.Dispose();
    }

    // —— [C2b] 非 JSON body 透传 ——

    [Fact]
    public async Task Proxy_NonJsonBody_PassthroughNoParseNoCapture()
    {
        // [C2b] multipart POST /v1/audio/transcriptions：200 透传、上游收到原始 body、无捕获无漏抓
        // （透传请求无法从 body 解析 model：路由 = query model → default_provider → 404，此处走 default）
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"ok":true}"""));
        var cfg = Config(upstream.Port, defaultProvider: "Up"); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        const string multipart = "--X\r\nContent-Disposition: form-data; name=\"file\"; filename=\"a.wav\"\r\n\r\nBINARYDATA\r\n--X--\r\n";
        var content = new StringContent(multipart, Encoding.UTF8, "multipart/form-data");
        content.Headers.ContentType!.Parameters.Add(new System.Net.Http.Headers.NameValueHeaderValue("boundary", "X"));
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/audio/transcriptions"), content);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"ok\":true", await resp.Content.ReadAsStringAsync());
        await TestHelpers.WaitForAsync(() => upstream.RequestCount == 1);
        Assert.Contains("BINARYDATA", upstream.Requests[0].Body); // 原始 body 透传
        Assert.Contains("multipart/form-data", upstream.Requests[0].ContentType!);
        await TestHelpers.WaitForAsync(() => proxy.InFlightCaptures == 0);
        Assert.Empty(ingestor.Events); // 不捕获
        Assert.Empty(ingestor.Missed); // 不计漏抓
        proxy.Dispose();
    }

    // —— [C2c] stream:true → 注入 include_usage 且保留 stream 值 ——

    [Fact]
    public async Task Proxy_StreamTrue_InjectsIncludeUsage_KeepsStreamValue()
    {
        using var upstream = await MockUpstream.StartAsync(MockResponses.SseResponse(
            """data: {"model":"test-model","usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""",
            "data: [DONE]"));
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        const string body = """{"model":"test-model","stream":true,"temperature":0.7,"max_tokens":999999,"enable_thinking":true}""";
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent(body, Encoding.UTF8, "application/json"));
        var echoed = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("text/event-stream", resp.Content.Headers.ContentType!.ToString());
        Assert.Contains("data:", echoed); // 客户端收到 SSE 转发

        await TestHelpers.WaitForAsync(() => upstream.RequestCount == 1);
        var sent = upstream.Requests[0].Body;
        Assert.Contains("\"stream_options\"", sent);
        Assert.Contains("\"include_usage\":true", sent);
        Assert.Contains("\"stream\":true", sent);               // stream 本身未被改写
        Assert.Contains("\"temperature\":0.7", sent);            // 其余字段保留
        Assert.Contains("\"max_tokens\":999999", sent);          // 无 provider 上限 → 不钳制
        Assert.DoesNotContain("enable_thinking", sent);          // 默认剥离项

        await TestHelpers.WaitForAsync(() => ingestor.Events.Count == 1, message: "SSE usage 未捕获");
        Assert.Equal(15, ingestor.Events[0].Usage.TotalTokens);
        Assert.Equal(CaptureSource.Sse, ingestor.Events[0].Source);
        proxy.Dispose();
    }

    // —— [C14] 未知模型路由 ——

    [Fact]
    public async Task Proxy_UnknownModel_NoDefault_Returns404()
    {
        // [C14a] providers 不含前缀匹配、default=null → 404、上游未收到请求、密钥未外发
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse("{}"));
        var cfg = Config(upstream.Port, defaultProvider: null); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"nomatch-xyz"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("unknown model: nomatch-xyz", body);
        Assert.Equal(0, upstream.RequestCount); // 上游未收到
        Assert.Empty(ingestor.Events);
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_UnknownModel_DefaultProvider_Routes()
    {
        // [C14b] default_provider="B" → 路由 B、Authorization=B 密钥
        using var upstreamA = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"from":"A"}"""));
        using var upstreamB = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"from":"B"}"""));
        var cfg = new ProxyConfig($"127.0.0.1:{TestHelpers.FreePort()}", "B",
        [
            new ProviderConfig("A", upstreamA.BaseUrl, "sk-a", ["alpha-"], null, null),
            new ProviderConfig("B", upstreamB.BaseUrl, "sk-b", ["beta-"], null, null),
        ]);
        var (proxy, _, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"nomatch-xyz"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("\"from\":\"B\"", await resp.Content.ReadAsStringAsync());
        Assert.Equal(1, upstreamB.RequestCount);
        Assert.Equal(0, upstreamA.RequestCount);
        Assert.Equal("Bearer sk-b", upstreamB.Requests[0].Authorization);
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_MatchProvider_CaseInsensitive_FirstPrefixWins()
    {
        // 大小写不敏感前缀匹配 + 跨 provider 重复前缀首个生效 + 模型名含 '/' 的路由
        using var upstreamA = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"from":"A"}"""));
        using var upstreamB = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"from":"B"}"""));
        var cfg = new ProxyConfig($"127.0.0.1:{TestHelpers.FreePort()}", null,
        [
            new ProviderConfig("A", upstreamA.BaseUrl, "sk-a", ["Alpha-"], null, null),
            new ProviderConfig("B", upstreamB.BaseUrl, "sk-b", ["alpha-"], null, null), // 重复前缀（校验时会被去重；此处直接构造验证匹配序）
        ]);
        var (proxy, _, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"ALPHA-m/with/slash"}""", Encoding.UTF8, "application/json"));
        Assert.Contains("\"from\":\"A\"", await resp.Content.ReadAsStringAsync()); // 首个命中（大小写不敏感）
        proxy.Dispose();
    }

    // —— [C15] per-provider strip_params ——

    [Fact]
    public async Task Proxy_StripParams_PerProvider()
    {
        // [C15] A strip=[enable_thinking]；B strip=[]：A 剥 e 不剥 r；B 两者都保留
        using var upstreamA = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"from":"A"}"""));
        using var upstreamB = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"from":"B"}"""));
        var cfg = new ProxyConfig($"127.0.0.1:{TestHelpers.FreePort()}", null,
        [
            new ProviderConfig("A", upstreamA.BaseUrl, "sk-a", ["a-"], null, ["enable_thinking"]),
            new ProviderConfig("B", upstreamB.BaseUrl, "sk-b", ["b-"], null, []),
        ]);
        var (proxy, _, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        const string body = """{"model":"a-m","enable_thinking":true,"reasoning_effort":"low"}""";
        await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"), new StringContent(body, Encoding.UTF8, "application/json"));
        const string bodyB = """{"model":"b-m","enable_thinking":true,"reasoning_effort":"low"}""";
        await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"), new StringContent(bodyB, Encoding.UTF8, "application/json"));

        await TestHelpers.WaitForAsync(() => upstreamA.RequestCount == 1 && upstreamB.RequestCount == 1);
        Assert.DoesNotContain("enable_thinking", upstreamA.Requests[0].Body);   // A 剥 e
        Assert.Contains("reasoning_effort", upstreamA.Requests[0].Body);        // A 不剥 r
        Assert.Contains("enable_thinking", upstreamB.Requests[0].Body);         // B 两者都保留
        Assert.Contains("reasoning_effort", upstreamB.Requests[0].Body);
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_MaxTokens_ClampedToProviderLimit()
    {
        // max_tokens 数值超限钳制（字符串型不动；缺省不补）
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"ok":true}"""));
        var cfg = new ProxyConfig($"127.0.0.1:{TestHelpers.FreePort()}", null,
            [new ProviderConfig("Up", upstream.BaseUrl, "sk", ["test-"], 32768, null)]);
        var (proxy, _, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m","max_tokens":9999999,"max_tokens_str":"9999999"}""", Encoding.UTF8, "application/json"));
        await TestHelpers.WaitForAsync(() => upstream.RequestCount == 1);
        var sent = upstream.Requests[0].Body;
        Assert.Contains("\"max_tokens\":32768", sent);           // 数值钳制
        Assert.Contains("\"max_tokens_str\":\"9999999\"", sent); // 字符串型不动
        proxy.Dispose();
    }

    // —— [C10] HTTP 面收窄 ——

    [Fact]
    public async Task HttpSurface_UnknownPathAndApiPaths_Return404()
    {
        // [C10] /api/*、/foo → 404；/health → 200；/v1/models → 列表
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse("{}"));
        var cfg = Config(upstream.Port); var (proxy, _, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ProxyUrl(addr, "/api/stats"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ProxyUrl(addr, "/api/shutdown"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ProxyUrl(addr, "/foo"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(ProxyUrl(addr, "/"))).StatusCode);

        var health = await client.GetAsync(ProxyUrl(addr, "/health"));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await health.Content.ReadAsStringAsync());

        var models = await client.GetAsync(ProxyUrl(addr, "/v1/models"));
        Assert.Equal(HttpStatusCode.OK, models.StatusCode);
        var modelsJson = await models.Content.ReadAsStringAsync();
        Assert.Contains("\"object\":\"list\"", modelsJson);
        Assert.Contains("\"id\":\"test-\"", modelsJson);
        Assert.Contains("\"owned_by\":\"Up\"", modelsJson);
        Assert.DoesNotContain("sk-up-key", modelsJson); // 密钥绝不外发
        proxy.Dispose();
    }

    // —— [C14] 请求体校验 ——

    [Fact]
    public async Task Proxy_InvalidJsonBody_Returns400()
    {
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse("{}"));
        var cfg = Config(upstream.Port); var (proxy, _, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("not-json", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, upstream.RequestCount);

        var noModel = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"messages":[]}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noModel.StatusCode);
        proxy.Dispose();
    }

    // —— [C19④] 端口占用预检 ——

    [Fact]
    public void Proxy_Start_PortBusy_PrecheckFailsBeforeBind()
    {
        // [C19④] 占用端口 → Start 抛 ProxyStartException、StateChanged(false,error)
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
        try
        {
            var stateChanges = new List<ProxyStateChangedEventArgs>();
            var cfgBusy = new ProxyConfig($"127.0.0.1:{busyPort}", null, []);
            var (proxy, _, addrBusy) = NewProxy(cfgBusy);
            proxy.StateChanged += (_, e) => stateChanges.Add(e);
            var ex = Assert.Throws<ProxyStartException>(() => proxy.Start($"127.0.0.1:{busyPort}"));
            Assert.Contains("占用", ex.Message);
            Assert.False(proxy.IsListening);
            Assert.Contains(stateChanges, s => !s.IsListening && s.Error is not null);
        }
        finally
        {
            blocker.Stop();
        }
    }

    [Fact]
    public void Proxy_Start_NonLoopbackAddr_Rejected()
    {
        // 安全红线：只听回环
        var cfgLoop = new ProxyConfig("0.0.0.0:8280", null, []); var (proxy, _, addrLoop) = NewProxy(cfgLoop);
        Assert.Throws<ProxyStartException>(() => proxy.Start("0.0.0.0:8280"));
    }

    // —— [C19③] SSE 30 分钟上限 ——

    [Fact]
    public async Task Capture_SseOver30Min_RecordedMissedWithCapReason()
    {
        // [C19③] 上游挂起不发 usage、模拟 30min CTS 到期（注入 300ms 短时限）
        // → missed 入表 reason="超过 30 分钟读取上限"、status=200
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = await MockUpstream.StartAsync(async (_, ctx, _) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(": keepalive\n\n"); // 注释行（不触发 usage）
            await ctx.Response.Body.FlushAsync();
            await release.Task; // 挂起不发 usage
        });
        var cfgCap = Config(upstream.Port);
        var (proxy, ingestor, addr) = NewProxy(cfgCap, new ProxyOptions { SseReadCap = TimeSpan.FromMilliseconds(300) });
        proxy.Start(addr);
        using var client = new HttpClient();
        var request = new HttpRequestMessage(HttpMethod.Post, ProxyUrl(addr, "/v1/chat/completions"))
        {
            Content = new StringContent("""{"model":"test-m","stream":true}""", Encoding.UTF8, "application/json"),
        };
        var resp = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await TestHelpers.WaitForAsync(() => ingestor.Missed.Count == 1, TimeSpan.FromSeconds(10),
            "超限漏抓未登记");
        Assert.Equal(200, ingestor.Missed[0].Status);
        Assert.Equal("超过 30 分钟读取上限", ingestor.Missed[0].Reason);
        Assert.Empty(ingestor.Events);
        release.TrySetResult(); // 释放上游，让请求收尾
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_Missed_2xxNoUsage_Recorded()
    {
        // [§1.7] 2xx 但响应无 usage 块 → 入表 reason="响应无 usage 块"
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse("""{"choices":[]}"""));
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m"}""", Encoding.UTF8, "application/json"));
        await TestHelpers.WaitForAsync(() => ingestor.Missed.Count == 1);
        Assert.Equal(200, ingestor.Missed[0].Status);
        Assert.Equal("响应无 usage 块", ingestor.Missed[0].Reason);
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_Missed_429_NotRecorded()
    {
        // [§1.7 D1-7] 429 未计费 → 不入表（不给横幅加数）
        using var upstream = await MockUpstream.StartAsync((_, ctx, _) =>
        {
            ctx.Response.StatusCode = (int)HttpStatusCode.TooManyRequests;
            return ctx.Response.WriteAsync("""{"error":"rate limited"}""");
        });
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
        await TestHelpers.WaitForAsync(() => proxy.InFlightCaptures == 0);
        Assert.Empty(ingestor.Missed); // 4xx/5xx 已知未计费 → 不入表
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_UpstreamRefused_NotRecorded()
    {
        // [§1.7] 拨号被拒 → 未送达上游 → 不计漏抓
        var deadPort = TestHelpers.FreePort(); // 无监听
        var cfgDead = new ProxyConfig(
            $"127.0.0.1:{TestHelpers.FreePort()}", null,
            [new ProviderConfig("Dead", $"http://127.0.0.1:{deadPort}", "sk", ["test-"], null, null)]);
        var (proxy, ingestor, addr) = NewProxy(cfgDead);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadGateway, resp.StatusCode);
        await TestHelpers.WaitForAsync(() => proxy.InFlightCaptures == 0);
        Assert.Empty(ingestor.Missed); // 未送达 → 不计
        proxy.Dispose();
    }

    // —— 原 proxy_test 移植：UpstreamRequestSent 分类 ——

    [Fact]
    public void Proxy_UpstreamRequestSent_Classification()
    {
        // 原 TestUpstreamRequestSent 五例（.NET SocketException 等价形态）
        Assert.False(ProxyEngine.UpstreamRequestSent(new HttpRequestException("Post",
            new SocketException((int)SocketError.HostNotFound))));                 // DNS 解析失败（未送达）
        Assert.False(ProxyEngine.UpstreamRequestSent(new HttpRequestException("Post",
            new SocketException((int)SocketError.ConnectionRefused))));            // 拨号被拒绝
        Assert.False(ProxyEngine.UpstreamRequestSent(new SocketException((int)SocketError.NetworkUnreachable))); // 网络不可达
        Assert.True(ProxyEngine.UpstreamRequestSent(new HttpRequestException("Post",
            new SocketException((int)SocketError.ConnectionReset))));              // 已建立连接后读取中断（厂商已计费）
        Assert.True(ProxyEngine.UpstreamRequestSent(new HttpRequestException("Post",
            new SocketException((int)SocketError.ConnectionAborted))));                   // 连接建立后写入中断
        Assert.True(ProxyEngine.UpstreamRequestSent(new InvalidOperationException("unrelated"))); // 其他错误保守视为已送达
    }

    // —— SSE 行为（原 proxy_test SSE 两例移植 + 多 usage 块）——

    [Fact]
    public async Task Proxy_Sse_NormalStreamForwardsAndCaptures()
    {
        // 原 TestInterceptSSEForwardsAndCapturesNormalStream
        using var upstream = await MockUpstream.StartAsync(MockResponses.SseResponse(
            """data: {"model":"test-m","choices":[{"delta":{"content":"hi"}}]}""",
            """data: {"model":"test-m","usage":{"prompt_tokens":10,"prompt_cache_hit_tokens":0,"prompt_cache_miss_tokens":10,"completion_tokens":5,"total_tokens":15}}""",
            "data: [DONE]"));
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, ProxyUrl(addr, "/v1/chat/completions"))
        {
            Content = new StringContent("""{"model":"test-m","stream":true}""", Encoding.UTF8, "application/json"),
        }, HttpCompletionOption.ResponseHeadersRead);
        var stream = await resp.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);
        var forwarded = await reader.ReadToEndAsync(); // 客户端完整读完流
        Assert.Contains("hi", forwarded);
        Assert.Contains("[DONE]", forwarded);

        await TestHelpers.WaitForAsync(() => ingestor.Events.Count == 1, message: "正常流未捕获");
        Assert.Equal(15, ingestor.Events[0].Usage.TotalTokens);
        Assert.Equal(5, ingestor.Events[0].Usage.CompletionTokens);
        Assert.Empty(ingestor.Missed);
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_Sse_CapturesUsageAfterClientDisconnect()
    {
        // 原 TestInterceptSSECapturesUsageAfterClientDisconnect：客户端中途断开后仍捕获（上游在断开后才发 usage）
        var upstreamSendUsage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = await MockUpstream.StartAsync(async (_, ctx, _) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("data: {\"model\":\"test-m\",\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n");
            await ctx.Response.WriteAsync("data: {\"model\":\"test-m\",\"choices\":[{\"delta\":{\"content\":\"there\"}}]}\n\n");
            await ctx.Response.Body.FlushAsync();
            await upstreamSendUsage.Task; // 等测试确认客户端已断开
            // 上游在客户端断开后仍发送 usage 块 + [DONE]（官网按此计费）
            await ctx.Response.WriteAsync("data: {\"model\":\"test-m\",\"usage\":{\"prompt_tokens\":100,\"prompt_cache_hit_tokens\":90,\"prompt_cache_miss_tokens\":10,\"completion_tokens\":50,\"completion_tokens_details\":{\"reasoning_tokens\":20},\"total_tokens\":150}}\n\n");
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
            await ctx.Response.Body.FlushAsync();
        });
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        var cts = new CancellationTokenSource();
        using var client = new HttpClient();
        var resp = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, ProxyUrl(addr, "/v1/chat/completions"))
        {
            Content = new StringContent("""{"model":"test-m","stream":true}""", Encoding.UTF8, "application/json"),
        }, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var stream = await resp.Content.ReadAsStreamAsync();
        var buf = new byte[128];
        Assert.True(await stream.ReadAsync(buf, cts.Token) > 0); // 读到第一块内容
        cts.Cancel();   // 客户端断开
        stream.Dispose();
        upstreamSendUsage.TrySetResult(); // 此刻上游才发 usage（断开后捕获语义与 abort 传播时序无关：写失败或写成功均不影响解析捕获）

        await TestHelpers.WaitForAsync(() => ingestor.Events.Count == 1, TimeSpan.FromSeconds(10),
            "客户端断开后未继续捕获");
        var u = ingestor.Events[0].Usage;
        Assert.Equal((150, 90, 10, 50, 20), (u.TotalTokens, u.CacheHitTokens, u.CacheMissTokens, u.CompletionTokens, u.ReasoningTokens));
        Assert.Empty(ingestor.Missed);
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_Sse_MultipleUsageBlocks_LastWins()
    {
        // 02-§1.5：多 usage 块（厂商重传/多段）取最后一块
        using var upstream = await MockUpstream.StartAsync(MockResponses.SseResponse(
            """data: {"model":"test-m","usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""",
            """data: {"model":"test-m","usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""",
            "data: [DONE]"));
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m","stream":true}""", Encoding.UTF8, "application/json"));
        await TestHelpers.WaitForAsync(() => ingestor.Events.Count == 1);
        Assert.Equal(15, ingestor.Events[0].Usage.TotalTokens); // 最后一个 usage 块胜出
        proxy.Dispose();
    }

    [Fact]
    public async Task Proxy_Sse_MissingContentTypeWithBrace_TreatedAsJson()
    {
        // D1-2：不发 Content-Type 且 body 为 '{' 开头 → 走 JSON 捕获路径
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse(
            """{"usage":{"prompt_tokens":5,"completion_tokens":5,"total_tokens":10}}"""));
        var cfg = Config(upstream.Port); var (proxy, ingestor, addr) = NewProxy(cfg);
        proxy.Start(addr);
        using var client = new HttpClient();
        using var content = new StringContent("""{"model":"test-m"}""", Encoding.UTF8);
        content.Headers.ContentType = null; // 移除 Content-Type
        var resp = await client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"), content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        await TestHelpers.WaitForAsync(() => ingestor.Events.Count == 1, message: "无 CT 请求未走捕获路径");
        Assert.Equal(10, ingestor.Events[0].Usage.TotalTokens);
        proxy.Dispose();
    }

    // —— [S5] 优雅停止 ——

    [Fact]
    public async Task Proxy_Stop_GracefulWaitsInflightThenAborts()
    {
        // [S5] 在途慢捕获：Stop(10s) 等待其完成（usage 不丢）；Stop(短) 超时强制中止
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = await MockUpstream.StartAsync(async (_, ctx, _) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(": wait\n\n");
            await ctx.Response.Body.FlushAsync();
            await release.Task; // 模拟长生成
            await ctx.Response.WriteAsync("data: {\"model\":\"test-m\",\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":3,\"total_tokens\":10}}\n\n");
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        });
        var cfgSlow = Config(upstream.Port);
        var (proxy, ingestor, addr) = NewProxy(cfgSlow, new ProxyOptions { SseReadCap = TimeSpan.FromSeconds(30) });
        proxy.Start(addr);
        using var client = new HttpClient();
        var requestTask = client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m","stream":true}""", Encoding.UTF8, "application/json"));
        await TestHelpers.WaitForAsync(() => proxy.InFlightCaptures == 1, TimeSpan.FromSeconds(10), "在途捕获未建立");

        var stopTask = Task.Run(() => proxy.Stop(TimeSpan.FromSeconds(10)));
        var completedEarly = await TestHelpers.TryWaitForAsync(() => stopTask.IsCompleted, TimeSpan.FromMilliseconds(300));
        Assert.False(completedEarly); // Stop 阻塞等待在途捕获（不丢 usage）
        release.TrySetResult();             // 上游完成生成
        await stopTask.WaitAsync(TimeSpan.FromSeconds(10)); // Stop 返回 = 在途已完成
        await TestHelpers.WaitForAsync(() => ingestor.Events.Count == 1, message: "优雅停止应保留在途捕获的 usage");
        Assert.Equal(10, ingestor.Events[0].Usage.TotalTokens);
        Assert.False(proxy.IsListening);

        // 超时强制中止：新请求挂起 → Stop(200ms) 快速返回
        var release2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream2 = await MockUpstream.StartAsync(async (_, ctx, _) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(": wait\n\n");
            await ctx.Response.Body.FlushAsync();
            await release2.Task;
        });
        var cfgSlow2 = Config(upstream2.Port);
        var (proxy2, _, addr2) = NewProxy(cfgSlow2, new ProxyOptions { SseReadCap = TimeSpan.FromSeconds(30) });
        proxy2.Start(addr2);
        using var client2 = new HttpClient();
        var hang = client2.PostAsync(ProxyUrl(addr2, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m","stream":true}""", Encoding.UTF8, "application/json"));
        await TestHelpers.WaitForAsync(() => proxy2.InFlightCaptures == 1);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        proxy2.Stop(TimeSpan.FromMilliseconds(200));
        Assert.True(sw.ElapsedMilliseconds < 8000, "超时强制中止未生效");
        Assert.True(proxy2.InFlightCaptures == 0 || proxy2.InFlightCaptures <= 1);
        release2.TrySetResult();
        proxy2.Dispose();
    }

    /// <summary>
    /// 回归：InFlightModels 必须在流式生成期间报告正在跑的模型、结束后清空
    /// （悬浮球"当前有模型正在跑"的判定依据）。
    /// </summary>
    [Fact]
    public async Task Proxy_InFlightModels_ReportsModelWhileStreaming()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var upstream = await MockUpstream.StartAsync(async (_, ctx, _) =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync(": wait\n\n");
            await ctx.Response.Body.FlushAsync();
            await release.Task;
            await ctx.Response.WriteAsync("data: {\"model\":\"test-m\",\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}\n\n");
            await ctx.Response.WriteAsync("data: [DONE]\n\n");
        });
        var cfg = Config(upstream.Port);
        var (proxy, _, addr) = NewProxy(cfg, new ProxyOptions { SseReadCap = TimeSpan.FromSeconds(30) });
        proxy.Start(addr);
        Assert.Empty(proxy.InFlightModels);

        using var client = new HttpClient();
        var request = client.PostAsync(ProxyUrl(addr, "/v1/chat/completions"),
            new StringContent("""{"model":"test-m","stream":true}""", Encoding.UTF8, "application/json"));
        await TestHelpers.WaitForAsync(() => proxy.InFlightCaptures == 1, TimeSpan.FromSeconds(10), "在途捕获未建立");
        Assert.Contains("test-m", proxy.InFlightModels);

        release.TrySetResult();
        await TestHelpers.WaitForAsync(() => proxy.InFlightCaptures == 0, TimeSpan.FromSeconds(10), "在途未归零");
        Assert.Empty(proxy.InFlightModels);
        await request;
        proxy.Dispose();
    }

    /// <summary>
    /// 回归：Stop 必须能在带 SynchronizationContext 的线程（WPF UI 线程）上安全调用。
    /// 宿主停止路径内部 await 默认捕获上下文；若在同一线程 GetResult() 阻塞，续体被排进
    /// 该线程的队列 → 互锁、进程永不退出（实测托盘「退出」后只能任务管理器结束）。
    /// 这里用一个"只入队不执行"的上下文模拟被同步阻塞的 UI 派发线程。
    /// </summary>
    [Fact]
    public async Task Proxy_Stop_OnThreadWithSynchronizationContext_DoesNotDeadlock()
    {
        using var upstream = await MockUpstream.StartAsync(
            MockResponses.JsonResponse("""{"usage":{"prompt_tokens":5,"completion_tokens":5,"total_tokens":10}}"""));
        var cfg = Config(upstream.Port);
        var (proxy, _, addr) = NewProxy(cfg);
        proxy.Start(addr);

        var blocking = new BlockingSynchronizationContext();
        var stopTask = Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(blocking);
            try { proxy.Stop(TimeSpan.FromSeconds(2)); }
            finally { SynchronizationContext.SetSynchronizationContext(null); }
        });

        var done = await TestHelpers.TryWaitForAsync(() => stopTask.IsCompleted, TimeSpan.FromSeconds(20));
        Assert.True(done, $"Stop 在带 SynchronizationContext 的线程上互锁（残留续体 {blocking.PendingPosts} 个）");
        Assert.Equal(0, blocking.PendingPosts);
        Assert.False(proxy.IsListening);
        proxy.Dispose();
    }

    /// <summary>只把续体揽入计数、永不执行 —— 模拟被同步阻塞的 UI 派发线程。</summary>
    private sealed class BlockingSynchronizationContext : SynchronizationContext
    {
        private int _pending;
        public int PendingPosts => Volatile.Read(ref _pending);

        public override void Post(SendOrPostCallback d, object? state) => Interlocked.Increment(ref _pending);

        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }
}
