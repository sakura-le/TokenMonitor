# Token Monitor 功能模块设计文档（01）

> 版本 v1.0（2026-09-18）。读者：实施代理（按本文零决策开工）、测试代理（按附录 A 写回归）。
> 权威输入（本文不与之冲突，冲突处以本文为准并须在附录 B 登记）：
> `design/context/01-original-features.md`（功能清单）、`02-bug-audit.md`（19 确认 + 7 风险）、`03-architecture-decision.md`（已批准架构）。
> 原始 Go 参考：`D:\GLM Workspace\Projects\Token Calculator\neon-token-monitor\`。
> 本文只定义**模块、类型、接口、数据格式、时序**；一切视觉/交互设计归 UI-Designer。

---

## 0. 文档定位与全局铁律

实施代理在写任何代码前必须接受以下铁律（均为审计 Bug 的根因级结论，出现在多处逻辑中，必须一次定型、处处引用）：

1. **total 同源铁律 [C3]**：`UnifiedUsage.TotalTokens` 在 Parser 归一化处一次性定型——厂商 `total_tokens > 0` 时取厂商值，否则取 `prompt + completion`。此后**所有**"总 Token"（UTC 桶、Local 桶、usage_daily、RecalcDerived、AggregateLocal/Hourly、XLSX 导出、详情面板）一律引用该字段，禁止任何路径再各自推导。
2. **倍率取整铁律 [C11]**：倍率分量逐项 `Math.Round(x * rate, MidpointRounding.AwayFromZero)`；`MulTotal = MulPrompt + MulCompletion`（由分量和推导，不单独对 total 乘倍率取整）。原始 token 用 `TotalTokens`（铁律 1），倍率 token 由 prompt/completion 分量构成（与原版口径一致，仅把截断改为四舍五入并强制加和一致）。
3. **模型键规则 [C19-②]**：模型键 `modelKey = provider + "/" + model`；解析时**只按第一个 `/` 切分**（模型名可含 `/`）。统一用 `ModelKey` 值对象（§2.2），禁止 `string.Split('/')` 直接取 `[0]/[1]`。
4. **归属时刻铁律**：一笔请求的统计归属时间戳 = **响应完成时刻**（SSE：usage 块到达时刻；JSON：响应体读完时刻），Unix 秒。跨午夜归属由此保证。
5. **usage_daily ≡ usage_log 派生铁律 [C1/S2]**：usage_daily（UTC 桶）任何时刻都必须能由 usage_log 全量重算出来（RecalcDerived 幂等）。因此"重置今日"必须同时删 log（§4.5），导入只导 log 不导 daily（§6）。
6. **线程安全铁律 [C5]**：所有可变共享状态（累积器、pricing 运行态、providers 路由表）经锁保护；重建 = "挂起摄入 → flush → 读库 → 原子换桶 → 恢复摄入（排队事件回放）"，由 `UsageCoordinator` 的单一串行锁保证（§2.4.9）。
7. **安全红线**：明文 API 密钥不落盘、不进日志、不进 git；config.json 中密钥一律 `dpapi:<base64>`（手填明文在保存时立即加密；读取兼容明文旧值）。HTTP 面只监听回环地址。

---

## 1. 模块划分与依赖

### 1.1 Core 子模块职责（src/TokenMonitor.Core，net10.0，无 UI 依赖，全部可单测）

**Core.Parser**（命名空间 `TokenMonitor.Core.Parser`）
多厂商 usage 归一化器。输入上游 JSON 响应体或 SSE `data:` 行，输出 `UnifiedUsage`/`UsageEvent`。负责别名映射（DashScope `input_tokens/output_tokens`、DeepSeek `prompt_cache_hit_tokens/prompt_cache_miss_tokens`、OpenAI 系 `prompt_tokens_details.cached_tokens`、`input_tokens_details.cached_tokens`）、reasoning 别名（`completion_tokens_details.reasoning_tokens` 与顶层 `reasoning_tokens`）、恒等式强制 `prompt == hit + miss`、total 铁律落地。纯函数、无 IO、无状态，是最底层可测组件。

**Core.Pricing**（`TokenMonitor.Core.Pricing`）
倍率与计价引擎。持有 pricing.json 反序列化后的版本链（multipliers/pricing 两棵字典），按"生效日期倒序选择最新生效版本"提供任意时刻的倍率与成本报价；内置 UTC↔Local 时段/规则平移（跨午夜拆分、星期平移、相邻同值片段合并）并按 (模型版本， offset) 缓存平移表 [S3]。所有时间匹配用**分数小时** [C4]。版本 upsert/去重/旧格式迁移（平铺 `{periods}`、per-1K→per-1M）在此实现。负责 pricing.json 的读写。

**Core.Stats**（`TokenMonitor.Core.Stats`）
双口径线程安全累积器与摄入协调。`Accumulator` 同时维护 UTC 桶与 Local(UTC+offset) 桶（各含原始 token、倍率 token、冻结成本、DeltaTokens）；`UsageCoordinator` 实现 `IUsageIngestor`，是代理捕获结果的唯一入口：串行锁内完成"报价→累加→缓冲落库→文件日志"，并负责"整桶重建"（时区切换/跨午夜/补录/导入后）。`StatsTicker` 按 200ms/1s 两频构建不可变 `StatsSnapshot` 经事件总线发布。

**Core.Storage**（`TokenMonitor.Core.Storage`）
SQLite（Microsoft.Data.Sqlite）持久化 + 派生数据重算 + 文件级输出。WAL、批量刷写（3 条或 5 秒）、失败整批回队 [C12]、RecalcDerived UPSERT 重算 [C1]、漏抓表、操作日志、VACUUM INTO 备份与回滚 [C6]、Local/Hourly 现算聚合。同时承载每日 usage_logs 文件（.log/.csv）与 XLSX 导出（MiniExcel）。通过 `RateFunc/CostFunc` 委托接入计价（不直接依赖 Core.Pricing，便于测试）。

**Core.Config**（`TokenMonitor.Core.Config`）
JSON 配置读写与校验修复。管理 config.json / settings.json / ui_state.json 三个文件（pricing.json 归 Core.Pricing）：缺省合并、损坏恢复、字段级校验 [S6]、API 密钥 DPAPI 加解密（`dpapi:` 前缀）、防抖保存。是"配置如何合法存在"的唯一裁判。

**Core.SysUtil**（`TokenMonitor.Core.SysUtil`）
Windows 系统能力与横切设施：单实例 Mutex + 显示面板信号、HKCU Run 开机自启、时区偏移表与标签、DPAPI 保护器（ProtectedData）、文件夹/文件打开、静态 `Logger`（分级、UTF-8、5MB 轮转，§8）。静态类 + 接口门面 `ISysUtil` 双形态，便于测试替身。

**Core.Events**（`TokenMonitor.Core.Events`）
进程内事件总线 `IEventBus`：类型化发布/订阅、三种分发模式（发布线程/UI 线程/线程池）、快照类事件"仅保留最新"背压（§5）。无业务依赖。

**Core.Proxy**（`TokenMonitor.Core.Proxy`）
本地 OpenAI 兼容反向代理引擎。HttpListener 监听回环；请求整备（按 provider 的 `strip_params` 剥离 [C15]、`max_tokens` 钳制、仅流式注入 `stream_options.include_usage` [C2]）、未知模型拒绝 [C14]、SSE 逐行边转发边解析且客户端断开后继续读完上游（30 分钟上限）、JSON 缓冲解析回放、漏抓判定与登记、`/v1/models` 合成、`/health`。单请求异常全捕获，不拖垮进程。捕获结果经 `IUsageIngestor` 交给 Core.Stats，自身不碰存储。

**Core 组合根**（`TokenMonitor.Core` 根命名空间，文件如 `MonitorEngine.cs`）
`MonitorEngine : ITokenMonitorEngine`——装配上述全部组件的唯一组合根，实现启动/退出序列（§7）、跨午夜守望、配置变更联动（重算→重建→广播）。App 只看见该门面与 §2 各接口。

### 1.2 App 子模块职责（src/TokenMonitor.App，net10.0-windows）

- **App.Windows**：`MainWindow`（无边框置顶面板）与 `FloatingBall`（透明分层悬浮球）的窗口壳与生命周期；PerMonitorV2 DPI；信息架构按 01 文档 §3-D/E/F，视觉按 UI-Designer 定稿。
- **App.Controls**：模型卡、环形图、折线图、比例条、流量动效等矢量自绘控件；只消费 `StatsSnapshot`/`ModelDailyAggregate` 等不可变数据。
- **App.Dialogs**：7 个配置对话框（倍率/计价/手动补录/操作日志/导出/时间范围/显示隐藏卡片）的窗口壳与表单逻辑；视觉归 UI-Designer。
- **App.Tray**：`TrayController : ITrayController`——Hardcodet NotifyIcon 托盘全项菜单（01 文档 §3-E1 清单），托盘动作回调转发给 ViewModels/Engine。
- **App.Services**：状态持久化编排（ui_state.json 经 `IConfigService`）、旧数据导入向导（调 `IImportService`）、窗口位置记忆、`EventWaitHandle` 面板唤起。
- **App.ViewModels**：CommunityToolkit.Mvvm；订阅 `PanelStatsTick`/`BallStatsTick`/`ConfigChanged`/`DayRolledOver` 等事件，向 Engine 下发命令；异步回包时校验卡片键（防 [C16] 类错配）。

### 1.3 依赖方向图

```
App.(Windows|Controls|Dialogs|Tray|Services|ViewModels)
      │  仅引用 TokenMonitor.Core 的接口与值对象（单向）
      ▼
TokenMonitor.Core (MonitorEngine 组合根) ──► 下列全部子模块
      │
      ├─ Core.Proxy ──────► Core.Parser, Core.Stats(IUsageIngestor), Core.Config(类型), Core.SysUtil
      ├─ Core.Stats ──────► Core.Parser, Core.Pricing, Core.Events, Core.SysUtil
      ├─ Core.Storage ────► Core.SysUtil（计价经 RateFunc/CostFunc 委托注入，不依赖 Pricing）
      ├─ Core.Config ─────► Core.SysUtil(DPAPI/Logger)
      ├─ Core.Parser / Core.Pricing / Core.Events / Core.SysUtil ──►（无内部依赖）
