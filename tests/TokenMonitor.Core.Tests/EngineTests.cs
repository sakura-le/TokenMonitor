using System.Text;
using TokenMonitor.Core.Events;
using TokenMonitor.Core.Proxy;
using TokenMonitor.Core.Stats;

namespace TokenMonitor.Core.Tests;

/// <summary>MonitorEngine 端到端集成（硬性要求 a/b）+ 生命周期 + C17 事件联动。</summary>
public class EngineTests
{
    private static readonly string JsonUpstreamResponse =
        """{"id":"c1","object":"chat.completion","model":"test-model","usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10,"completion_tokens":50,"total_tokens":150}}""";

    private static readonly string SseUsageLine =
        """data: {"model":"test-model","usage":{"prompt_tokens":100,"prompt_cache_hit_tokens":90,"prompt_cache_miss_tokens":10,"completion_tokens":50,"completion_tokens_details":{"reasoning_tokens":20},"total_tokens":150}}""";

    private static async Task<MonitorEngine> StartEngineAsync(TempDir dir, MockUpstream upstream,
        Action<List<TokenMonitor.Core.Config.ProviderConfig>>? tweak = null)
    {
        var engine = TestHelpers.NewEngine(dir.Path);
        var port = TestHelpers.FreePort();
        engine.Config.SaveProxy(new TokenMonitor.Core.Config.ProxyConfig($"127.0.0.1:{port}", null,
        [
            new TokenMonitor.Core.Config.ProviderConfig("Up", upstream.BaseUrl, "sk-up", ["test-"], null, null),
        ]));
        tweak?.Invoke(engine.Config.Proxy.Providers.ToList());
        await engine.StartAsync(CancellationToken.None);
        return engine;
    }

    // —— 端到端集成 a：流式 SSE ——

    [Fact]
    public async Task E2E_Stream_UsageCapturedNormalizedPricedAndPersisted()
    {
        // a) 流式：Core 内起 mock 上游回 SSE 含最终 usage → 经 IProxyEngine →
        //    断言捕获/归一化/计价/双桶/daily
        using var dir = new TempDir();
        using var upstream = await MockUpstream.StartAsync(MockResponses.SseResponse(
            """data: {"model":"test-model","choices":[{"delta":{"content":"hi"}}]}""",
            SseUsageLine,
            "data: [DONE]"));
        var engine = await StartEngineAsync(dir, upstream);
        try
        {
            // 计价配置：CNY，input=2/1M、cache=0.4/1M、output=6/1M（每时段全价）
            engine.Pricing.UpdatePricingVersion("Up/test-model", null, "CNY",
                [new TokenMonitor.Core.Pricing.PriceRule(0, 24, null, 2, 0.4, 6)]);
            var expectedCost = 90 * 0.4 / 1e6 + 10 * 2 / 1e6 + 50 * 6 / 1e6;

            using var client = new HttpClient();
            var resp = await client.PostAsync($"http://{engine.Proxy.ListenAddr}/v1/chat/completions",
                new StringContent("""{"model":"test-model","stream":true}""", Encoding.UTF8, "application/json"));
            Assert.True(resp.IsSuccessStatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            Assert.Contains("[DONE]", text); // SSE 已转发

            // 捕获+归一化+计价+双桶（快照断言，事件驱动）
            var snap = await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcModels.Any(m => m.Model == "test-model" && m.RequestCount == 1));
            var utc = snap.UtcModels[0];
            Assert.Equal((100, 90, 10, 50, 20, 150), (utc.PromptTokens, utc.CacheHitTokens,
                utc.CacheMissTokens, utc.CompletionTokens, utc.ReasoningTokens, utc.TotalTokens));
            Assert.Equal(100, utc.MulPrompt); // 无倍率配置 → 1.0
            Assert.Equal(150, utc.MulTotal);
            Assert.Equal(expectedCost, utc.CostCNY, 10); // 冻结成本
            Assert.Equal(0, utc.CostUSD, 10);
            Assert.Equal((100, 90, 10, 50, 20, 150), (snap.LocalModels[0].PromptTokens, snap.LocalModels[0].CacheHitTokens,
                snap.LocalModels[0].CacheMissTokens, snap.LocalModels[0].CompletionTokens, snap.LocalModels[0].ReasoningTokens, snap.LocalModels[0].TotalTokens));
            Assert.Equal(expectedCost, snap.LocalModels[0].CostCNY, 10); // Local 口径同价（同时段）
            Assert.Equal(150, snap.UtcSummary.TotalTokens);

            // daily 持久化（Flush 后读 DB）
            engine.Store.Flush();
            var daily = engine.Store.GetTodayUtc().Single();
            Assert.Equal(150, daily.TotalTokens);
            Assert.Equal(1, daily.RequestCount);
            Assert.Equal(expectedCost, daily.CostCNY, 10);
            // usage_log 原始行
            Assert.Single(engine.Store.GetLogsByRange(0, long.MaxValue));
        }
        finally
        {
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
    }

    // —— 端到端集成 b：非流式 JSON ——

    [Fact]
    public async Task E2E_Json_UsageCapturedAndPersisted()
    {
        // b) 非流式 JSON 同路径
        using var dir = new TempDir();
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse(JsonUpstreamResponse));
        var engine = await StartEngineAsync(dir, upstream);
        try
        {
            using var client = new HttpClient();
            var resp = await client.PostAsync($"http://{engine.Proxy.ListenAddr}/v1/chat/completions",
                new StringContent("""{"model":"test-model","stream":false}""", Encoding.UTF8, "application/json"));
            Assert.True(resp.IsSuccessStatusCode);
            Assert.Contains("chat.completion", await resp.Content.ReadAsStringAsync());

            var snap = await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcModels.Any(m => m.Model == "test-model" && m.RequestCount == 1));
            Assert.Equal(150, snap.UtcModels[0].TotalTokens);
            Assert.Equal(90, snap.UtcModels[0].CacheHitTokens);

            engine.Store.Flush();
            var daily = engine.Store.GetTodayUtc().Single();
            Assert.Equal(150, daily.TotalTokens);
            Assert.Single(engine.Store.GetLogsByRange(0, long.MaxValue));
            // 双口径一致性 [C3]：Local 桶与 UTC 桶 total 相同
            Assert.Equal(daily.TotalTokens, snap.LocalModels[0].TotalTokens);
        }
        finally
        {
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
    }

