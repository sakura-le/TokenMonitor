using System.Text.Json.Serialization;

namespace TokenMonitor.Core.Config;

/// <summary>config.json 根（01-§3.1）。ApiKey 内存态为明文；落盘一律 "dpapi:&lt;b64&gt;"。</summary>
public sealed record ProxyConfig(
    string ListenAddr,
    string? DefaultProvider,
    IReadOnlyList<ProviderConfig> Providers);

/// <summary>单个提供商。ModelPrefix 大小写不敏感前缀匹配；MaxTokens=null=不钳制；StripParams=null=缺省剥离项，[]=不剥离。</summary>
public sealed record ProviderConfig(
    string Name,
    string BaseUrl,
    string ApiKey,
    IReadOnlyList<string> ModelPrefix,
    int? MaxTokens,
    IReadOnlyList<string>? StripParams);

/// <summary>settings.json（01-§3.3）。</summary>
public sealed record AppSettings(
    int OffsetMin,
    string EffectiveDateMode,
    int BallOpacity,
    bool BallTopmost);

/// <summary>ui_state.json（新文件；吸收原 multiplier_states.json 与前端 localStorage）。</summary>
public sealed record UiState(
    PanelWindowState? Panel,
    IReadOnlyList<string> CardOrder,
    IReadOnlySet<string> HiddenCards,
    IReadOnlyDictionary<string, string> CardScope,
    IReadOnlyDictionary<string, bool> CardMultiplierView,
    BallState? Ball,
    string ActiveSkin = "GraphiteTerminal"); // 附录 C v1.1 增补字段

/// <summary>面板窗口记忆（null=首启由 UI 默认决定）。</summary>
public sealed record PanelWindowState(double? Left, double? Top, double? Width, double? Height,
                                      double Opacity, bool Collapsed);

/// <summary>悬浮球位置记忆。</summary>
public sealed record BallState(double? Left, double? Top);

/// <summary>配置节（IConfigService.Saved 事件参数）。</summary>
public enum ConfigSection { Proxy, Settings, UiState, Pricing }

/// <summary>config.json / settings.json / ui_state.json 的 JSON DTO（snake_case 显式标注，与原版字段兼容）。</summary>
internal sealed class ProxyConfigDto
{
    [JsonPropertyName("listen_addr")] public string? ListenAddr { get; set; }
    [JsonPropertyName("default_provider")] public string? DefaultProvider { get; set; }
    [JsonPropertyName("providers")] public List<ProviderDto>? Providers { get; set; }
}

internal sealed class ProviderDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("base_url")] public string? BaseUrl { get; set; }
    [JsonPropertyName("api_key")] public string? ApiKey { get; set; }
    [JsonPropertyName("model_prefix")] public List<string>? ModelPrefix { get; set; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; set; }
    [JsonPropertyName("strip_params")] public List<string>? StripParams { get; set; }
}

internal sealed class SettingsDto
{
    [JsonPropertyName("offset_min")] public int OffsetMin { get; set; }
    [JsonPropertyName("effective_date_mode")] public string EffectiveDateMode { get; set; } = "utc";
    [JsonPropertyName("ball_opacity")] public int BallOpacity { get; set; } = 255;
    [JsonPropertyName("ball_topmost")] public bool? BallTopmost { get; set; }
}

internal sealed class UiStateDto
{
    [JsonPropertyName("panel")] public PanelDto? Panel { get; set; }
    [JsonPropertyName("card_order")] public List<string>? CardOrder { get; set; }
    [JsonPropertyName("hidden_cards")] public List<string>? HiddenCards { get; set; }
    [JsonPropertyName("card_scope")] public Dictionary<string, string>? CardScope { get; set; }
    [JsonPropertyName("card_multiplier_view")] public Dictionary<string, bool>? CardMultiplierView { get; set; }
    [JsonPropertyName("ball")] public BallDto? Ball { get; set; }
    [JsonPropertyName("active_skin")] public string? ActiveSkin { get; set; }
}

internal sealed class PanelDto
{
    [JsonPropertyName("left")] public double? Left { get; set; }
    [JsonPropertyName("top")] public double? Top { get; set; }
    [JsonPropertyName("width")] public double? Width { get; set; }
    [JsonPropertyName("height")] public double? Height { get; set; }
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1.0;
    [JsonPropertyName("collapsed")] public bool Collapsed { get; set; }
}

internal sealed class BallDto
{
    [JsonPropertyName("left")] public double? Left { get; set; }
    [JsonPropertyName("top")] public double? Top { get; set; }
}