```
规则：App→Core 单向；Core 内部**禁止**反向引用；Core.* 任何模块不得引用 WPF/Hardcodet/CommunityToolkit。

### 1.4 线程模型归属

| 线程/上下文 | 归属工作 |
|---|---|
| WPF UI 线程 | 窗口/控件/托盘菜单/ViewModel；只消费不可变快照；耗时操作全部丢线程池 |
| HTTP 监听线程池 | HttpListener 回调：请求整备、转发、流式捕获（async/await；客户端断开后上游续读为独立 Task，30 分钟上限） |
| 摄入串行锁 | UsageCoordinator.Ingest / RebuildToday（§2.4.9，唯一写状态入口） |
| 定时器（线程池） | StatsTicker 200ms/1s；DayWatch 30s；Store 5s flush；缓存 TTL |
| 后台任务 | RecalcDerived、XLSX 导出、导入、备份（均为独占式长任务，互相以锁/状态机串行） |

---

## 2. 公共类型与接口契约（C# 签名级）

约定：JSON 序列化用 System.Text.Json，字段名以 `[JsonPropertyName]` 显式标注为原版 snake_case（§3）；金额/单价/倍率一律 `double`；token 数与计数一律 `long`；时间戳一律 Unix 秒 `long`（字段名以 `Unix` 结尾）。每个接口标注线程要求；未标注"非线程安全"即为"线程安全（内部同步）"。

### 2.2 值对象

```csharp
namespace TokenMonitor.Core.Parser;

/// <summary>归一化用量。全链路唯一用量载体；字段语义见铁律 1/2。</summary>
public readonly record struct UnifiedUsage(
    long PromptTokens,      // 输入总量，恒等式 == CacheHit + CacheMiss
    long CacheHitTokens,    // 缓存命中输入
    long CacheMissTokens,   // 缓存未命中输入（缺别名时按 prompt-hit 推导）
    long CompletionTokens,  // 输出（reasoning 是其子集，不叠加）
    long ReasoningTokens,   // 思维链 token
    long TotalTokens);      // [C3] 厂商 total>0 ? 厂商值 : prompt+completion

public enum CaptureSource { Sse, Json, Estimated }

/// <summary>一笔已归一化的用量事件。CompletedAtUnix = 响应完成时刻（铁律 4）。</summary>
public sealed record UsageEvent(
    string Provider, string Model, UnifiedUsage Usage,
    long CompletedAtUnix, CaptureSource Source);

/// <summary>解析结果（不抛异常，漏抓原因由此带回）。</summary>
public sealed record ParseResult(UnifiedUsage? Usage, string? FailureReason);
```

```csharp
namespace TokenMonitor.Core;   // 模型键（铁律 3）

public readonly record struct ModelKey(string Provider, string Model)
{
    public static ModelKey Parse(string key);      // 只按第一个 '/' 切分；无 '/' → Provider="", Model=key
    public override string ToString();             // provider + "/" + model
    public static implicit operator string(ModelKey k) => k.ToString();
}
```

```csharp
namespace TokenMonitor.Core.Stats;

/// <summary>单个模型的实时快照条目（不可变；UTC/Local 两桶结构相同）。</summary>
public sealed record ModelSnapshot(
    string Provider, string Model,
    long RequestCount,
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,      // 原始值（TotalTokens 遵铁律 1）
    long MulTotal, long MulPrompt, long MulCacheHit,                    // 倍率值（遵铁律 2）
    long MulCacheMiss, long MulCompletion, long MulReasoning,
    double CostCNY, double CostUSD,        // 冻结成本（该口径）
    long DeltaTokens,                      // 最近一笔请求的 TotalTokens（卡片"+N"；重建/重置后为 0）
    long LastActiveUnix,                   // 最近一笔请求完成时刻
    long MissedCount);                     // 当日（本地窗口）漏抓条数（>0 红框提示）

/// <summary>面板/悬浮球消费的完整不可变快照。</summary>
public sealed record StatsSnapshot(
    IReadOnlyList<ModelSnapshot> UtcModels,   // UTC 口径模型列表（含 0 值骨架条目）
    ModelSnapshot UtcSummary,                 // UTC 合计（Provider="合计", Model="ALL"）
    IReadOnlyList<ModelSnapshot> LocalModels, // Local(UTC+offset) 口径
    ModelSnapshot LocalSummary,
    long MissedCaptures,                      // 全局漏抓合计：遍历 MissedByModel 全部键求和 [S4]
    IReadOnlyDictionary<string, long> MissedByModel, // key=provider/model（本地日窗口）
    int OffsetMin,                            // 当前配置偏移（分钟）
    long CreatedAtUnix);

/// <summary>AddUsage 的返回：本次请求对 UTC 桶的增量（即写入 usage_daily/upsert 的倍率与成本）。</summary>
public sealed record AccumulateResult(
    long MulTotal, long MulPrompt, long MulCacheHit, long MulCacheMiss, long MulCompletion, long MulReasoning,
    double CostCNY, double CostUSD);

/// <summary>Local 桶重建时的原始行（来自 usage_log）。</summary>
public sealed record LocalReplayRow(long Ts, string Provider, string Model, UnifiedUsage Usage);

/// <summary>近 N 天/范围查询结果（逐日明细；不含汇总，由调用方累加）。</summary>
public sealed record RecentDaysResult(IReadOnlyList<ModelDailyAggregate> Rows, int RequestedDays);
```

```csharp
namespace TokenMonitor.Core.Storage;

/// <summary>usage_daily/聚合结果共用的日聚合行（列语义与 §4 DDL 一一对应）。</summary>
public sealed record ModelDailyAggregate(
    string Date, string Provider, string Model,          // Date=YYYY-MM-DD（该行所属口径日历）
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,
    long RequestCount,
    long MulTotal, long MulPrompt, long MulCacheHit, long MulCacheMiss, long MulCompletion, long MulReasoning,
    double CostCNY, double CostUSD);

/// <summary>本地口径小时分段聚合（对齐官方 usage/cost 导出模板）。</summary>
public sealed record HourlyAggregate(
    string Date, int Hour, string Provider, string Model,  // 本地日历
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,
    long RequestCount, double CostCNY, double CostUSD);

/// <summary>usage_log 原始行（写入与明细读取共用；Mul*/Cost* 仅写路径使用，usage_log 表不存这些列）。</summary>
public sealed record UsageLogRow(
    long Ts, string Provider, string Model,
    long PromptTokens, long CacheHitTokens, long CacheMissTokens,
    long CompletionTokens, long ReasoningTokens, long TotalTokens,
    long MulTotal = 0, long MulPrompt = 0, long MulCacheHit = 0,
    long MulCacheMiss = 0, long MulCompletion = 0, long MulReasoning = 0,
    double CostCNY = 0, double CostUSD = 0,
    bool Estimated = false, long? ImportBatch = null);

public sealed record MissedLogRow(long Id, long Ts, string Provider, string Model, int Status, string Reason);
public sealed record OpLogRow(long Id, long Ts, string Model, string Action, string Detail, string EffectiveFrom);

/// <summary>计价回调（offsetMin=0 → UTC 口径；>0 → Local 口径）。hour 为分数小时 [C4]。</summary>
public delegate double RateFunc(string modelKey, long ts, double hour, int offsetMin);
public delegate (double Cny, double Usd) CostFunc(string modelKey, long ts, double hour, int offsetMin,
                                                 long cacheHit, long cacheMiss, long completion);
```

```csharp
namespace TokenMonitor.Core.Pricing;

/// <summary>倍率时段（小时，0.5 步进；匹配区间 [Start, End)）。</summary>
public sealed record MultiplierPeriod(double Start, double End, double Rate);
/// <summary>计价规则（单价 per 1M token；Days=1..7 周一..周日，空/缺省=每天）。</summary>
public sealed record PriceRule(double Start, double End, IReadOnlyList<int>? Days,
                               double InputPer1M, double CachePer1M, double OutputPer1M);
public sealed record MultiplierVersion(string? EffectiveFrom, IReadOnlyList<MultiplierPeriod> Periods); // null/"": 一直生效
public sealed record PriceVersion(string? EffectiveFrom, string Currency, IReadOnlyList<PriceRule> Rules);

/// <summary>单模型倍率版本链（history 末尾=最近保存）。</summary>
public sealed record MultiplierConfig(IReadOnlyList<MultiplierVersion> History);
/// <summary>单模型计价版本链。</summary>
public sealed record PriceConfig(IReadOnlyList<PriceVersion> History);

/// <summary>pricing.json 文档（键=模型名，不带 provider 前缀，与原版一致）。</summary>
public sealed record PricingDocument(
    IReadOnlyDictionary<string, MultiplierConfig> Multipliers,
    IReadOnlyDictionary<string, PriceConfig> Pricing);

public readonly record struct MultiplierQuote(double Rate, bool Matched);   // Matched=false → Rate=1.0
public readonly record struct CostQuote(double CostCNY, double CostUSD, bool Priced); // Priced=false → "不计价/Coding Plan"
```

```csharp
namespace TokenMonitor.Core.Config;

/// <summary>config.json 根（文件名 listen_addr/providers/... 见 §3.1）。</summary>
public sealed record ProxyConfig(
    string ListenAddr,                       // 默认 "127.0.0.1:8280"；只允许回环
    string? DefaultProvider,                 // [C14] 可选兜底 provider 名；null=未知模型 404
    IReadOnlyList<ProviderConfig> Providers);

/// <summary>单个提供商。</summary>
public sealed record ProviderConfig(
    string Name,
    string BaseUrl,                          // 绝对 http(s) URL
    string ApiKey,                           // 内存中为明文；落盘一律 "dpapi:<b64>"（§3.1）
    IReadOnlyList<string> ModelPrefix,       // 模型前缀（大小写不敏感前缀匹配）
    int? MaxTokens,                          // null/缺省=不钳制
    IReadOnlyList<string> StripParams);      // [C15] 请求体剥离参数；缺省=["enable_thinking","reasoning_effort"]

/// <summary>settings.json。</summary>
public sealed record AppSettings(
    int OffsetMin,                           // 默认 0（UTC±0）；[-720,840] 30 分钟步进
    string EffectiveDateMode,                // "utc"|"local"，默认 "utc"
    int BallOpacity,                         // 0-255；<=0 视为 255（与原版 loadSettings 一致）
    bool BallTopmost);                       // 默认 true

/// <summary>ui_state.json（新文件；吸收原 multiplier_states.json 与前端 localStorage）。</summary>
public sealed record UiState(
    PanelWindowState Panel,
    IReadOnlyList<string> CardOrder,                        // 卡片顺序（modelKey）
    IReadOnlySet<string> HiddenCards,
    IReadOnlyDictionary<string, string> CardScope,          // modelKey → "utc"|"local"
    IReadOnlyDictionary<string, bool> CardMultiplierView,   // modelKey → true=显示倍率值（原 multiplier_states.json）
    BallState Ball);

public sealed record PanelWindowState(double? Left, double? Top, double? Width, double? Height,
                                      double Opacity, bool Collapsed);   // null=首启由 UI 默认决定
public sealed record BallState(double? Left, double? Top);
```

```csharp
namespace TokenMonitor.Core.Proxy;

/// <summary>漏抓登记（usage_missed 行）。仅"上游可能已计费"的请求进入（status==0 未知 或 2xx）。</summary>
public sealed record MissedCapture(long Ts, string Provider, string Model, int Status, string Reason);

