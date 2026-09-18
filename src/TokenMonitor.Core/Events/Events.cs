namespace TokenMonitor.Core.Events;

/// <summary>配置变更节标志（01-§5：Proxy=1, Settings=2, Pricing=4, UiState=8）。</summary>
[Flags]
public enum ConfigSectionFlags { None = 0, Proxy = 1, Settings = 2, Pricing = 4, UiState = 8 }

/// <summary>配置变更广播（QueryService 缓存失效、托盘 RefreshChecks、各 ViewModel）。</summary>
public sealed record ConfigChanged(ConfigSectionFlags Sections) : IEvent;

/// <summary>跨午夜/时区切换后整桶重建完成（折线图/范围缓存失效、卡片刷新）。</summary>
public sealed record DayRolledOver(string UtcDate, string LocalDate, int OffsetMin) : IEvent;

/// <summary>代理监听状态变化（IProxyEngine.StateChanged 转发；托盘 SetStatus、面板状态条）。</summary>
public sealed record ProxyStateChangedEvent(bool IsListening, string ListenAddr, string? Error) : IEvent;