    // —— 生命周期 ——

    [Fact]
    public async Task Engine_StartStop_LifecycleEnforced()
    {
        using var dir = new TempDir();
        var engine = TestHelpers.NewEngine(dir.Path);
        TestHelpers.ConfigureProvider(engine);
        await engine.StartAsync(CancellationToken.None);
        Assert.True(engine.Proxy.IsListening);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(CancellationToken.None)); // 重复启动
        await engine.StopAsync();
        Assert.False(engine.Proxy.IsListening);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StopAsync()); // 重复停止
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Engine_ProxyStateChanged_PublishedOnBus()
    {
        using var dir = new TempDir();
        var engine = TestHelpers.NewEngine(dir.Path);
        TestHelpers.ConfigureProvider(engine);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = engine.Bus.Subscribe<ProxyStateChangedEvent>(e =>
        {
            if (e.IsListening) started.TrySetResult(true);
        });
        await engine.StartAsync(CancellationToken.None);
        Assert.True(await started.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        await engine.StopAsync();
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Engine_StartWithBusyPort_ContinuesRunning()
    {
        // 附录 B-9：端口占用 → 进程继续运行（不退出），ProxyStateChanged(false, error)
        using var dir = new TempDir();
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        blocker.Start();
        try
        {
            var engine = TestHelpers.NewEngine(dir.Path);
            var busyPort = ((System.Net.IPEndPoint)blocker.LocalEndpoint).Port;
            engine.Config.SaveProxy(new TokenMonitor.Core.Config.ProxyConfig($"127.0.0.1:{busyPort}", null, []));
            var errorSeen = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = engine.Bus.Subscribe<ProxyStateChangedEvent>(e =>
            {
                if (!e.IsListening && e.Error is not null) errorSeen.TrySetResult(e.Error);
            });
            await engine.StartAsync(CancellationToken.None); // 不抛
            Assert.False(engine.Proxy.IsListening);
            Assert.NotNull(await errorSeen.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
        finally
        {
            blocker.Stop();
        }
    }

    // —— [C17] 时区切换：事件广播 + 查询缓存失效 ——

    [Fact]
    public async Task Engine_SetTimezone_PublishesEventsAndRebuilds()
    {
        using var dir = new TempDir();
        var engine = TestHelpers.NewEngine(dir.Path);
        TestHelpers.ConfigureProvider(engine);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            var dayRolled = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var configChanged = new TaskCompletionSource<TokenMonitor.Core.Events.ConfigSectionFlags>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var s1 = engine.Bus.Subscribe<DayRolledOver>(e => dayRolled.TrySetResult(e.OffsetMin));
            using var s2 = engine.Bus.Subscribe<ConfigChanged>(e => configChanged.TrySetResult(e.Sections));

            engine.SetTimezone(480);

            Assert.Equal(480, await dayRolled.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            var flags = await configChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(flags.HasFlag(TokenMonitor.Core.Events.ConfigSectionFlags.Settings));
            Assert.Equal(480, engine.Config.Settings.OffsetMin);
            Assert.Equal(480, engine.Pricing.EffectiveContext.OffsetMin);

            // 非法偏移被拒绝
            Assert.Throws<ArgumentException>(() => engine.SetTimezone(999));
        }
        finally
        {
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
    }

    // —— 数据命令联动 ——

    [Fact]
    public async Task Engine_ResetToday_ClearsBucketsAndMissed()
    {
        // 经真实代理摄入一笔 + 造一条漏抓 → ResetToday：卡片清零 + 漏抓横幅清零 + log 层删除
        using var dir = new TempDir();
        using var upstream = await MockUpstream.StartAsync(MockResponses.JsonResponse(
            "{\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":5,\"total_tokens\":15}}"));
        var engine = TestHelpers.NewEngine(dir.Path);
        var port = TestHelpers.FreePort();
        engine.Config.SaveProxy(new TokenMonitor.Core.Config.ProxyConfig($"127.0.0.1:{port}", null,
        [
            new TokenMonitor.Core.Config.ProviderConfig("Up", upstream.BaseUrl, "sk", ["test-"], null, null),
        ]));
        await engine.StartAsync(CancellationToken.None);
        try
        {
            using var client = new HttpClient();
            await client.PostAsync($"http://{engine.Proxy.ListenAddr}/v1/chat/completions",
                new StringContent("{\"model\":\"test-model\"}", Encoding.UTF8, "application/json"));
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            engine.Store.AddMissed(new TokenMonitor.Core.Storage.MissedLogRow(0, now, "Up", "test-model", 200, "x"));
            // 先经 DB 直查确认漏抓行已落库（Store 直写可靠），再等快照经 TTL 缓存刷新后可见
            await TestHelpers.WaitForAsync(() => engine.Store.GetMissed(0, long.MaxValue).Count == 1,
                TimeSpan.FromSeconds(5), "漏抓行未落库");
            var snap = await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcModels.Any(m => m.RequestCount == 1 && m.MissedCount >= 1));

            engine.ResetToday(null);

            // [C8] 卡片清零 + 漏抓横幅清零（快照聚合 + DB 双重确认）
            await TestHelpers.WaitForSnapshotAsync(engine.Bus,
                s => s.UtcSummary.RequestCount == 0 && s.MissedCaptures == 0 && s.UtcModels.Count > 0);
            engine.Store.Flush();
            Assert.Empty(engine.Store.GetLogsByRange(0, long.MaxValue)); // 铁律 5：log 层已删
            Assert.Empty(engine.Store.GetMissed(0, long.MaxValue));      // [C8] missed 双窗口清零
        }
        finally
        {
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
    }

    [Fact]
    public async Task Engine_ReloadProxyConfig_HotSwapsRoutingTable()
    {
        using var dir = new TempDir();
        var engine = TestHelpers.NewEngine(dir.Path);
        TestHelpers.ConfigureProvider(engine);
        await engine.StartAsync(CancellationToken.None);
        try
        {
            var addrBefore = engine.Proxy.ListenAddr;
            // 修改配置文件内容（新增 provider + 改名）→ 重载
            var cfg = engine.Config.Proxy;
            engine.Config.SaveProxy(cfg with
            {
                Providers = [new TokenMonitor.Core.Config.ProviderConfig("New", "https://new.example.com", "", ["new-"], null, null)],
            });
            var changed = new TaskCompletionSource<TokenMonitor.Core.Events.ConfigSectionFlags>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var sub = engine.Bus.Subscribe<ConfigChanged>(e => changed.TrySetResult(e.Sections));
            engine.ReloadProxyConfig();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(addrBefore, engine.Proxy.ListenAddr); // 地址未变 → 不重建监听
            Assert.Equal("New", engine.Config.Proxy.Providers[0].Name);
            Assert.True(engine.Proxy.IsListening);
        }
        finally
        {
            await engine.StopAsync();
            await engine.DisposeAsync();
        }
    }
}