public sealed record ProxyStateChangedEventArgs(bool IsListening, string ListenAddr, string? Error);
```

```csharp
namespace TokenMonitor.Core.Storage;   // 校准/导入辅助

/// <summary>data/calibrate_state.json（沿用原文件名与字段）。</summary>
public sealed record CalibrateState(bool HasCalibrated, string LastBackup);

public enum ImportMode { MergeIfNew, ReplaceAll }   // §6.5

public sealed record ImportOptions(string LegacyDataDir, bool MigrateApiKeys, ImportMode Mode);

public sealed record ImportPreview(
    bool LegacyDbFound, long EstimatedUsageRows, long EstimatedMissedRows, long EstimatedOpRows,
    bool ConfigFound, bool PricingFound, bool SettingsFound, bool MultiplierStatesFound,
    bool AlreadyImported, string? SourceSha256, IReadOnlyList<string> Warnings);

public sealed record ImportResult(
    bool Success, string? Error,
    long ImportedUsageRows, long ImportedMissedRows, long ImportedOpRows,
    bool ConfigImported, bool PricingImported, bool SettingsImported,
    string BackupPath);

public sealed record ExportRequest(string StartDate, string EndDate, BucketScope RangeScope,
                                   string? ModelKey, bool IncludeHourly);
public sealed record ExportResult(bool Success, string? Error, string FilePath, int UtcRows, int LocalRows, int HourlyRows);
```

```csharp
namespace TokenMonitor.Core.Pricing;   // 查询口径枚举（放 Pricing 供 Stats/Storage/Export 共用）

public enum BucketScope { Utc, Local }
```

### 2.3 接口契约

#### 2.3.1 IUsageParser（Core.Parser；纯函数，任意线程）

```csharp
public interface IUsageParser
{
    /// <summary>解析非流式 JSON 响应体。返回 Usage=null 表示无 usage 字段；
    /// JSON 结构非法时 FailureReason 带原因（供漏抓 reason）。永不抛异常（内部全捕获）。</summary>
    ParseResult ParseJsonResponse(string responseBody, string provider);

    /// <summary>解析单行 SSE data 载荷（不含 "data: " 前缀，调用方剥离）。该行不含 "usage" 或不可解析 → Usage=null。</summary>
    ParseResult ParseSseDataLine(string dataLine, string provider);

    /// <summary>请求体是否为可捕获的 JSON（Content-Type 含 application/json 且能解析出顶层对象）。
    /// 决定走"整备+捕获"还是"纯透传"路径 [C2]。</summary>
    bool IsJsonRequestBody(string contentType, ReadOnlySpan<byte> bodyPrefix);
}
```
语义：归一化规则 = 原 `parser.go normalizeUsage` + 顶层 `reasoning_tokens` 别名 + total 铁律（§0.1）。恒等式强制：`CacheMiss = Prompt - CacheHit`（别名缺失或冲突时一律以此收口）。

#### 2.3.2 IPricingEngine（Core.Pricing；读线程安全；变更方法内部串行）

```csharp
public interface IPricingEngine
{
    /// <summary>指定时刻倍率（scope=Utc → UTC 分数小时匹配 UTC 时段；Local → 平移后时段）。
    /// 无版本/无匹配时段 → Rate=1.0。完成时刻内部换算分数小时 [C4]，调用方只给 Unix 秒。</summary>
    MultiplierQuote GetMultiplier(string modelKey, long completedAtUnix, BucketScope scope);

    /// <summary>指定时刻成本。命中第一条规则（星期+时段匹配）后按 1e6 折算冻结：
    /// cost = hit/1e6*CachePer1M + miss/1e6*InputPer1M + completion/1e6*OutputPer1M；
    /// Currency=="CNY" → CNY 桶，否则 → USD 桶。无版本/无规则 → Priced=false。</summary>
    CostQuote GetCost(string modelKey, long completedAtUnix,
                      long cacheHit, long cacheMiss, long completion, BucketScope scope);

    /// <summary>当前文档不可变快照（对话框绑定用）。EffectiveFrom/Periods/Rules 均为只读视图。</summary>
    PricingDocument Document { get; }

    (string Mode, int OffsetMin) EffectiveContext { get; }   // ("utc"|"local", 偏移分钟)

    /// <summary>生效日期口径与偏移（来自 settings.json）。变更会使平移缓存全部失效 [S3]。</summary>
    void SetEffectiveContext(string mode, int offsetMin);

    /// <summary>追加/覆盖倍率版本：同 EffectiveFrom 的旧版本被移除并把新版本插到链尾（"最近保存优先"）。
    /// periods=null → [{0,24,1.0}]。立即写 pricing.json（合并去重后）并触发 Changed。</summary>
    void UpdateMultiplierVersion(string modelKey, string? effectiveFrom, IReadOnlyList<MultiplierPeriod>? periods);

    /// <summary>同上，计价版本。currency 仅接受 "CNY"/"USD"，其它值 ArgumentException。</summary>
    void UpdatePricingVersion(string modelKey, string? effectiveFrom, string currency, IReadOnlyList<PriceRule> rules);

    /// <summary>导入/全量替换文档（先经 Normalize：旧平铺格式→history、per-1K→×1000、同生效日期去重保留末次）。</summary>
    void ReplaceDocument(PricingDocument doc);

    /// <summary>文档变更通知（Engine 转发为 ConfigChanged(Pricing)）。</summary>
    event EventHandler? Changed;
}
```
版本选择语义（与原 `multiplierVersionFor/pricingVersionFor` 一致）：把 `EffectiveFrom==null` 归一为 `"0000-00-00"`，自链尾向前找第一个 `effDate <= 目标日期` 的版本；目标日期按 `EffectiveContext`（utc→UTC 日历 / local→UTC+offset 日历）从 ts 推导。时段/星期匹配：UTC 口径星期按 UTC 日、Local 口径按 UTC+offset 日；Local 平移算法 = 原 `shiftPeriodsToLocal`/`shiftPriceRulesToLocal`（含 `dayOffset = floor((start+shift)/24)` 的星期平移与同值合并，参考 `tzShift.js` 的全表合并变体）。平移表缓存键 `(模型键, 版本引用, offsetMin)`，`ReplaceDocument/Update*/SetEffectiveContext` 时清空 [S3]。

#### 2.3.3 IAccumulator（Core.Stats；全部成员线程安全）

```csharp
public interface IAccumulator
{
    /// <summary>累加一笔请求到 UTC 与 Local 两桶（内部按当前 EffectiveContext 报价并冻结）。
    /// 返回 UTC 口径增量供持久化。永不抛异常（内部异常→记 Warn、事件丢弃）。</summary>
    AccumulateResult AddUsage(UsageEvent evt);

    /// <summary>整桶替换 UTC 桶（"设置即完整替换"语义 [S1]）：先清全部 UTC 侧值再逐行置入；
    /// 行的 Mul*/Cost* 直接取自 usage_daily 列（不再乘算）。空列表 = 清空。DeltaTokens/LastActive 归零。</summary>
    void ReplaceUtcBucket(IReadOnlyList<ModelDailyAggregate> todayUtcRows);

    /// <summary>整桶替换 Local 桶：逐行按本地分数小时重新取倍率/成本冻结（与 AddUsage 的 Local 路径同口径）。</summary>
    void ReplaceLocalBucket(IReadOnlyList<LocalReplayRow> todayLocalRows);

    /// <summary>补 0 值骨架条目（历史出现过的模型，保证卡片渲染；已存在则跳过）。</summary>
    void SeedModelSkeleton(string provider, string model);

    bool HasModel(string provider, string model);

    /// <summary>构建不可变快照。missedByModel 由调用方注入（本地日窗口的漏抓计数）；
    /// 每模型 MissedCount 与全局 MissedCaptures（= 字典全部值求和 [S4]）由此填充。O(模型数)，200ms 调用无压力。</summary>
    StatsSnapshot Snapshot(IReadOnlyDictionary<string, long> missedByModel);

    void ResetUtcBucket();     // 保留模型条目，UTC 侧清零（原 ResetKeepModels）
    void ResetLocalBucket();   // 保留条目，Local 侧清零（原 ResetKeepModelsLocal）
    void ResetAllBuckets();    // 删除全部条目（原 Reset）
    void ResetModel(string modelKey); // 单模型两桶清零，条目保留（原 ResetModel）
}
```

#### 2.3.4 IStore（Core.Storage；全部成员线程安全；内部单连接 + 单锁串行）

```csharp
public interface IStore : IDisposable
{
    // —— 写路径 ——
    /// <summary>加入批量缓冲（攒 3 条或 5 秒落盘，WAL）。落盘失败→整批回队头部重试，本方法不抛。</summary>
    void AddUsage(IReadOnlyList<UsageLogRow> rows);
    /// <summary>同步刷出缓冲（退出/备份/重算前必须调用）。</summary>
    void Flush();
    /// <summary>同步写一条漏抓（provider/model 为空则忽略）。不抛。</summary>
    void AddMissed(MissedLogRow m);
    /// <summary>同步写一条操作日志，随后裁剪保留最近 5000 条（读取恒 500，§4.4）。不抛。</summary>
    void LogOp(string model, string action, string detail, string effectiveFrom);
    /// <summary>事务直写补录行（estimated=1；total 缺失按 prompt+completion 推导）。失败抛 StorageException 并整批回滚。</summary>
    void InsertEstimatedUsage(IReadOnlyList<UsageLogRow> rows);

    // —— 查询（失败抛 StorageException）——
    IReadOnlyList<ModelDailyAggregate> GetTodayUtc();                          // usage_daily WHERE date=UTC today
    IReadOnlyList<ModelDailyAggregate> QueryDailyRange(string startUtc, string endUtc); // 闭区间 [start,end]
    IReadOnlyList<ModelDailyAggregate> GetRecentDaysUtc(int days);             // [C7] -(days-1)，恰 days 行内数据
    IReadOnlyList<UsageLogRow> GetLogsByRange(long startTs, long endExclusiveTs); // [startTs, endExclusiveTs)，不含 Mul/Cost 列
    IReadOnlyList<ModelDailyAggregate> AggregateLocal(long startTs, long endExclusiveTs, int offsetMin,
                                                      RateFunc? rateFn, CostFunc? costFn);   // Local 现算（total 遵铁律 1+S2）
    IReadOnlyList<HourlyAggregate> AggregateHourly(long startTs, long endExclusiveTs, int offsetMin,
                                                   RateFunc? rateFn, CostFunc? costFn);
    IReadOnlyList<MissedLogRow> GetMissed(long startTs, long endExclusiveTs);
    IReadOnlyDictionary<string, long> GetMissedCountByModel(long startTs, long endExclusiveTs); // key=provider/model
    IReadOnlyList<OpLogRow> GetOpLogs(string? modelKey);                       // null/"all"=全部；恒 ts DESC LIMIT 500
    IReadOnlyList<(string Provider, string Model)> GetAllModels();             // DISTINCT provider,model FROM usage_daily

