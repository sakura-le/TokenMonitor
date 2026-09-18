using System.Security.Cryptography;
using System.Text.Json;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Config;

/// <summary>
/// IConfigService 默认实现（01-§3）：
/// - 文件不存在 → 写默认；JSON 损坏 → 原文件改名 .corrupt-yyyyMMdd_HHmmss 保留现场、写默认、Warn（附录 B-1）；
/// - 所有保存为"临时文件 + File.Replace"原子写；ui_state 防抖 300ms；
/// - API 密钥 DPAPI（CurrentUser）加密，明文不落盘、不进日志（安全红线 §0.7）。
/// </summary>
public sealed class ConfigService : IConfigService, IDisposable
{
    private static readonly string[] DefaultStripParams = ["enable_thinking", "reasoning_effort"];
    private static readonly TimeSpan UiDebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly object _saveLock = new();      // §8.1：三个 JSON 文件原子写串行
    private readonly object _stateGate = new();
    private readonly TimeProvider _clock;
    private readonly string _configPath;
    private readonly string _settingsPath;
    private readonly string _uiStatePath;
    private readonly HashSet<string> _plaintextWarned = new(StringComparer.Ordinal);
    private Timer? _uiDebounceTimer;
    private UiState? _pendingUiState;
    private bool _disposed;

    public string DataDir { get; }
    public event EventHandler<ConfigSection>? Saved;

    public ConfigService(string dataDir, TimeProvider? clock = null)
    {
        DataDir = dataDir;
        _clock = clock ?? TimeProvider.System;
        _configPath = Path.Combine(dataDir, "config.json");
        _settingsPath = Path.Combine(dataDir, "settings.json");
        _uiStatePath = Path.Combine(dataDir, "ui_state.json");
        Directory.CreateDirectory(dataDir);
        Proxy = ValidateAndRepair(LoadJson<ProxyConfigDto>(_configPath) is { } dto ? FromFileDto(dto) : null);
        Settings = Validate(LoadJson<SettingsDto>(_settingsPath) is { } s ? FromDto(s) : null);
        Ui = LoadUiState();
        // 文件不存在 → 写默认（§3 通用规则；原 config.Load 语义保持）
        if (!File.Exists(_configPath)) SaveProxy(Proxy);
        if (!File.Exists(_settingsPath)) SaveSettings(Settings);
    }

    public ProxyConfig Proxy { get; private set; }
    public AppSettings Settings { get; private set; }
    public UiState Ui { get; private set; }

    // —— 校验修复（[S6]，§3.1 校验表）——

    public ProxyConfig ValidateAndRepair(ProxyConfig? raw)
    {
        var listen = ValidateListenAddr(raw?.ListenAddr);
        var providers = new List<ProviderConfig>();
        var usedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in raw?.Providers ?? [])
        {
            if (string.IsNullOrWhiteSpace(p.Name))
            {
                Logger.Warn("Config", "provider 缺少 name，已停用该 provider");
                continue;
            }
            if (!usedNames.Add(p.Name))
            {
                Logger.Warn("Config", $"provider name 重复：{p.Name}，仅首个生效");
                continue;
            }
            if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                Logger.Warn("Config", $"provider {p.Name} 的 base_url 非法（{p.BaseUrl}），已停用该 provider");
                continue;
            }
            var prefixList = (p.ModelPrefix ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            if (prefixList.Count == 0)
                Logger.Warn("Config", $"provider {p.Name} 的 model_prefix 为空，不可被路由");
            var prefixes = new List<string>();
            foreach (var prefix in prefixList)
            {
                if (!usedPrefixes.Add(prefix))
                {
                    Logger.Warn("Config", $"model_prefix 跨 provider 重复：{prefix}，仅首个 provider 生效");
                    continue;
                }
                prefixes.Add(prefix);
            }
            providers.Add(p with
            {
                ModelPrefix = prefixes,
                MaxTokens = p.MaxTokens is > 0 ? p.MaxTokens : null,
                StripParams = p.StripParams?.ToList() ?? [.. DefaultStripParams], // null=缺省（[C15]）；显式 [] 保留=不剥离
            });
        }

        if (providers.Count == 0)
        {
            // [S6] 缺失/空数组 → 合并默认项并告警（不 bricks；用户的 listen_addr/default_provider 保留）
            Logger.Warn("Config", "providers 缺失或为空，已合并默认 4 个提供商（api_key 为空，请手动填写）");
            providers.AddRange(DefaultProviders());
        }

        string? defaultProvider = raw?.DefaultProvider;
        if (!string.IsNullOrEmpty(defaultProvider) && providers.All(x => x.Name != defaultProvider))
        {
            Logger.Warn("Config", $"default_provider={defaultProvider} 不在 providers 中，已忽略");
            defaultProvider = null;
        }

