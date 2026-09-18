using TokenMonitor.Core.Config;

namespace TokenMonitor.Core.Tests;

/// <summary>配置服务回归：S6 + 校验/DPAPI/损坏恢复/原子写。</summary>
public class ConfigTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly ConfigService _svc;

    public ConfigTests() => _svc = new ConfigService(_dir.Path);

    public void Dispose() => _svc.Dispose();

    // —— [S6] 空 providers 合并默认 ——

    [Fact]
    public void Config_EmptyProviders_MergedDefaultsWarn()
    {
        // [S6] config.json providers=[] → 合并默认 4 项；listen_addr/default_provider 保留
        const string json = """
            { "listen_addr": "127.0.0.1:9999", "default_provider": "GLM", "providers": [] }
            """;
        File.WriteAllText(Path.Combine(_dir.Path, "config.json"), json);
        using var svc = new ConfigService(_dir.Path);
        Assert.Equal(4, svc.Proxy.Providers.Count);
        Assert.Equal("127.0.0.1:9999", svc.Proxy.ListenAddr); // 用户配置保留
        Assert.Equal("GLM", svc.Proxy.DefaultProvider);       // 合法 default 保留
        Assert.All(svc.Proxy.Providers, p => Assert.Equal("", p.ApiKey)); // 安全红线：默认密钥为空
        Assert.Equal(["glm-", "zhipu-"], svc.Proxy.Providers.Single(p => p.Name == "GLM").ModelPrefix);
        Assert.Equal(32768, svc.Proxy.Providers.Single(p => p.Name == "GLM").MaxTokens);
    }

    [Fact]
    public void Config_InvalidDefaultProvider_Ignored()
    {
        const string json = """{ "listen_addr": "127.0.0.1:8280", "default_provider": "NoSuch", "providers": [{"name":"A","base_url":"https://a.example.com","model_prefix":["a-"]}] }""";
        File.WriteAllText(Path.Combine(_dir.Path, "config.json"), json);
        using var svc = new ConfigService(_dir.Path);
        Assert.Null(svc.Proxy.DefaultProvider); // 不存在 → 忽略
    }

    [Theory]
    [InlineData("0.0.0.0:8280")]      // 非回环
    [InlineData("example.com:8280")]  // 非回环
    [InlineData("127.0.0.1:99999")]   // 端口越界
    [InlineData("127.0.0.1")]         // 无端口
    [InlineData("")]
    public void Config_InvalidListenAddr_FallsBackToDefault(string addr)
    {
        var raw = new ProxyConfig(addr, null, [new ProviderConfig("A", "https://a.example.com", "", ["a-"], null, null)]);
        var repaired = _svc.ValidateAndRepair(raw);
        Assert.Equal("127.0.0.1:8280", repaired.ListenAddr);
    }

    [Theory]
    [InlineData("localhost:8280")]
    [InlineData("[::1]:8280")]
    [InlineData("127.0.0.1:8280")]
    public void Config_LoopbackAddrs_Accepted(string addr)
    {
        var raw = new ProxyConfig(addr, null, [new ProviderConfig("A", "https://a.example.com", "", ["a-"], null, null)]);
        Assert.Equal(addr, _svc.ValidateAndRepair(raw).ListenAddr);
    }

    [Fact]
    public void Config_ProviderValidation_DisablesInvalidAndDeduplicates()
    {
        var raw = new ProxyConfig("127.0.0.1:8280", null,
        [
            new ProviderConfig("", "https://a.example.com", "", ["a-"], null, null),          // 无 name → 停用
            new ProviderConfig("Bad", "not-a-url", "", ["b-"], null, null),                   // 非法 URL → 停用
            new ProviderConfig("Dup", "https://c.example.com", "", ["c-"], null, null),
            new ProviderConfig("Dup", "https://d.example.com", "", ["d-"], null, null),       // 重名 → 首个生效
            new ProviderConfig("Prefix", "https://e.example.com", "", ["dup-", "dup-"], -5, null), // 重复前缀 + 非法 max_tokens
        ]);
        var repaired = _svc.ValidateAndRepair(raw);
        var names = repaired.Providers.Select(p => p.Name).ToList();
        Assert.Equal(["Dup", "Prefix"], names);
        Assert.Single(repaired.Providers.Single(p => p.Name == "Prefix").ModelPrefix); // 重复前缀去重
        Assert.Null(repaired.Providers.Single(p => p.Name == "Prefix").MaxTokens);     // -5 → null（不钳制）
    }

    [Fact]
    public void Config_StripParams_MissingGetsDefault_EmptyRespected()
    {
        // [C15] 缺省 → 默认剥离项；显式 [] = 不剥离
        var raw = new ProxyConfig("127.0.0.1:8280", null,
        [
            new ProviderConfig("A", "https://a.example.com", "", ["a-"], null, null),
            new ProviderConfig("B", "https://b.example.com", "", ["b-"], null, []),
        ]);
        var repaired = _svc.ValidateAndRepair(raw);
        Assert.Equal(["enable_thinking", "reasoning_effort"], repaired.Providers.Single(p => p.Name == "A").StripParams);
        Assert.Empty(repaired.Providers.Single(p => p.Name == "B").StripParams!);
    }

    // —— DPAPI（G2）——

    [Fact]
    public void Config_EncryptDecryptApiKey_DpapiRoundtrip()
    {
        var cipher = _svc.EncryptApiKey("sk-secret-123");
        Assert.StartsWith("dpapi:", cipher, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret-123", cipher);
        Assert.Equal("sk-secret-123", _svc.DecryptApiKey(cipher));
    }

    [Fact]
    public void Config_DecryptApiKey_PlaintextPassthrough()
    {
        // 兼容旧明文配置：无前缀原样返回
        Assert.Equal("sk-legacy", _svc.DecryptApiKey("sk-legacy"));
        Assert.Equal("", _svc.DecryptApiKey(""));
    }

    [Fact]
    public void Config_SaveProxy_EncryptsPlaintextKeys()
    {
        // 落盘态：明文 → dpapi:；已是 dpapi: 的保持；文件内容无明文
        _svc.SaveProxy(new ProxyConfig("127.0.0.1:8280", null,
        [
            new ProviderConfig("A", "https://a.example.com", "sk-plain", ["a-"], null, null),
            new ProviderConfig("B", "https://b.example.com", "dpapi:QUJD", ["b-"], null, null),
        ]));
        var text = File.ReadAllText(Path.Combine(_dir.Path, "config.json"));
        Assert.DoesNotContain("sk-plain", text); // 明文不落盘
        Assert.Contains("dpapi:", text);
        Assert.Equal("sk-plain", _svc.Proxy.Providers.Single(p => p.Name == "A").ApiKey); // 内存态明文
        Assert.Equal("dpapi:QUJD", _svc.Proxy.Providers.Single(p => p.Name == "B").ApiKey);
        // 重载后仍能解密
        _svc.ReloadProxy();
        Assert.Equal("sk-plain", _svc.Proxy.Providers.Single(p => p.Name == "A").ApiKey);
    }

    [Fact]
    public void Config_CorruptFile_RenamedAndDefaultsWritten()
    {
        // 损坏 → 改名 .corrupt-* 保留现场 → 默认生效
        var path = Path.Combine(_dir.Path, "settings.json");
        File.WriteAllText(path, "{ not valid json !!!");
        using var svc = new ConfigService(_dir.Path);
        Assert.Equal(0, svc.Settings.OffsetMin); // 默认
        Assert.True(File.Exists(path));          // 新默认已写
        var corrupt = Directory.GetFiles(_dir.Path, "settings.json.corrupt-*");
        Assert.Single(corrupt);                  // 现场保留
    }

    [Fact]
    public void Config_SettingsValidation()
    {
        // offset 非法 → 0；effmode 非法 → utc；opacity ≤0 → 255
        const string json = """{ "offset_min": 500, "effective_date_mode": "xx", "ball_opacity": 0, "ball_topmost": false }""";
        File.WriteAllText(Path.Combine(_dir.Path, "settings.json"), json);
        using var svc = new ConfigService(_dir.Path);
        Assert.Equal(0, svc.Settings.OffsetMin);
        Assert.Equal("utc", svc.Settings.EffectiveDateMode);
        Assert.Equal(255, svc.Settings.BallOpacity);
        Assert.False(svc.Settings.BallTopmost);

        var halfHour = new AppSettings(330, "local", 128, true);
        svc.SaveSettings(halfHour);
        Assert.Equal(330, svc.Settings.OffsetMin); // 半时区合法
        Assert.Equal("local", svc.Settings.EffectiveDateMode);
    }

    [Fact]
    public async Task Config_UiState_DebouncedSave_FlushForces()
    {
        _svc.SaveUiState(new UiState(null, ["P/m"],
            new HashSet<string>(StringComparer.Ordinal) { "Q/x" },
            new Dictionary<string, string>(StringComparer.Ordinal) { ["P/m"] = "utc" },
            new Dictionary<string, bool>(StringComparer.Ordinal) { ["P/m"] = true },
            new BallState(1.5, 2.5)));
        // 防抖 300ms 后落盘
        await TestHelpers.WaitForAsync(() => File.Exists(Path.Combine(_dir.Path, "ui_state.json")),
            TimeSpan.FromSeconds(5), "防抖保存未落盘");
        using var svc2 = new ConfigService(_dir.Path);
        Assert.Equal(["P/m"], svc2.Ui.CardOrder);
        Assert.Contains("Q/x", svc2.Ui.HiddenCards);
        Assert.Equal("utc", svc2.Ui.CardScope["P/m"]);
        Assert.True(svc2.Ui.CardMultiplierView["P/m"]);
        Assert.Equal(1.5, svc2.Ui.Ball!.Left);
        Assert.Equal("GraphiteTerminal", svc2.Ui.ActiveSkin);
    }

    [Fact]
    public void Config_UiState_OpacityClamped()
    {
        _svc.SaveUiState(new UiState(
            new PanelWindowState(1, 2, 3, 4, 5.0, false), [], // opacity=5.0 越界
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new Dictionary<string, bool>(StringComparer.Ordinal), null));
        Assert.Equal(1.0, _svc.Ui.Panel!.Opacity); // 夹取 [0.3,1.0]
    }
}