    // —— 维护/重算 ——
    /// <summary>全量重算 usage_daily（§4.5 语义）：UPSERT 写回 [C1] + 清孤儿行；幂等。耗时操作，调用方放后台线程。</summary>
    void RecalcDerived(RateFunc? rateFn, CostFunc? costFn);
    /// <summary>重置今日：删 usage_daily[UTC 今日] + usage_log[ts∈(UTC今日∪本地今日)] + usage_missed[同双窗口]（±model）[C8]。见 §4.5。</summary>
    void ResetToday(string? modelKey, int offsetMin);
    /// <summary>删除模型全部数据：usage_log + usage_daily + usage_missed 三表按 provider/model [C8]。</summary>
    void DeleteModelData(string modelKey);
    /// <summary>删除全部数据：三表全清 [C8]。</summary>
    void DeleteAllData();
    /// <summary>VACUUM INTO → data/backups/token_monitor_backup_<yyyyMMdd_HHmmss_fff>.db（重名追加序号）；先 Flush；返回路径。</summary>
    string Backup();
    /// <summary>回滚：Flush→关连接→删 -wal/-shm→覆盖主库→同参重开→initTables→清缓冲 [C6]。</summary>
    void ReplaceDatabase(string backupPath);
    /// <summary>PRAGMA wal_checkpoint(TRUNCATE)（退出前）。</summary>
    void CheckpointWal();
}
```

#### 2.3.5 IConfigService（Core.Config；线程安全）

```csharp
public interface IConfigService
{
    string DataDir { get; }                 // exe 旁 data/
    ProxyConfig Proxy { get; }              // 已校验合并的生效配置（ApiKey 内存态为明文）
    AppSettings Settings { get; }
    UiState Ui { get; }

    /// <summary>保存 config.json：明文 ApiKey → DPAPI 加密（"dpapi:<b64>"）；已是 dpapi: 的保持不变。失败抛 ConfigException，文件不动。</summary>
    void SaveProxy(ProxyConfig cfg);
    void SaveSettings(AppSettings s);       // 原子写（临时文件+替换）
    /// <summary>防抖保存 ui_state.json（300ms 合并；进程退出时强制落盘）。</summary>
    void SaveUiState(UiState u);
    /// <summary>重读 config.json → ValidateAndRepair → 更新 Proxy；不抛（损坏时修复并告警日志）。</summary>
    void ReloadProxy();
    ProxyConfig ValidateAndRepair(ProxyConfig? raw);   // [S6]，§3.1 校验表
    string EncryptApiKey(string plain);     // ProtectedData CurrentUser → "dpapi:<b64>"
    /// <summary>"dpapi:x" → 明文；无前缀原样返回（兼容旧明文配置，读取时告警一次）。</summary>
    string DecryptApiKey(string stored);

    /// <summary>内部文件保存完成事件（Engine 转发 ConfigChanged）。</summary>
    event EventHandler<ConfigSection>? Saved;
}
public enum ConfigSection { Proxy, Settings, UiState, Pricing }
```

#### 2.3.6 IEventBus（Core.Events；Publish/Subscribe 均线程安全；Publish 永不阻塞、永不抛）

```csharp
namespace TokenMonitor.Core.Events;

public interface IEvent { }   // 标记接口

public enum EventDispatch { PublisherThread, UiThread, BackgroundPool }

public interface IEventBus
{
    /// <summary>订阅。UiThread 模式捕获订阅时的 SynchronizationContext（必须已在 UI 线程订阅），否则落到进程主 Dispatcher。
    /// 返回 IDisposable；Dispose=退订（可重入、幂等）。Handler 内异常被隔离并记日志，不影响其它订阅者。</summary>
    IDisposable Subscribe<TEvent>(Action<TEvent> handler, EventDispatch dispatch = EventDispatch.BackgroundPool)
        where TEvent : IEvent;

    /// <summary>FIFO 发布（一次性事件用）。每主题有界队列（32），溢出丢最旧并 Warn。</summary>
    void Publish<TEvent>(TEvent evt) where TEvent : IEvent;

    /// <summary>合并式发布（快照类用）：若该主题尚有未分发的旧载荷则原地替换，UI 永远只处理最新（背压=仅保留最新）。</summary>
    void PublishCoalesced<TEvent>(TEvent evt) where TEvent : IEvent;
}
```
事件清单与载荷见 §5。

#### 2.3.7 IProxyEngine 与捕获入口（Core.Proxy）

```csharp
public interface IProxyEngine : IDisposable
{
    bool IsListening { get; }
    string ListenAddr { get; }
    int InFlightCaptures { get; }            // 在途捕获计数（诊断/退出等待）
    /// <summary>绑定前 TcpListener 预检端口占用 [C19-④]，再 HttpListener 启动（前缀 127.0.0.1 与 localhost 双注册）。
    /// 失败抛 ProxyStartException（含原因），并已发出 StateChanged(IsListening=false, error)。</summary>
    void Start(string listenAddr);
    /// <summary>停止接受新连接→等待在途捕获至多 gracefulTimeout→强制中止 [S5]。可重复调用（幂等）。</summary>
    void Stop(TimeSpan gracefulTimeout);
    event EventHandler<ProxyStateChangedEventArgs>? StateChanged;
}

/// <summary>捕获结果唯一出口（UsageCoordinator 实现）。代理线程调用；实现必须线程安全且永不抛。</summary>
public interface IUsageIngestor
{
    void Ingest(UsageEvent evt);
    void ReportMissed(MissedCapture capture);
}
```
代理行为细则（实施代理照做，逐条对应原 proxy.go）：
- 路由：`model` 前缀（大小写不敏感）匹配 provider；无匹配 → `DefaultProvider` 兜底，仍无 → **404** JSON 错误体 [C14]。body 非法/无 model → 400。
- 整备：仅当请求 `"stream"==true` 时注入 `stream_options:{"include_usage":true}`；**不改写 stream 本身** [C2]。`max_tokens` 超 provider 上限则钳制。按 provider `StripParams` 删除字段 [C15]。
- 透传：Content-Type 非 JSON 的请求体不解析、不整备、不捕获、不计漏抓（/v1/audio/* multipart、GET /v1/files 等）[C2]。
- 转发：剥离 hop-by-hop 头（Connection/Keep-Alive/Transfer-Encoding/Upgrade/Proxy-*），`Authorization: Bearer <provider密钥>` 重写，`Content-Type: application/json`、`Host=目标`，路径 = base_url.Path + (入站路径剥 "/v1")，Query 原样。
- 上游 HttpClient（SocketsHttpHandler）：ConnectTimeout 30s、ResponseHeadersRead、KeepAlivePing 30s、MaxConnectionsPerServer 100、自动解压、跟随系统代理（对齐原 transport 调优）。
- SSE：Content-Type 含 `text/event-stream` → 逐行转发+解析（"data: " 前缀；[DONE] 跳过；行缓冲上限 8MB）；客户端断开→停止转发但**继续读完上游**（请求 CancellationToken 与客户端解除关联，链接 30 分钟超时 CTS）；取**最后一个**含 usage 的块为结果。JSON：整包缓冲（容忍部分读）→解析→原样回放。
- 漏抓判定：解析失败/无 usage 块时——`status==0 或 2xx` → `ReportMissed`（reason：429→"限流(429)"、2xx→"响应无 usage 块"、上游中断→"上游连接中断: {err}"、读中断→具体错误文本；status=0 表示无法判定保守计入）；上游错误区分"未送达上游"（DNS 失败/拨号失败/连接拒绝/网络不可达 → 不计）与"送达后中断"（计）[C2/A6]。4xx/5xx 已知未计费 → 只记运行日志不入表。
- `/v1/models`：由各 provider 的 ModelPrefix 合成 `{"object":"list","data":[{"id":prefix,"object":"model","owned_by":name}]}`；`/health` → `{"status":"ok"}`；其余路径 404（与原版"任意路径都代理"不同：收窄攻击面，附录 B-14）。
- 每请求外层 try/catch→500/502 + 日志，绝不让进程崩溃 [A8]。

#### 2.3.8 IStatsQueryService / IExportService / ICalibrationService / IImportService / ISysUtil

```csharp
namespace TokenMonitor.Core.Stats;

/// <summary>读侧门面：按需查询 + 结果缓存 [S7]（缓存键含参数与 scope；ConfigChanged/DayRolledOver/ImportCompleted 失效）。</summary>
public interface IStatsQueryService
{
    /// <summary>近 N 天逐日聚合。两口径均取恰好 days 个日历日 [C7]；Local 用 offset 换算日界。</summary>
    RecentDaysResult GetRecentDays(int days, BucketScope scope);
    /// <summary>日期范围逐日聚合。[C9]：startDate/endDate 按 scope 口径解释——Local 时换算为 ts 窗口（本地 00:00-offset）再查；
    /// Utc 直接按 UTC 日界。随 scope 返回的 Date 字段即该口径日历。</summary>
    IReadOnlyList<ModelDailyAggregate> GetRange(string startDate, string endDate, BucketScope scope);
    /// <summary>本地日历某日的小时分段（offset 当前值）。</summary>
    IReadOnlyList<HourlyAggregate> GetHourly(string localDate);
    IReadOnlyList<OpLogRow> GetOpLogs(string? modelKey);
    IReadOnlyList<(string Provider, string Model)> GetAllModels();
}
```

```csharp
namespace TokenMonitor.Core.Storage;

/// <summary>XLSX 导出（MiniExcel）。同步 IO，调用方必须放后台线程。</summary>
public interface IExportService
{
    /// <summary>RangeScope 决定 StartDate/EndDate 的日历解释（[C9] 规则同 IStatsQueryService.GetRange）。
    /// Sheet 固定顺序：UTC总表 / UTC明细表 / LOCAL总表 / LOCAL明细表 [+ HOUR用量 / HOUR消费]；
    /// UTC 侧取 usage_daily（窗口内 UTC 日期），LOCAL 侧从 usage_log 现算，HOUR 表=本地"今日"（仅 IncludeHourly）。
    /// 文件：data/export/token_usage_{modelTag|all}_{start}_{end}_{yyyyMMdd_HHmmss}.xlsx；表头与列序照原版 storage.go。</summary>
    ExportResult ExportXlsx(ExportRequest request);
}

/// <summary>手动补录（校准）与回滚。方法级串行（内部锁）；重入抛 InvalidOperationException。</summary>
public interface ICalibrationService
{
    CalibrateState State { get; }   // data/calibrate_state.json
    /// <summary>补录：Backup → InsertEstimatedUsage(均分 count 份、余数入最后一笔、prompt=hit+miss、total=prompt+output)
    /// → ClearMissedByModel → RecalcDerived → RebuildToday → 持久化 HasCalibrated → 触发 CalibrateCompleted。
    /// 参数校验：provider/model 非空、count≥1（<1 按 1）、hit+miss+output>0（否则 ArgumentException）。</summary>
    string ApplyManualCalibrate(string provider, string model, int requestCount,
                                long cacheHit, long cacheMiss, long output);
    /// <summary>回滚：无记录/备份缺失 → InvalidOperationException；成功后 HasCalibrated=false（该轮备份已消费）。</summary>
    void RollbackLastCalibration();
}