        return new ProxyConfig(listen, defaultProvider, providers);
    }

    private AppSettings Validate(AppSettings? raw)
    {
        var offset = raw?.OffsetMin ?? 0;
        if (offset is < -720 or > 840 || offset % 30 != 0)
        {
            Logger.Warn("Config", $"offset_min={offset} 非法（[-720,840] 且 30 分钟步进），回退 0");
            offset = 0;
        }
        var mode = raw?.EffectiveDateMode;
        if (mode != "utc" && mode != "local")
        {
            if (mode is not null && mode.Length > 0) Logger.Warn("Config", $"effective_date_mode={mode} 非法，回退 utc");
            mode = "utc";
        }
        var opacity = raw?.BallOpacity ?? 255;
        if (opacity <= 0) opacity = 255; // 原版语义：0/缺省视为 255
        if (opacity > 255) opacity = 255;
        return new AppSettings(offset, mode ?? "utc", opacity, raw?.BallTopmost ?? true);
    }

    private static string ValidateListenAddr(string? addr)
    {
        // 必须 host:port 且 host ∈ {127.0.0.1, localhost, ::1}（红线：只听回环）；非法→回默认+Warn
        const string fallback = "127.0.0.1:8280";
        if (string.IsNullOrWhiteSpace(addr)) return fallback;
        string host;
        string portPart;
        if (addr.StartsWith('['))
        {
            // [::1]:8280 形式（IPv6）
            var close = addr.IndexOf(']');
            if (close < 0 || close + 2 > addr.Length || addr[close + 1] != ':')
            {
                Logger.Warn("Config", $"listen_addr={addr} 非法，回退 {fallback}");
                return fallback;
            }
            host = addr[1..close];
            portPart = addr[(close + 2)..];
        }
        else
        {
            var idx = addr.LastIndexOf(':');
            if (idx <= 0 || idx == addr.Length - 1)
            {
                Logger.Warn("Config", $"listen_addr={addr} 非法，回退 {fallback}");
                return fallback;
            }
            host = addr[..idx];
            portPart = addr[(idx + 1)..];
        }
        if (!int.TryParse(portPart, out var port) || port is < 1 or > 65535)
        {
            Logger.Warn("Config", $"listen_addr={addr} 端口非法，回退 {fallback}");
            return fallback;
        }
        if (host is not ("127.0.0.1" or "localhost" or "::1"))
        {
            Logger.Warn("Config", $"listen_addr={addr} 非回环地址（安全红线），回退 {fallback}");
            return fallback;
        }
        return addr;
    }

    internal static IReadOnlyList<ProviderConfig> DefaultProviders() =>
    [
        new("DeepSeek", "https://api.deepseek.com", "", ["deepseek-"], null, null),
        new("GLM", "https://open.bigmodel.cn/api/paas/v4", "", ["glm-", "zhipu-"], 32768, null),
        new("Moonshot", "https://api.moonshot.cn/v1", "", ["moonshot-", "kimi-"], null, null),
        new("MiMo", "https://api.xiaomi.com/v1", "", ["mimo-"], null, null),
    ];

    // —— 保存 ——

    public void SaveProxy(ProxyConfig cfg)
    {
        var validated = ValidateAndRepair(cfg);
        // 明文密钥一律立即加密落盘（安全红线）；已是 dpapi: 前缀的保持不变；内存态保持明文（§2.3.5）
        var forFile = validated with
        {
            Providers = validated.Providers
                .Select(p => p with
                {
                    ApiKey = p.ApiKey.Length > 0 && !p.ApiKey.StartsWith("dpapi:", StringComparison.Ordinal)
                        ? EncryptApiKey(p.ApiKey)
                        : p.ApiKey
                })
                .ToList(),
        };
        var dto = ToDto(forFile);
        lock (_saveLock)
        {
            try
            {
                WriteAtomic(_configPath, Serialize(dto));
            }
            catch (Exception ex)
            {
                throw new ConfigException($"保存 config.json 失败: {ex.Message}", ex);
            }
            Proxy = validated;
        }
        Saved?.Invoke(this, ConfigSection.Proxy);
    }

    public void SaveSettings(AppSettings s)
    {
        var validated = Validate(s);
        lock (_saveLock)
        {
            try
            {
                WriteAtomic(_settingsPath, Serialize(ToDto(validated)));
            }
            catch (Exception ex)
            {
                throw new ConfigException($"保存 settings.json 失败: {ex.Message}", ex);
            }
            Settings = validated;
        }
        Saved?.Invoke(this, ConfigSection.Settings);
    }

    public void SaveUiState(UiState u)
    {
        // 校验：panel.opacity 夹取 [0.3,1.0]（§3.4）
        var normalized = u.Panel is null
            ? u
            : u with { Panel = u.Panel with { Opacity = Math.Clamp(u.Panel.Opacity, 0.3, 1.0) } };
        lock (_stateGate)
        {
            Ui = normalized;
            if (_disposed) { WriteUiStateImmediately(normalized); return; }
            _pendingUiState = normalized;
            _uiDebounceTimer?.Dispose();
            // 防抖 300ms 合并写入（ timers 用回调内取最新 pending，避免竞态）
            _uiDebounceTimer = new Timer(_ =>
            {
                UiState? pending;
                lock (_stateGate)
                {
                    pending = _pendingUiState;
                    _pendingUiState = null;
                }
                if (pending is null) return;
                lock (_saveLock)
                {
                    try { WriteAtomic(_uiStatePath, Serialize(ToDto(pending))); }
                    catch (Exception ex) { Logger.Warn("Config", $"保存 ui_state.json 失败: {ex.Message}"); }
                }
                Saved?.Invoke(this, ConfigSection.UiState);
            }, null, UiDebounceInterval, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>防抖队列强制落盘（退出序列步骤 4；具体类成员，Engine 持具体引用调用）。</summary>
    public void FlushUiState()
    {
        UiState? pending;
        lock (_stateGate)
        {
            _uiDebounceTimer?.Dispose();
            _uiDebounceTimer = null;
            pending = _pendingUiState;
            _pendingUiState = null;
        }
        if (pending is null) return;
        WriteUiStateImmediately(pending);
    }

    private void WriteUiStateImmediately(UiState pending)
    {
        lock (_saveLock)
        {
            try { WriteAtomic(_uiStatePath, Serialize(ToDto(pending))); }
            catch (Exception ex) { Logger.Warn("Config", $"保存 ui_state.json 失败: {ex.Message}"); }
        }
    }

    public void ReloadProxy()
    {
        // 重读 → ValidateAndRepair → 更新；不抛（损坏时修复并告警日志）
        ProxyConfig? raw = null;
        try
        {
            raw = LoadJson<ProxyConfigDto>(_configPath) is { } dto ? FromFileDto(dto) : null;
        }
        catch (Exception ex)
        {
            Logger.Warn("Config", $"重读 config.json 失败，沿用内存配置: {ex.Message}");
            return;
        }
        lock (_saveLock)
        {
            Proxy = ValidateAndRepair(raw);
        }
        Saved?.Invoke(this, ConfigSection.Proxy);
    }

    // —— DPAPI（G2）——

    /// <summary>从文件加载：dpapi: 密文解密为内存明文（§2.3.5）；解密失败保留原值并告警，不 bricks。</summary>
    private ProxyConfig FromFileDto(ProxyConfigDto dto)
    {
        var cfg = FromDto(dto);
        return cfg with
        {
            Providers = cfg.Providers
                .Select(p => p.ApiKey.StartsWith("dpapi:", StringComparison.Ordinal)
                    ? p with
                    {
                        ApiKey = TryDecrypt(p.ApiKey, p.Name)
                    }
                    : p)
                .ToList(),
        };
    }

    private string TryDecrypt(string stored, string providerName)
    {
        try
        {
            return DecryptApiKey(stored);
        }
        catch (Exception ex)
        {
            Logger.Warn("Config", $"provider {providerName} 的 API 密钥解密失败（保留密文）: {ex.Message}");
            return stored;
        }
    }


    public string EncryptApiKey(string plain)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(plain);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return "dpapi:" + Convert.ToBase64String(encrypted);
    }

    public string DecryptApiKey(string stored)
    {
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) 
        {
            if (stored.Length > 0 && _plaintextWarned.Add(stored))
                Logger.Warn("Config", "检测到明文 API 密钥，保存时将自动加密（兼容旧配置读取）");
            return stored;
        }
        try
        {
            var bytes = Convert.FromBase64String(stored["dpapi:".Length..]);
            return System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            throw new ConfigException("解密 API 密钥失败（密文损坏或属于其它用户）", ex);
        }
    }

    // —— 文件 IO 基元 ——

    private UiState LoadUiState()
    {
        var dto = LoadJson<UiStateDto>(_uiStatePath);
        if (dto is null) return EmptyUiState();
        var opacity = Math.Clamp(dto.Panel?.Opacity ?? 1.0, 0.3, 1.0); // 校验：opacity 夹取 [0.3,1.0]
        return new UiState(
            dto.Panel is null ? null : new PanelWindowState(dto.Panel.Left, dto.Panel.Top, dto.Panel.Width, dto.Panel.Height, opacity, dto.Panel.Collapsed),
            dto.CardOrder ?? [],
            new HashSet<string>(dto.HiddenCards ?? [], StringComparer.Ordinal),
            dto.CardScope ?? new Dictionary<string, string>(StringComparer.Ordinal),
            dto.CardMultiplierView ?? new Dictionary<string, bool>(StringComparer.Ordinal),
            dto.Ball is null ? null : new BallState(dto.Ball.Left, dto.Ball.Top),
            dto.ActiveSkin ?? "GraphiteTerminal");
    }

    internal static UiState EmptyUiState() => new(null, [],
        new HashSet<string>(StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal),
        new Dictionary<string, bool>(StringComparer.Ordinal), null, "GraphiteTerminal");

    private T? LoadJson<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path); // 读取兼容 UTF-8 BOM
            var dto = JsonSerializer.Deserialize<T>(json, JsonOpts);
            if (dto is null)
            {
                Quarantine(path);
                return null;
            }
            return dto;
        }
        catch (JsonException)
        {
            Quarantine(path);
            return null;
        }
        catch (IOException ex)
        {
            Logger.Warn("Config", $"读取 {path} 失败: {ex.Message}");
            return null;
        }
    }

    private void Quarantine(string path)
    {
        // JSON 损坏 → 原文件改名 .corrupt-<时间戳> 保留现场（附录 B-1），下次保存写默认
        try
        {
            if (!File.Exists(path)) return;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var corrupt = $"{path}.corrupt-{stamp}";
            File.Move(path, corrupt);
            Logger.Warn("Config", $"配置文件损坏，已保留现场: {corrupt}");
        }
        catch (Exception ex)
        {
            Logger.Warn("Config", $"保留损坏配置现场失败: {ex.Message}");
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new System.Text.UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string Serialize(object dto) => JsonSerializer.Serialize(dto, JsonOpts);

    // —— DTO ↔ 模型 ——

    internal static ProxyConfig FromDto(ProxyConfigDto dto) => new(
        dto.ListenAddr ?? string.Empty,
        dto.DefaultProvider,
        (dto.Providers ?? []).Select(p => new ProviderConfig(
            p.Name ?? string.Empty,
            p.BaseUrl ?? string.Empty,
            p.ApiKey ?? string.Empty,
            p.ModelPrefix ?? [],
            p.MaxTokens,
            p.StripParams)).ToList());

    internal static ProxyConfigDto ToDto(ProxyConfig cfg) => new()
    {
        ListenAddr = cfg.ListenAddr,
        DefaultProvider = cfg.DefaultProvider,
        Providers = cfg.Providers.Select(p => new ProviderDto
        {
            Name = p.Name,
            BaseUrl = p.BaseUrl,
            ApiKey = p.ApiKey.Length == 0 ? string.Empty : p.ApiKey,
            ModelPrefix = [.. p.ModelPrefix],
            MaxTokens = p.MaxTokens,
            StripParams = p.StripParams?.ToList(),
        }).ToList(),
    };

    internal static AppSettings FromDto(SettingsDto dto) => new(
        dto.OffsetMin,
        dto.EffectiveDateMode,
        dto.BallOpacity,
        dto.BallTopmost ?? true);

    internal static SettingsDto ToDto(AppSettings s) => new()
    {
        OffsetMin = s.OffsetMin,
        EffectiveDateMode = s.EffectiveDateMode,
        BallOpacity = s.BallOpacity,
        BallTopmost = s.BallTopmost,
    };

    internal static UiStateDto ToDto(UiState u) => new()
    {
        Panel = u.Panel is null ? null : new PanelDto
        {
            Left = u.Panel.Left, Top = u.Panel.Top, Width = u.Panel.Width, Height = u.Panel.Height,
            Opacity = u.Panel.Opacity, Collapsed = u.Panel.Collapsed,
        },
        CardOrder = [.. u.CardOrder],
        HiddenCards = [.. u.HiddenCards],
        CardScope = new Dictionary<string, string>(u.CardScope, StringComparer.Ordinal),
        CardMultiplierView = new Dictionary<string, bool>(u.CardMultiplierView, StringComparer.Ordinal),
        Ball = u.Ball is null ? null : new BallDto { Left = u.Ball.Left, Top = u.Ball.Top },
        ActiveSkin = u.ActiveSkin,
    };

    public void Dispose()
    {
        UiState? pending;
        lock (_stateGate)
        {
            _disposed = true;
            _uiDebounceTimer?.Dispose();
            _uiDebounceTimer = null;
            pending = _pendingUiState;
            _pendingUiState = null;
        }
        if (pending is not null)
        {
            lock (_saveLock)
            {
                try { WriteAtomic(_uiStatePath, Serialize(ToDto(pending))); }
                catch { /* 退出路径尽力落盘 */ }
            }
        }
    }
}