/// <summary>旧项目数据一键导入（§6）。方法级串行。</summary>
public interface IImportService
{
    /// <summary>扫描旧目录，产出预览（不修改任何文件）。目录缺 db → Warnings 说明、Preview 恒可返回。</summary>
    ImportPreview Preview(ImportOptions options);
    /// <summary>执行导入：先 Backup 新库 → 事务导入 → RecalcDerived → RebuildToday → 写 manifest → 触发 ImportCompleted。
    /// 失败抛 ImportException（导入事务已回滚，新库保持原状）。</summary>
    ImportResult Import(ImportOptions options);
}
```

```csharp
namespace TokenMonitor.Core.SysUtil;

public interface ISysUtil : IDisposable
{
    string DataDir { get; }   // exe 旁 data/（失败回退当前目录\data）
    /// <summary>单实例：Mutex "Local\TokenMonitor.SingleInstance"。已有实例 → false（并已置位显示信号）。</summary>
    bool TryAcquireSingleInstance(out EventWaitHandle showPanelSignal);
    void SetAutoStart(bool enable, string exePath);   // HKCU\Software\Microsoft\Windows\CurrentVersion\Run，值名 "TokenMonitor"
    bool IsAutoStartEnabled();
    IReadOnlyList<int> TimezoneOffsets();             // -720..840 步长 30（53 项）
    string OffsetLabel(int offsetMin);                // "UTC±0" / "UTC+8" / "UTC+5:30"
    void OpenInExplorer(string path);
    void OpenTextFile(string path);                   // 默认编辑器打开（配置文件）
}
```

#### 2.3.9 UsageCoordinator 与 ITokenMonitorEngine（组合根）

```csharp
namespace TokenMonitor.Core.Stats;

/// <summary>摄入协调器（C5 修复的落点）：一把串行锁保护"报价→累加→落库→文件日志"与"整桶重建"。</summary>
public sealed class UsageCoordinator : IUsageIngestor
{
    public UsageCoordinator(IAccumulator acc, IStore store, IUsageFileLogger fileLog,
                            IPricingEngine pricing, IEventBus bus);
    /// <summary>锁内：GetMultiplier/GetCost（Utc+Local 各一次）→ acc.AddUsage → store.AddUsage(单行) → fileLog.Log。
    /// 全捕获：内部异常 → Warn 日志 + store.AddMissed("ingest error")，绝不向代理线程抛出。</summary>
    public void Ingest(UsageEvent evt);
    /// <summary>锁外直写 store.AddMissed（小量、独立）。</summary>
    public void ReportMissed(MissedCapture capture);
    /// <summary>锁内：store.Flush() → 读 UTC 今日（GetTodayUtc）与本地今日（GetLogsByRange）→ Replace 两桶 →
    /// 补历史模型骨架（GetAllModels）。重建期间 Ingest 因锁排队，恢复后自然落新桶——无间隙、无重复、无死锁 [C5]。</summary>
    public void RebuildToday();
    /// <summary>快照 = acc.Snapshot(missedByModel)；missedByModel 经 1s TTL 缓存查询（GetMissedCountByModel 本地日窗口）。</summary>
    public StatsSnapshot BuildSnapshot();
}

/// <summary>每日文件日志（data/usage_logs/YYYY-MM-DD_Provider_Model.log + .csv，本地时区；格式照原 usagelog.go，
/// CSV 带 UTF-8 BOM 与表头）。offsetMin 变更时调 Configure。</summary>
public interface IUsageFileLogger
{
    void Log(UsageEvent evt, double costCny, double costUsd);   // 线程安全；内部锁串行追加
    void Configure(int offsetMin);
}
```

```csharp
namespace TokenMonitor.Core;

/// <summary>App 唯一门面。StartAsync/StopAsync 全生命周期只允许调用一次（重复抛 InvalidOperationException）。</summary>
public interface ITokenMonitorEngine : IAsyncDisposable
{
    IConfigService Config { get; }
    IPricingEngine Pricing { get; }
    IStore Store { get; }
    IProxyEngine Proxy { get; }
    IEventBus Bus { get; }
    IStatsQueryService Queries { get; }
    IExportService Exporter { get; }
    ICalibrationService Calibration { get; }
    IImportService Import { get; }

    Task StartAsync(CancellationToken ct);   // §7.1 启动序列
    Task StopAsync();                        // §7.2 退出序列

    // —— 以下命令均：后台执行重活 + 完成后经总线广播；可在 UI 线程直接调用 ——
    void SetTimezone(int offsetMin);         // 存 settings → pricing.SetEffectiveContext → RebuildToday → DayRolledOver+ConfigChanged
    void SetEffectiveDateMode(string mode);  // 同上 + store.RecalcDerived（生效日期选择变了，daily 需重算）
    void ReloadProxyConfig();                // ReloadProxy → 地址变更则 Stop/Start，否则热换路由表 → ConfigChanged(Proxy)
    void ResetToday(string? modelKey);       // store.ResetToday → RebuildToday → ConfigChanged
    void DeleteModelData(string modelKey);   // store.DeleteModelData → RebuildToday → ConfigChanged
    void DeleteAllData();                    // store.DeleteAllData → RebuildToday → ConfigChanged
}
```

#### 2.3.10 ITrayController（App.Tray；成员须在 UI 线程调用，内部不再同步）

```csharp
namespace TokenMonitor.App.Tray;

/// <summary>托盘菜单回调集（App.Tray 只编排菜单，动作实现由 ViewModels 注入）。</summary>
public sealed record TrayMenuCallbacks(
    Action OpenPanel, Action OpenConfigFile, Action MultiplierConfig, Action PricingConfig,
    Action<int> TimezoneSelected,            // 参数 offsetMin
    Action ToggleEffectiveMode,              // utc ↔ local
    Action ExportToday, Action ExportMonth, Action ExportRecent7Days, Action OpenExportDir,
    Action ManualCalibrate, Action RollbackLastCalibration, Action ReloadConfig,
    Action ToggleAutoStart, Action<int> BallOpacitySelected,   // 40/60/80/100（%）
    Action ToggleBallTopmost, Action ImportLegacy, Action Exit);

public interface ITrayController : IDisposable
{
    /// <summary>创建图标与全项菜单（01 文档 §3-E1 全集，含"导入旧数据"）。Dispose 释放图标。</summary>
    void Initialize(TrayMenuCallbacks callbacks);
    /// <summary>更新"状态/监听地址"禁用项文案（ProxyStateChanged 时）。</summary>
    void SetStatus(bool isListening, string listenAddr, string? error);
    /// <summary>刷新勾选态：时区单选/生效口径/开机自启/悬浮球透明度与置顶（ConfigChanged 后与启动时）。</summary>
    void RefreshChecks();
    /// <summary>回滚项可用态（CalibrateCompleted/启动时）。</summary>
    void SetRollbackEnabled(bool enabled);
}
```

---

## 3. 配置文件 Schema（exe 旁 data/；全部 UTF-8 无 BOM、snake_case、2 空格缩进）

通用规则：读取兼容 UTF-8 BOM（与原版 loadSettings 一致）；文件不存在 → 写默认；JSON 损坏 → 原文件改名 `<name>.corrupt-<yyyyMMdd_HHmmss>` 保留、写默认、Warn 日志（原版直接丢弃旧文件，新策略保留现场，附录 B-1）。所有保存为"临时文件 + File.Replace"原子写。

### 3.1 config.json（Core.Config.ProxyConfig）

```json
{
  "listen_addr": "127.0.0.1:8280",
  "default_provider": null,
  "providers": [
    { "name": "GLM", "base_url": "https://open.bigmodel.cn/api/paas/v4",
      "api_key": "dpapi:...",
      "model_prefix": ["glm-", "zhipu-"], "max_tokens": 32768,
      "strip_params": ["enable_thinking", "reasoning_effort"] }
  ]
}
```

| 字段 | 类型 | 默认 | 校验/语义 |
|---|---|---|---|
| `listen_addr` | string | `"127.0.0.1:8280"` | 必须 `host:port` 且 host ∈ {127.0.0.1, localhost, ::1}（红线：只听回环）；port 1–65535；非法→回默认+Warn |
| `default_provider` | string? | `null` | [C14] 非空时必须等于某 provider.name（否则忽略+Warn）；null=未知模型一律 404 |
| `providers` | array | 首启为 4 个默认项（DeepSeek/GLM/Moonshot/MiMo，base_url 与 model_prefix 照原 DefaultConfig，**api_key=""**，GLM max_tokens=32768） | [S6] 缺失/空数组 → 合并默认项并 Warn 告警日志（不 bricks；用户配置的 listen_addr/default_provider 保留） |
| `providers[].name` | string | — | 非空；重复 → Warn 且仅首个生效 |
| `providers[].base_url` | string | — | 绝对 http(s) URL（Uri.TryCreate 校验），非法→该 provider 停用+Warn |
| `providers[].api_key` | string | `""` | 落盘态：`""` 或 `dpapi:<base64>`；读取兼容明文（Warn 一次"检测到明文密钥，保存时将自动加密"）；保存时明文一律加密。明文/密文都不写日志 |
| `providers[].model_prefix` | string[] | — | 非空数组；空数组→Warn"该 provider 不可被路由"；跨 provider 重复前缀 → 首个生效+Warn |
| `providers[].max_tokens` | int? | `null` | null/缺省=不钳制；>0=钳制上限 |
| `providers[].strip_params` | string[] | 缺省 `["enable_thinking","reasoning_effort"]` | [C15] 显式 `[]`=不剥离；导入旧配置时一律补默认值（保持旧全局剥离行为，§6.4） |

### 3.2 pricing.json（Core.Pricing.PricingDocument；由 IPricingEngine 独占读写）

```json
{
  "multipliers": { "deepseek-chat": { "history": [
      { "effective_from": "2026-09-01", "periods": [ { "start": 9.0, "end": 12.5, "rate": 1.5 } ] } ] } },
  "pricing": { "deepseek-chat": { "history": [
      { "effective_from": "2026-09-01", "currency": "CNY",
        "rules": [ { "start": 9.0, "end": 12.5, "days": [1,2],
                     "input_per_1m": 4.0, "cache_per_1m": 0.8, "output_per_1m": 12.0 } ] } ] } }
}
```

- 键=模型名（不含 provider 前缀，与原版一致）。`effective_from`：`"YYYY-MM-DD"` 或 `null`/`""`（=一直生效，排序为最早）。`start/end`：小时数 0–24，0.5 步进。`days`：1=周一…7=周日，缺省=每天。`currency`：`"CNY"`|`"USD"`（其它值读取时归 USD+Warn）。
- **加载迁移**（载入即规范并回写）：旧平铺 `{periods:[...]}` → `{history:[{effective_from:null, periods}]}`；旧 `{currency,rules}` 同理；`input_per_1k/cache_per_1k/output_per_1k`（≠0 且对应 per_1m 缺失）→ ×1000 迁入 per_1m 并删除旧字段；同一 `effective_from` 重复版本 → 保留链尾（最近保存）版本。以上即原 Load 迁移逻辑，规范落在 `PricingDocument.Normalize()`。
- **运行期字段**：`effective_date_mode`/偏移**不入** pricing.json（存 settings.json），经 `SetEffectiveContext` 注入。

### 3.3 settings.json（AppSettings）

| 字段 | 类型 | 默认 | 校验 |
|---|---|---|---|
| `offset_min` | int | `0` | [-720, 840] 且 30 的倍数；非法→0+Warn |
| `effective_date_mode` | string | `"utc"` | 仅 `"utc"|"local"`；其它→utc |
| `ball_opacity` | int | `255` | 0–255；≤0→255（原版语义：0/缺省视为 255） |
| `ball_topmost` | bool | `true` | 缺省 true（原版为可空 bool） |

### 3.4 ui_state.json（UiState；新文件）

```json
{
  "panel":  { "left": null, "top": null, "width": null, "height": null, "opacity": 1.0, "collapsed": false },
  "card_order": [],
  "hidden_cards": [],
  "card_scope": { "DeepSeek/deepseek-chat": "utc" },
  "card_multiplier_view": { "DeepSeek/deepseek-chat": false },
  "ball": { "left": null, "top": null }
}
```
默认：全空集合/坐标 null（首启布局归 UI-Designer 的默认值逻辑，本文只管持久化）。校验：`opacity` 夹取 [0.3,1.0]；`card_scope` 值仅 utc/local；未知 modelKey 的键在加载时保留（模型是历史数据驱动，不因 providers 变更而删）。`card_multiplier_view` 即原 `data/multiplier_states.json`（bool：true=该卡显示倍率值）。

### 3.5 辅助文件

- `data/calibrate_state.json`：`{ "has_calibrated": false, "last_backup": "" }`（沿用原文件名/字段，回滚项置灰依据）。
- `data/import_manifest.json`：`[ { "source_sha256": "...", "source_path": "...", "imported_at_utc": "...", "counts": { "usage_log": 0, "usage_missed": 0, "op_log": 0 } } ]`（§6.5 幂等依据）。
- `data/logs/TokenMonitor.log`、`data/usage_logs/`、`data/backups/`、`data/export/`：见 §8 与 §4/§2.3.8。

---

## 4. SQLite DDL 与访问语义（data/token_monitor.db）

连接：`new SqliteConnection("Data Source=data/token_monitor.db;Mode=ReadWriteCreate;Default Timeout=5")`；每次打开执行 `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;`（NORMAL 为 WAL 标准搭配，附录 B-2）。Store 内部**单连接 + 一把锁**串行所有操作（本应用写频 <10/s，无需并发读连接）。

### 4.1 建表（列名对齐原 storage.go，便于旧库导入）

```sql
CREATE TABLE IF NOT EXISTS usage_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,                 -- 响应完成时刻 Unix 秒（铁律 4）
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    prompt_tokens INTEGER NOT NULL DEFAULT 0,
    prompt_cache_hit INTEGER NOT NULL DEFAULT 0,
    prompt_cache_miss INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0,
    reasoning_tokens INTEGER NOT NULL DEFAULT 0,
    total_tokens INTEGER NOT NULL DEFAULT 0,   -- 旧库此列可能缺失（ALTER 迁移而来）；导入时 [S2] 回退
    estimated INTEGER NOT NULL DEFAULT 0,      -- 1=校准补录/导入估算行（原为 ALTER 迁移，新库建表即含）
    import_batch INTEGER                       -- 新增：导入批次号（运行期写入为 NULL）
);
CREATE INDEX IF NOT EXISTS idx_usage_log_ts    ON usage_log(ts);
CREATE INDEX IF NOT EXISTS idx_usage_log_model ON usage_log(provider, model);  -- 新增索引：删除/本地聚合加速（附录 B-3）

CREATE TABLE IF NOT EXISTS usage_daily (          -- 即 UTC 桶（列与原库完全一致）
    date TEXT NOT NULL,                           -- UTC 日历 YYYY-MM-DD
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    prompt_tokens INTEGER NOT NULL DEFAULT 0,
    prompt_cache_hit INTEGER NOT NULL DEFAULT 0,
    prompt_cache_miss INTEGER NOT NULL DEFAULT 0,
    completion_tokens INTEGER NOT NULL DEFAULT 0,
    reasoning_tokens INTEGER NOT NULL DEFAULT 0,
    total_tokens INTEGER NOT NULL DEFAULT 0,
    request_count INTEGER NOT NULL DEFAULT 0,
    mul_total INTEGER NOT NULL DEFAULT 0,
    mul_prompt INTEGER NOT NULL DEFAULT 0,
    mul_cache_hit INTEGER NOT NULL DEFAULT 0,
    mul_cache_miss INTEGER NOT NULL DEFAULT 0,
    mul_completion INTEGER NOT NULL DEFAULT 0,
    mul_reasoning INTEGER NOT NULL DEFAULT 0,
    cost_cny REAL NOT NULL DEFAULT 0,
    cost_usd REAL NOT NULL DEFAULT 0,
    PRIMARY KEY (date, provider, model)
);
CREATE INDEX IF NOT EXISTS idx_usage_daily_date ON usage_daily(date);

CREATE TABLE IF NOT EXISTS usage_missed (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    status INTEGER NOT NULL DEFAULT 0,     -- 上游状态码；0=无法判定（保守视为已计费）
    reason TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_usage_missed_ts    ON usage_missed(ts);
CREATE INDEX IF NOT EXISTS idx_usage_missed_model ON usage_missed(provider, model);

CREATE TABLE IF NOT EXISTS op_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    model TEXT NOT NULL,
    action TEXT NOT NULL,
    detail TEXT NOT NULL,
    effective_from TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_op_log_model ON op_log(model);
```

### 4.2 批量刷写与失败策略 [C12]

- 触发：缓冲 ≥3 条或 5 秒定时；`Flush()` 供退出/备份/重算前调用。
- **单事务**内完成：usage_log 批量 INSERT + 对应 usage_daily UPSERT（原版 daily 在事务外追加，失败行"进 daily 不进 log"即 C12；合并为单事务后二者天然一致，附录 B-4）。
- 事务开始/提交失败 → 整批放回缓冲头部重试（原版语义保留）；事务内单条 INSERT 失败 → **该行整体跳过**（log 与 daily 都不写）+ Warn，不拖垮同批其它行。

### 4.3 usage_daily 增量 UPSERT（写入路径）

```sql
INSERT INTO usage_daily (date,provider,model, prompt_tokens,prompt_cache_hit,prompt_cache_miss,
  completion_tokens,reasoning_tokens,total_tokens, request_count,
  mul_total,mul_prompt,mul_cache_hit,mul_cache_miss,mul_completion,mul_reasoning, cost_cny,cost_usd)
VALUES (?,?,?, ?,?,?,?,?,?, 1, ?,?,?,?,?,?, ?,?)
ON CONFLICT(date,provider,model) DO UPDATE SET
  prompt_tokens=prompt_tokens+excluded.prompt_tokens, /* …其余 12 个数值列同式累加… */,
  request_count=request_count+1;
```
`date` = `ts` 的 UTC 日期。

### 4.4 op_log

写入即返回（同步小量）；写后执行 `DELETE FROM op_log WHERE id NOT IN (SELECT id FROM op_log ORDER BY id DESC LIMIT 5000)` 防无界增长（原版无裁剪，附录 B-5）；读取恒 `ORDER BY ts DESC, id DESC LIMIT 500`（与原版一致）。动作字典（新增动作为审计需要，附录 B-6）：`model_created`（首次出现的模型，detail="首次出现模型 {model}"）、`set_multiplier`、`set_pricing`、`manual_calibrate`、`rollback_calibrate`、`import_legacy`、`reset_today`、`delete_model_data`、`delete_all_data`。

### 4.5 RecalcDerived（[C1][C4][S2] 全量重算，幂等）

1. `store.Flush()`。
2. 读全部 usage_log（ts, provider, model, 5 token 列, total）。
3. 逐行：`effTotal = total > 0 ? total : prompt + completion`（[S2] 回退）；`hour = UTC 分数小时 = H + M/60.0 + S/3600.0`（[C4]）；`rate = rateFn(key, ts, hour, 0)`；倍率分量 = `Round(x*rate)` 且 `mulTotal = mulPrompt + mulCompletion`（铁律 2）；`(cny,usd) = costFn(key, ts, hour, 0, hit, miss, comp)`。
4. 按 (UTC日期， provider, model) 聚合后**单事务**内：对每个组 `INSERT ... ON CONFLICT(date,provider,model) DO UPDATE SET`（全部 15 个数值列覆盖）**[C1 核心修复]**；并 `DELETE FROM usage_daily` 中不属于任何组的孤儿行（保证铁律 5 的 daily≡log）。
5. 全程单事务；失败抛 StorageException（调用方 log，数据无损——daily 旧值仍在）。

### 4.6 ResetToday 语义（[C8]，与原版差异见附录 B-7）

`ResetToday(modelKey?, offsetMin)`：窗口 A=UTC 今日 [00:00,+24h)，窗口 B=本地今日（offsetMin 换算）。删除：`usage_daily.date=UTC今日`（±model）、`usage_log.ts ∈ (A ∪ B)`（±model）、`usage_missed.ts ∈ (A ∪ B)`（±model）。原因：[C1] 修复后 RecalcDerived 会从 log 复活 daily，"重置今日"必须落到 log 层才不可逆；双窗口保证 UTC/Local 两侧卡片与横幅同时清零。

### 4.7 备份与回滚 [C6]

- `Backup()`：先 `Flush()` → `VACUUM INTO 'data/backups/token_monitor_backup_<utc yyyyMMdd_HHmmss_fff>.db'`（重名追加 `_001` 序号）。
- `ReplaceDatabase(path)`：`Flush()` → 关闭连接 → **删除 `token_monitor.db-wal` 与 `-shm`** → `File.Copy(backup→dbPath, overwrite)` → 以同连接串重开 → 建表 → 清空内存缓冲。[C6] 的 WAL 侧车与 DSN 不一致两个坑均已消除。

---

## 5. 事件总线契约（Core.Events；UI 消费接口的权威定义）

| 事件类型（record, 实现 IEvent） | 载荷 | 发布者/频率 | 合并策略 | 典型订阅者 |
|---|---|---|---|---|
| `PanelStatsTick(StatsSnapshot Snapshot)` | 全量不可变快照 | StatsTicker 每 **200ms** | PublishCoalesced（仅最新） | 面板 ViewModel（UiThread） |
| `BallStatsTick(StatsSnapshot Snapshot)` | 同上（同一实例复用） | StatsTicker 每 **1s** | PublishCoalesced | 悬浮球 ViewModel（UiThread） |
| `ConfigChanged(ConfigSectionFlags Sections)` | 变更节标志（Proxy=1, Settings=2, Pricing=4, UiState=8） | Engine（配置/倍率/计价/设置变更后） | PublishCoalesced（按最新） | QueryService 缓存失效、托盘 RefreshChecks、各 ViewModel |
| `DayRolledOver(string UtcDate, string LocalDate, int OffsetMin)` | 重建后的当日口径 | Engine（跨午夜守望/时区切换后 RebuildToday 完成） | PublishCoalesced | 折线图/范围缓存失效、卡片刷新 |
| `ProxyStateChanged(bool IsListening, string ListenAddr, string? Error)` | 监听状态 | IProxyEngine.StateChanged 转发 | PublishCoalesced | 托盘 SetStatus、面板状态条 |
| `CalibrateCompleted(string BackupPath)` | 补录成功 + 备份路径 | CalibrationService | Publish（FIFO，不合并） | 托盘 SetRollbackEnabled(true)、op_log 已由服务写入 |
| `ImportCompleted(ImportResult Result)` | 导入结果 | ImportService | Publish（FIFO） | 导入向导、QueryService 缓存失效 |

订阅/退订语义（§2.3.6 已定义）补充：
- **分发模型**：每 (TEvent 主题) 一条投递通道；UiThread 模式经订阅时捕获的 SynchronizationContext.Post；BackgroundPool 直接线程池。同主题处理顺序：FIFO（Publish）或"最新者胜"（PublishCoalesced）；跨主题不保证顺序。
- **背压**：快照类事件必须 PublishCoalesced——UI 阻塞 >200ms 时中间快照自动被跳过，永不堆积。
- **异常隔离**：任一 handler 抛异常 → 捕获 + `[Events]` Warn 日志 + 继续分发其余 handler。
- **一次性事件**（CalibrateCompleted/ImportCompleted）不合并、有界队列容量 32、溢出丢最旧并 Warn。
- 订阅者必须快速返回；长工作（如导出）自行转后台线程。
- 引擎内部依赖链（实现者参考）：`IPricingEngine.Changed` / `IConfigService.Saved` → Engine → `PublishCoalesced(ConfigChanged)`；`UsageCoordinator.RebuildToday()` 完成 → `PublishCoalesced(DayRolledOver)`。

---

## 6. 旧数据导入映射（G1；IImportService）

适用源：旧版三进程项目的 data 目录（`token_monitor.db`、`config.json`、`pricing.json`、`settings.json`，可选 `multiplier_states.json`、`calibrate_state.json`）。执行者：向导（App.Services）收集 `ImportOptions` → `Preview` → 用户确认 → `Import`。

### 6.1 导入前动作

1. 校验旧库可打开（只读方式试连 + `PRAGMA table_info` 内省每表实际列集——旧库可能缺 `total_tokens`/`estimated` 列）。
2. `IStore.Backup()` 备份**新库**（VACUUM INTO，§4.7）。
3. 计算旧库 SHA256，对照 manifest 判定是否已导入（§6.5）。

### 6.2 token_monitor.db 四表 → 新库逐列映射

**usage_log → usage_log**（唯一全量导入的用量表）：

| 旧列 | 新列 | 清洗规则 |
|---|---|---|
| `ts` | `ts` | 原值；≤0 或 > 当前+1d → 丢弃该行并计数警告 |
| `provider` / `model` | 同名 | Trim；空值行丢弃；不重组键（模型键规则铁律 3 仅在展示/查询时切分） |
| `prompt_tokens` | `prompt_tokens` | max(0, 值) |
| `prompt_cache_hit` | `prompt_cache_hit`（→ CacheHitTokens） | max(0, 值) |
| `prompt_cache_miss` | `prompt_cache_miss` | **重算**：`miss = clamp(prompt - hit, 0, prompt)`（旧库可能违反恒等式） |
| `completion_tokens` | `completion_tokens` | max(0, 值) |
| `reasoning_tokens` | `reasoning_tokens` | max(0, 值)；> completion → 截为 completion（reasoning 是其子集） |
| `total_tokens` | `total_tokens` | **[S2] 回退**：`total > 0 ? total : prompt + completion`（落库即归一，此后读侧永不再回退） |
| `estimated`（列存在时） | `estimated` | 原值（0/1） |
| （无） | `import_batch` | 本批次号（manifest 序号，从 1 递增） |
| （无对应） | `mul_*`/`cost_*` | 不导入——usage_log 从不存这些列；导入完成后 RecalcDerived 重建 daily |

**usage_daily → 不导入**。理由（铁律 5）：daily 全列可由 usage_log + 导入后的 pricing.json 精确重算；直接导入会与新 pricing 重算结果冲突。导入完成后自动执行 `RecalcDerived`（C1 修复保证 INSERT 生效）。

**usage_missed → usage_missed**：`ts/provider/model/status/reason` 五列 1:1 原值复制；ts 非法行丢弃。

**op_log → op_log**：`ts/model/action/detail/effective_from` 五列 1:1 原值复制（保留旧操作历史；读取恒 500 条不受影响）。

全部行插入在**单个事务**内（WAL busy_timeout 与运行期摄入共存；导入期间代理照常捕获，其写入在锁上自然排队）。

### 6.3 usage_log 旧表列缺失兼容

`PRAGMA table_info(usage_log)` 得实际列集：缺 `total_tokens` → 该列按 0 读（触发 [S2] 回退）；缺 `estimated` → 按 0；缺 `reasoning_tokens` → 按 0。`usage_missed`/`op_log` 缺列同理按默认值。

### 6.4 JSON 配置映射

| 源 | 目标 | 规则 |
|---|---|---|
| 旧 `config.json` | 新 config.json（`SaveProxy`） | `listen_addr` 校验回环后沿用；providers 逐项：name/base_url/model_prefix/max_tokens 直迁；**api_key 默认不迁移**（新值为 `""`，用户必须手填——安全红线）；仅当向导勾选"迁移 API 密钥"时读旧明文 → `EncryptApiKey` → 以 `dpapi:` 落盘；**`strip_params` 一律写默认 `["enable_thinking","reasoning_effort"]`**（旧版全局剥离，保持行为一致 [C15]）；`default_provider` = null（新版未知模型 404，附录 B-8） |
| 旧 `pricing.json` | `PricingEngine.ReplaceDocument`（Normalize 后） | 格式本就兼容：经 §3.2 加载迁移（平铺→history、per-1K→per-1M、同日期去重）后整体替换 |
| 旧 `settings.json` | 新 settings.json | 四字段直迁 + §3.3 默认值补齐（`ball_opacity<=0→255`、`ball_topmost` 缺省 true） |
| 旧 `multiplier_states.json`（可选） | ui_state.`card_multiplier_view` | `{"provider/model": bool}` 原样并入 |
| 旧 `calibrate_state.json`（可选） | 新 calibrate_state.json | 若 `last_backup` 指向的备份文件存在 → 复制到新 `data/backups/imported_<原名>` 并更新路径、`has_calibrated=true`；文件缺失 → `has_calibrated=false` |

### 6.5 幂等与重复导入

- manifest 按 `source_sha256` 判重：已导入的同一旧库 + `MergeIfNew`（默认）→ 拒绝并返回"该旧库已于 {时间} 导入过"；用户选 `ReplaceAll` → 清空 usage_log/usage_missed/op_log/usage_daily 四表后重新全量导入（config/settings 仍按上表覆盖）。
- config/settings/pricing 的导入不受 manifest 限制（可随时重跑，最后者胜），仅 DB 导入受控。
- 导入收尾：`RecalcDerived` → `UsageCoordinator.RebuildToday()` → `QueryService` 缓存失效（经 ImportCompleted）→ `LogOp("all","import_legacy", "导入旧数据 N 条, 来源 {path}", "")`。

---

## 7. 进程生命周期

### 7.1 启动序列（MonitorEngine.StartAsync + App 引导）

1. **单实例**：`ISysUtil.TryAcquireSingleInstance`；已有实例 → 置位 `showPanelSignal` 后退出（进程退出码 0）。
2. **DPI**：app.manifest 声明 PerMonitorV2 + `SetProcessDpiAwarenessContext(-4)` 兜底（任何窗口创建前）。
3. **日志**：初始化 `Logger`（§8；>5MB 先轮转）。
4. **全局异常钩子**：`AppDomain.UnhandledException` / `TaskScheduler.UnobservedTaskException` / `DispatcherUnhandledException` → 记 Error + 尽力 UI 兜底（§7.3）。
5. **配置**：IConfigService 加载 config/settings/ui_state（校验修复 [S6]）；IPricingEngine 加载 pricing.json + `SetEffectiveContext(mode, offset)`。
6. **存储**：IStore 打开（DDL + WAL）。
7. **事件总线**、UsageCoordinator、StatsQueryService、各服务装配。
8. **历史重算**：后台 `store.RecalcDerived(rateFn, costFn)`（一次，修正历史/导入遗留），完成前 UI 可先渲染旧快照。
9. **恢复今日桶**：`UsageCoordinator.RebuildToday()`（UTC 从 usage_daily 整桶替换 + Local 从 usage_log 重放 + 历史骨架），之后立即 `PublishCoalesced(DayRolledOver)`。
10. **代理监听**：`IProxyEngine.Start(listen_addr)`；失败 → `ProxyStateChanged(false, addr, error)`，**进程继续运行**（托盘/面板可用，用户可改配置后"重载配置"）——与原版"端口占用即退出"不同（附录 B-9，单进程下退出会丢 UI）。
11. **定时器**：StatsTicker（200ms 面板 / 1s 球）；DayWatch（30s：UTC 日期或本地日期变化 → `RebuildToday` + `DayRolledOver`；offset 变化由 SetTimezone 主动处理，守望器跳过一轮）。
12. **UI**：托盘 Initialize → 按 `ui_state.panel.collapsed` 决定显示面板或仅悬浮球；订阅 `showPanelSignal`（第二次实例唤起）。

### 7.2 退出序列（ITokenMonitorEngine.StopAsync；托盘"退出"/系统关机/致命异常共用）

> 线程约束（实测回归）：整个停止序列**不得在带 SynchronizationContext 的线程上同步等待**。
> ASP.NET Core 主机的 StopAsync 内部 await 默认捕获上下文；在 WPF UI 线程阻塞 GetResult()
> 会让续体排进被阻塞的派发队列 → 互锁，表现为"托盘退出后进程只能任务管理器结束"。
> 实现：App 在后台线程跑 StopAsync，完成后再回 UI 线程 Shutdown（ProxyEngine.StopHostQuiet 同样脱上下文）。

1. 停 StatsTicker 与 DayWatch（先断 UI 数据流）。
2. `IProxyEngine.Stop(gracefulTimeout: 10s)` [S5]：停止接受新连接 → 等待 InFlightCaptures 归零或超时 → 取消捕获 CTS（在途 SSE 续读任务随 30 分钟上限 CTS 链接一起中止）→ HttpListener.Stop/Abort。
3. `store.Flush()` → `store.CheckpointWal()`。
4. 保存 ui_state（强制落盘防抖队列）与 settings（如有脏）。
5. 托盘 Dispose（移除图标）→ 关闭悬浮球/面板窗口。
6. `store.Dispose()`（关连接）→ 释放 Mutex/EventWaitHandle。
7. 兜底：退出流程 20s 未结束 → 记日志后 `Environment.Exit(0)`（正常路径 <1s 自然退出，计时随进程消失）。

### 7.3 异常兜底

- **单请求级**：代理管线每请求 try/catch → 500/502 响应 + `[Proxy]` Error 日志，进程照常 [A8]；捕获/统计异常 → UsageCoordinator 内捕获 → Warn + 必要时漏抓登记。
- **任务级**：RecalcDerived/导出/导入失败 → 日志 + 一次性事件/状态提示，主流程不受影响。
- **UI 线程级**：DispatcherUnhandledException → 记日志 + 置错误状态，不退出（用户可托盘退出）。
- **致命级**：AppDomain.UnhandledException → 尽力 `store.Flush()` → 记日志 → Exit(非 0)。

---

## 8. 错误处理与日志规范

### 8.1 Core 层异常策略

| 异常类型 | 抛出点 | 边界处理 |
|---|---|---|
| `ProxyStartException` | IProxyEngine.Start | Engine 捕获 → ProxyStateChanged(error)，不终止进程 |
| `StorageException` | IStore 查询/维护方法 | 调用方捕获 → 日志 + 状态提示；AddUsage/AddMissed/LogOp 内部自吞（重试/丢弃策略见 §4.2） |
| `ConfigException` | IConfigService.Save* | 调用方捕获 → 对话框/托盘气泡提示；加载路径永不抛（§3 修复策略） |
| `ImportException` / `InvalidOperationException` | IImportService / ICalibrationService | ViewModel 捕获 → 向导错误页 |
| 解析类 | **不抛**（ParseResult.FailureReason） | — |

原则：Core 对外契约方法可抛（fail-fast 交调用方），**管线内部**（摄入/刷写/守望/事件分发）一律自吞 + 日志 + 降级，绝不向上传染导致进程退出。

### 8.2 日志（Core.SysUtil.Logger 静态门面）

- 级别：`Debug < Info < Warn < Error`；默认门面 Info，存在 `data/logs/.debug` 文件时启用 Debug。
- 文件：`data/logs/TokenMonitor.log`，UTF-8 无 BOM，追加写；启动时与每次写入前检查 >5MB → 轮转为 `TokenMonitor.old.log`（覆盖旧轮转，磁盘占用上界 ≈10MB）。
- 格式：`yyyy-MM-dd HH:mm:ss.fff [LEVEL] [Tag] message`，异常另起缩进行含 `Exception.ToString()`。
- Tag 字典：`Proxy / Parser / Stats / Pricing / Storage / Config / Import / Calibrate / SysUtil / Events / App / Tray`。
- **脱敏铁律**：永不记录 Authorization 头、api_key（明文或密文）、请求体内容；请求级日志只含 method、path、body 字节数、model、token 数。
- 关键事件必须留痕：监听启停与端口冲突、漏抓（含 reason）、补录/回滚（含备份路径）、导入（含行数）、配置修复 [S6] 告警、时区/口径切换、跨午夜重建、未捕获异常。

---

## 9. 附录

### 附录 A：Bug 修法索引（实施代理自查表 + 测试代理回归清单）

| 编号 | 设计落点 |
|---|---|
| C1 | §4.5 RecalcDerived UPSERT；§4.6 |
| C2 | §2.3.7 整备/透传规则（stream 尊重、非 JSON 透传） |
| C3 | §0.1 铁律 1（UnifiedUsage.TotalTokens） |
| C4 | §2.3.2 分数小时；§4.5 步骤 3；回归 09:29/09:30/00:00/24:00/UTC+5:30 |
| C5 | §2.3.9 UsageCoordinator 串行锁 + RebuildToday 协议；§0.6 |
| C6 | §4.7 ReplaceDatabase（删侧车 + 同 DSN） |
| C7 | §2.3.8 GetRecentDays 两口径 -(days-1) |
| C8 | §4.6 ResetToday / DeleteModelData / DeleteAllData 含 missed |
| C9 | §2.3.8 GetRange/ExportRequest 的 scope 换算 |
| C10 | 架构根除：无 /api 面（§2.3.7 仅 /v1/*、/v1/models、/health） |
| C11 | §0.2 铁律 2（Round + mulTotal=分量和） |
| C12 | §4.2 单事务 + 失败行整体跳过 |
| C13 | §3.2 Normalize 迁移仅此一处实现 |
| C14 | §2.3.7 路由 404 + default_provider |
| C15 | §3.1 strip_params per-provider；§6.4 导入默认值 |
| C16 | §1.2 ViewModels 异步回包校验卡片键 |
| C17 | §5 ConfigChanged/DayRolledOver → 查询缓存与 ViewModel 缓存失效 |
| C18 | 未移植死代码（架构决策 §6） |
| C19 | ① §2.3.8 ExportRequest（口径显式传入）② 铁律 3 ModelKey ③ 漏抓 30 分钟上限+横幅提示（§2.3.7）④ §2.3.7 绑定前预检 |
| S1 | §2.3.3 ReplaceUtcBucket/ReplaceLocalBucket 整桶替换语义 |
| S2 | §4.5 步骤 3；§6.2 total 回退（导入即归一） |
| S3 | §2.3.2 平移表缓存 + 失效 |
| S4 | §2.3.3 Snapshot：MissedCaptures=全字典求和 |
| S5 | §2.3.7 Stop(graceful)；§7.2 步骤 2 |
| S6 | §3.1 providers 校验合并默认 + §8.2 告警留痕 |
| S7 | §2.3.8 IStatsQueryService 按需查询 + 事件失效缓存 |

### 附录 B：与原版语义差异清单（每条含理由）

1. 配置文件损坏：原版丢弃；新版改名保留 `.corrupt-*` 再写默认。理由：保留用户现场便于恢复。
2. SQLite 增加 `PRAGMA synchronous=NORMAL`；usage_log 新增 `idx_usage_log_model` 索引。理由：WAL 标准搭配；删除/本地聚合性能（S7 同向）。
3. flush 的 log+daily 合并为单事务、失败行整行跳过（原版 daily 在事务外、失败行仍聚合）。理由：C12 根治，见 §4.2。
4. op_log 写入侧裁剪至最近 5000（原版无界）。理由：常驻进程防膨胀；读取恒 500 不变。
5. op_log 新增动作：manual_calibrate/rollback_calibrate/import_legacy/reset_today/delete_model_data/delete_all_data。理由：操作日志需覆盖新功能审计（原版仅 3 种动作）。
6. "重置今日"同时删 usage_log 与双窗口 missed（原版只删当日 daily）。理由：C1 修复后 log 会复活 daily，见 §4.6。
7. 启动时端口被占用不退出进程（原版 os.Exit）。理由：单进程下 UI 与托盘同进程，退出会连 UI 一起消失；保留状态提示与"重载配置"自救路径。
8. HTTP 面收窄：任意路径不再代理（原版 `/` 兜底全部代理）。理由：减小攻击面；OpenAI 兼容客户端全部走 `/v1/*`。
9. 默认配置 providers 的 api_key 为空字符串（原版为 "sk-your-deepseek-key" 等占位明文）。理由：安全红线 §0.7。
10. reasoning 别名新增顶层 `reasoning_tokens`（原版仅 completion_tokens_details）。理由：01 文档 §5 明确要求。
11. 导入 usage_daily 不导入、由 log 重算（原版两表独立）。理由：铁律 5 + 导入的 pricing 与旧版可能一致，重算结果即旧值；避免了双表口径漂移。
12. `ReplaceDatabase` 后的缓冲语义：丢弃缓冲（原版亦丢弃），但先 Flush 到**旧**库再关连接。理由：与原版一致且回滚目标态纯净。
13. 托盘快捷导出预设（今日/本月/近7日）按 UTC 日历传参（保持原版行为）；导出对话框按用户所选口径（C19-① 的统一规则 = 口径显式随请求传入，不再隐式混用）。
14. usage_missed 的"限流(429)"仅出现在 reason 字典与日志；429 实际不入表（原码语义：只有 status==0/2xx 计入），与 01 文档 A6 的表述差异以原码为准。

---

## 附录 C（v1.1 增补）：皮肤系统（用户评审决策 2026-09-18）

用户决策：UI 不采用单一风格，而是 **4 套可切换皮肤**（前三套方向全部保留 + 新增"空军战斗"风格），且全部皮肤**所有角部圆润化**（验收红线）。

架构落点（对本文档的增补，冲突处以本附录为准）：

1. **皮肤清单**（最终命名以 `design/03-ui-spec.md` 定稿为准）：
   `EditorialInk` 纸墨印刷（浅）/ `GraphiteTerminal` 石墨终端（深）/ `SwissGrid` 瑞士网格（浅）/ 第 4 套空军战斗风（名称定稿后补记）。默认皮肤 = `GraphiteTerminal`（深色常驻优先，用户可在托盘随时切换）。
2. **App 新增 `Themes/Skins/Skin.<Name>.xaml`**（每皮肤一个 ResourceDictionary，全局 DynamicResource 键；运行时经 MergedDictionaries 替换热切换、不重启进程）。视觉令牌的唯一权威定义在 `design/03-ui-spec.md`，实施代理照表翻译为 XAML，不得自行改色。
3. **UiState/设置增字段**：`ui_state.json` 新增 `active_skin: string`（默认 `"GraphiteTerminal"`）；托盘菜单新增"皮肤"子菜单（4 项，当前项勾选）。IUiState 契约同步增补该字段。
4. **Core 不感知皮肤**：Core 无任何 UI 概念；皮肤属 App 层 `Services/ThemeService` 职责（加载字典、切换、持久化 active_skin）。无需占用 Core 事件总线（若 UI 内部需要广播，用 App 内 .NET 事件即可）。
5. **悬浮球与托盘**同受皮肤令牌约束（两态几何由 03-ui-spec.md 规定），四皮肤各自的球体视觉随皮肤令牌走。
