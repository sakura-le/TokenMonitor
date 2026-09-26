# Token Monitor 程序代码与算法设计文档（02）

> 版本 v1.0（2026-09-18）。读者：实施代理（照此零决策实现）、测试代理（照 §9 写 xUnit 回归）。
> 上游文档：`01-module-design.md`（类型/接口/DDL 权威）、`context/01-original-features.md`、`context/02-bug-audit.md`、`context/03-architecture-decision.md`。
> 语义来源声明：本文全部"原实现如何"均逐行读自原 Go 源码
> `D:\GLM Workspace\Projects\Token Calculator\neon-token-monitor\`（parser.go / pricing.go / stats.go / storage.go / proxy.go / usagelog.go / main.go / timeutil.go / tzShift.js 及各自 _test.go），无凭空推断；每处差异均给编号（D-xx）与理由，Bug 编号沿用审计报告（C=确认，S=风险）。
> 类型/接口签名以 01 文档 §2 为准，本文不重复定义，只给**算法、公式、SQL、边界、复杂度、回归**。

---

## 0. 全局记号与算法铁律

### 0.1 记号

| 记号 | 定义 |
|---|---|
| `ts` | Unix 秒（`long`）。一笔请求的归属 `ts` = 响应完成时刻（铁律 4，见 01 §0）。 |
| `分数小时` | `H + M / 60.0`（H=该口径日历的小时 0–23，M=分钟）。秒不参与——时段边界均为 0.5 步进，秒级偏差不可能改变匹配结果（`9:29:59 → 9.483 < 9.5`；`01 §4.5` 允许的 `+S/3600` 变体在本配置约束下数学等价，实现取其一即可）。[C4] |
| `modelKey` | `provider + "/" + model`；切分只按第一个 `/`（`ModelKey.Parse`，铁律 3）。 |
| `ε` | `1e-9`，浮点边界比较容差（沿用原 `shiftPeriodsToLocal` 的判定精度）。 |
| `Round(x)` | `Math.Round(x, MidpointRounding.AwayFromZero)`，结果转 `long`。[C11] |
| `UtcDate(ts)` / `LocalDate(ts, off)` | `DateTimeOffset.FromUnixTimeSeconds(ts)` 分别按 UTC / `UTC+off`（`TimeSpan.FromMinutes(off)`）日历取 `yyyy-MM-dd`。 |
| `UtcFractionalHour(ts)` / `LocalFractionalHour(ts, off)` | 对应日历下的分数小时。 |
| `BucketScope` | `Utc` / `Local`（枚举，Core.Pricing）。 |
| 伪代码 | C# 风格，可读性优先于可编译性；SQL 为 SQLite 方言，参数用 `@name`。 |

### 0.2 铁律（全文引用，定义见 01 §0）

1. **total 同源 [C3]**：`UnifiedUsage.TotalTokens` 在 Parser 一次定型（厂商 `total>0` 取厂商值，否则 `prompt+completion`）；UTC 桶、Local 桶、`usage_daily`、RecalcDerived、AggregateLocal/Hourly、XLSX 导出、DeltaTokens 一律引用该字段（§2.3）。
2. **倍率取整 [C11]**：分量逐项 `Round(x*rate)`；`MulTotal = MulPrompt + MulCompletion`；原始 total 用铁律 1 字段，倍率 total 由 prompt/completion 分量构成（§3.2）。
3. **模型键**：只按第一个 `/` 切分（§5.7/§7 文件名与 SQL 均遵守）。
4. **归属时刻**：`ts` = 响应完成时刻（§1.5/§1.6）。
5. **usage_daily ≡ usage_log 派生**：RecalcDerived 幂等可全量重建 daily（§5.4）；重置必须落到 log 层（§5.7）；导入只导 log 不导 daily（§5.9）。
6. **线程安全 [C5]**：全部可变共享状态经锁保护；重建 = Coordinator 串行锁内"flush→读库→原子换桶"（§3.6、§8）。

### 0.3 原实现语义来源映射

| 本文节 | 原 Go 位置 |
|---|---|
| §1 | `internal/proxy/proxy.go`（handleProxy/RoundTrip/interceptSSE/interceptJSON/recordMissed/upstreamRequestSent/processEvent）、`internal/config/config.go`（MatchProvider） |
| §2 | `internal/parser/parser.go`（rawUsage/normalizeUsage/ParseSSEUsageLine/ParseJSONResponse）+ parser_test.go |
| §3 | `internal/stats/stats.go`（Accumulator/AddUsage/AddLocalUsage/SetTodayStats/GetSnapshot/Reset*）+ stats_test.go、`cmd/ntm-proxy/main.go`（守望/restore/rebuildLocalToday） |
| §4 | `internal/pricing/pricing.go`（Load/dedupe/VersionFor/GetRateAt*/CalcCostAt*/shiftPeriodsToLocal/shiftPriceRulesToLocal/shiftDays）+ pricing_test.go、`frontend/src/utils/tzShift.js`（mergeAdjacent/normalizeItems） |
| §5 | `internal/storage/storage.go`（DDL/Add/flush/upsertDaily/GetTodayStats/GetRecentDays*/Reset*/Delete*/RecalcDerived/InsertEstimatedUsage/Backup/ReplaceDatabase/AggregateLocal/AggregateHourly/ExportData*/GetLogsByRange/ExportToXLSX）、`internal/timeutil/timeutil.go` |
| §6 | `proxy.go ApplyManualCalibrate/splitManualRows/reloadAcc`、`storage.go InsertEstimatedUsage/ClearMissedByModel`、`main.go rollbackTokenData` |
| §7 | `storage.go ExportToXLSX/write*Sheet/hourRange`、`proxy.go handleExport`、`main.go exportData` |
| §8 | `main.go refreshStatsAndProxy/setTimezone/restoreTodayFromDB`、`stats.go` 锁、`proxy.go` 回调注入 |

---

## 1. 请求整备与捕获（Core.Proxy）

### 1.1 HTTP 面与请求分类决策树

HTTP 面只保留 `/health`、`/v1/models`、`/v1/*`（其余路径一律 404，原版 `/` 兜底全部代理——差异 D1-1，理由：收窄攻击面，对齐 01 附录 B-8）。每请求外层 `try/catch` → 日志 + 500/502，单请求异常绝不拖垮进程 [A8]。

```
收到请求
├─ GET /health                → 200 {"status":"ok"}
├─ GET /v1/models             → §1.8 合成模型列表
├─ /v1/* 其余                 → handleProxy：
│   1) 全量读入请求体 body（内存上限 32MB，超限 413）
│   2) 是否"可捕获 JSON 请求"？（IUsageParser.IsJsonRequestBody）
│      判定：Content-Type 含 "application/json"
│             或（Content-Type 缺失且 body 首个非空白字符为 '{'——兼容不发 CT 的客户端，D1-2）
│      ├─ 否 → 纯透传：不解析、不整备、不捕获、不计漏抓（/v1/audio/* multipart、
│      │        GET /v1/files 等）。[C2] 后半（原版对所有 body 无条件 json.Unmarshal，
│      │        失败即 400——D1-3）
│      └─ 是 → 3)
│   3) body 解析为顶层 JSON 对象；失败 → 400（保持原版对 JSON 型请求的失败语义）
│   4) 取 "model"（字符串，缺失/空 → 400）；取 "stream"（bool，缺省 false）
│   5) 路由匹配 provider（§1.2）→ 无匹配 → 404 [C14]
│   6) 请求体整备（§1.3）→ newBody
│   7) 转发 + 响应拦截（§1.4–§1.6）
└─ 其它                        → 404
```

### 1.2 模型前缀路由 [C14]

算法（对齐原 `config.MatchProvider` 的匹配方式，仅改"未匹配"分支）：

```csharp
ProviderConfig? MatchProvider(ProxyConfig cfg, string model)
{
    var lower = model.ToLowerInvariant();
    foreach (var p in cfg.Providers)                      // 配置声明序
        foreach (var prefix in p.ModelPrefix)             // 前缀声明序
            if (lower.StartsWith(prefix.ToLowerInvariant(), StringComparison.Ordinal))
                return p;                                 // 首个命中即返回
    return null;
}
// 无匹配：cfg.DefaultProvider 非空 → 按 name 精确查找 provider 兜底；
//        仍无（或未配置）→ 404，JSON 错误体 {"error":{"message":"unknown model: <model>","type":"invalid_request_error"}}。
```

- 原实现：未匹配**静默落到第一个 provider**（`config.go MatchProvider` 末尾），错账 + 错密钥 [C14]。
- 新实现：拒绝（404）+ 可选 `default_provider` 显式兜底（`config.json` 新字段，校验见 01 §3.1）。
- 复杂度：O(providers × prefixes)；单请求一次，无需缓存。
- 回归要点：`deepseek-chat`→DeepSeek；`unknown-x` 无 default→404；有 default→路由到该 provider（Authorization 用其密钥）；大小写不敏感；跨 provider 重复前缀首个生效。

### 1.3 请求体整备（仅"可捕获 JSON 请求"路径）

按序执行（与原 handleProxy 步骤一致，分支收敛为三点修复）：

1. **include_usage 注入 [C2]**：仅当 `body.stream == true`：
   `body["stream_options"]["include_usage"] = true`（已存在 `stream_options` 对象时合并写入该键，其余键保留；无则创建）。
   **绝不改写 `stream` 本身**——原版无条件 `modifiedBody["stream"]=true` + 注入 stream_options，使 `stream:false` 的客户端收到 `text/event-stream`（Bug 根因）；非流式 JSON 请求的 usage 走响应缓冲路径本就能捕获（§1.6），无需改造。
2. **max_tokens 钳制**：`provider.MaxTokens > 0` 且 `body["max_tokens"]` 为 JSON 数值且 `> MaxTokens` → 替换为 `MaxTokens`（整数）。字符串型值不动（原版 `mt.(float64)` 类型断言失败即跳过——语义保持）；缺省不补。
3. **strip_params 剥离 [C15]**：删除 body 顶层中出现在 `provider.StripParams` 的键（仅顶层，原版语义；嵌套不递归）。缺省 `["enable_thinking","reasoning_effort"]`；显式 `[]` = 不剥离。原版对**所有** provider 全局剥离（C15）；新实现 per-provider（导入旧配置一律补默认值，保持旧行为，见 01 §6.4）。
4. 序列化回 `newBody`（`ContentLength` 同步更新）。

整备**不改变**其余字段顺序/未知字段（`System.Text.Json` 以 `JsonObject` 就地增删，不用 DTO 往返，避免丢字段）。

### 1.4 转发与头重写

- 目标 URL：`provider.BaseUrl` 解析（启动时校验，非法 provider 已停用）。
- 路径：`target.Path + (入站路径剥去前缀 "/v1")`（原 Director 语义：`/v1/chat/completions → base+/chat/completions`）；Query 原样。
- 头：删除 hop-by-hop（Connection/Keep-Alive/Transfer-Encoding/Upgrade/TE/Trailers/Proxy-*，D1-4 显式化）；`Authorization: Bearer <provider密钥>` 重写（明文不进日志）；`Content-Type: application/json`（仅 JSON 整备路径，原版行为）；`Host = target.Host`；`Content-Length = newBody.Length`。
- 上游 `HttpClient`（SocketsHttpHandler，实例复用）：`ConnectTimeout=30s`、`ResponseHeadersRead`、`KeepAlivePing=30s`、`MaxConnectionsPerServer=100`、自动解压、跟随系统代理——对齐原 `newUpstreamTransport` 调优（降低长生成被 RST 概率）。
- 上游错误分类（`UpstreamRequestSent`，对齐原 `upstreamRequestSent`）：

```csharp
bool UpstreamRequestSent(Exception ex)
{
    ex = ex.GetBaseException();                       // 剥 HttpRequestException/AggregateException
    if (ex is SocketException se)
        switch (se.SocketErrorCode)
        {
            case SocketError.HostNotFound:            // DNS 失败 → 请求未送达，不会计费
            case SocketError.ConnectionRefused:       // 拨号被拒
            case SocketError.NetworkUnreachable:
            case SocketError.HostUnreachable:
                return false;
        }
    return true;                                      // 连接建立后的读/写中断 → 厂商可能已计费
}
```
对应原判定：`net.DNSError → false`；`OpError{dial|connect} → false`；`ECONNREFUSED/ENETUNREACH/EHOSTUNREACH → false`；其余 `true`（原 proxy_test.go 五个用例逐一移植）。
未送达 → 仅记日志 + 502；送达后中断 → `ReportMissed(status=0)`（保守计入）+ 502。

### 1.5 SSE 逐行解析状态机（流式捕获）

触发：上游响应 `Content-Type` 含 `text/event-stream`。

**状态机定义**（逐行驱动，`data:` 帧 Payload 进入解析，其余行只转发）：

```csharp
async Task InterceptSseAsync(HttpResponseMessage up, Stream clientOut, CaptureContext ctx)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30)); // 30 分钟上限：
    bool clientGone = false, overCap = false;                              // 独立于客户端（见下）
    Exception? scanErr = null;
    UsageEvent? last = null;                       // "最后一个 usage 块胜出"（原实现语义）
    try
    {
        using var reader = new LineReader(up.Content.ReadAsStream(cts.Token), maxLineBytes: 8 << 20);
        while (await reader.ReadLineAsync() is string line)
        {
            if (!clientGone)
                try { await clientOut.WriteAsync(line + "\n"); await clientOut.FlushAsync(); }
                catch { clientGone = true; }        // 客户端断开：停止转发，继续读 [A3]
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;   // event:/注释/空行仅转发
            var data = line.Length > 5 && line[5] == ' ' ? line[6..] : line[5..]; // "data: x"|"data:x"
            if (data == "[DONE]") continue;
            var r = _parser.ParseSseDataLine(data, ctx.Provider);   // 快路径：行不含 "usage" 直接 null
            if (r.Usage is not null) last = MakeEvent(r, ctx, CaptureSource.Sse);
        }
    }
    catch (OperationCanceledException) { overCap = true; }   // 30 分钟上限到期
    catch (Exception ex) { scanErr = ex; }                   // 上游中断/超长行（>8MB，对齐原 scanner.Buffer 上限）

    if (last is not null) _ingestor.Ingest(last with { CompletedAtUnix = NowUnix() });
    else _ingestor.ReportMissed(BuildMissed(ctx, up.StatusCode, overCap, scanErr));
}
```

要点与原实现对照：
- **帧格式**：原实现只认 `"data: "`（带空格）前缀、`[DONE]` 跳过、`scanner.Buffer(1KB, 8MB)` 行上限；新实现等价，另宽容 `"data:"` 无空格（D1-5，健壮性；匹配规则不变）。
- **最后一个 usage 块胜出**：多 usage 块（厂商重传/多段）时取最后一块——原 `lastEvent = ev` 覆盖语义，保持。
- **转发保真**：逐行 + `"\n"`（原实现 `pw.Write(line+"\n")`，`\r\n` 归一为 `\n`，保持）。
- **客户端断开后续读**：CancellationToken 分离——上游读取绑定 `cts(30min)`，**不链接**客户端连接的取消（对齐原 `context.WithoutCancel(clientCtx)`）；客户端断开只把转发通道置 `clientGone`，解析继续直到流尾。挂起时机：客户端断开而上游迟迟不发 usage → 捕获任务最长存活 30 分钟（原 30 分钟上限保持，超限按漏抓"超上限"登记，D1-6 显式文案）。
- **归属 ts**：读流结束后取 `NowUnix()`（原 `processEvent` 在 scanner 循环后 `time.Now()`，语义一致；usage 块与 `[DONE]` 间隔通常毫秒级）。
- 复杂度：O(上游字节数) 时间、O(1) 额外内存（不整包缓冲——原版已从整包缓冲改为流式，保持）。
- 回归要点：正常流转发+捕获（原 TestInterceptSSEForwardsAndCapturesNormalStream 移植）；**客户端中途断开仍捕获**（原 TestInterceptSSECapturesUsageAfterClientDisconnect 移植：上游在断开后才发 usage 块）；`[DONE]` 前中断→漏抓；多 usage 块取最后；>8MB 行按漏抓登记。

### 1.6 非流式 JSON 缓冲回放

```csharp
async Task InterceptJsonAsync(HttpResponseMessage up, CaptureContext ctx)
{
    byte[] body;
    try { body = await up.Content.ReadAsByteArrayAsync(); }        // 整包缓冲
    catch (Exception ex) {
        body = /* 已读部分 */;                                     // 容忍部分读（原实现语义）
        _log.Warn($"读取上游 JSON 响应中断: {ex.Message}，已读 {body.Length} 字节");
    }
    var r = _parser.ParseJsonResponse(Encoding.UTF8.GetString(body), ctx.Provider);
    if (r.Usage is not null) _ingestor.Ingest(MakeEvent(r, ctx, CaptureSource.Json, NowUnix()));
    else                     _ingestor.ReportMissed(BuildMissed(ctx, (int)up.StatusCode, false, r.FailureReason != null
                                        ? new Exception(r.FailureReason) : null));
    // 原样回放：以内存缓冲替换响应体，ContentLength = body.Length，头不动，交给客户端
}
```
- 原实现：`io.ReadAll` → 解析 → `io.NopCloser(bytes.NewReader)` 回放；读中断取已读部分继续解析。全部保持。
- 触发条件：Content-Type 非 `text/event-stream`（含 `application/json` 及一切其它类型——但非 JSON 请求体在 §1.1 已透传，此处主要为 JSON 响应）。
- 回归要点：2xx JSON 捕获；截断 JSON（半包）→ 尽力解析、失败按漏抓（reason=解析错误文本）；响应头/状态码原样回传。

### 1.7 漏抓记账（usage_missed）

**原因枚举与写入判定**（判定式对齐原 `recordMissed`：`billed = status==0 || (200<=status<300)`）：

| 情形 | status | billed | usage_missed | reason 字符串 |
|---|---|---|---|---|
| 2xx 但响应无 usage 块 | 2xx | 是 | 写 | `响应无 usage 块` |
| SSE 扫描中断 / JSON 读中断（2xx） | 2xx | 是 | 写 | 具体错误文本（原实现同） |
| JSON 解析失败（2xx） | 2xx | 是 | 写 | 解析错误文本（原实现同） |
| 30 分钟读取上限到期 [C19-③] | 2xx | 是 | 写 | `超过 30 分钟读取上限`（D1-6：原为 `context deadline exceeded` 文本，语义同、文案显式化） |
| 上游中断（已送达） | 0 | 是（保守） | 写 | `上游连接中断: {err}`（原 `recordUpstreamAbort` 前缀保持） |
| 429 限流 | 429 | **否** | **不写** | `限流(429)` 仅作运行日志分类（D1-7：原 `missedReason` 的 429 分支实际不可达——429 非 2xx 不满足 `billed`，从未入表；新实现文档化该语义：429 未计费，不计漏抓、不给横幅加数） |
| 其它 4xx/5xx | 4xx/5xx | 否 | 不写 | `非 2xx 响应`（仅日志） |
| 未送达上游（DNS/拨号拒绝/不可达） | 0 | 否 | 不写 | 仅日志（原 `ErrorHandler` 分支保持） |

- 写入：`store.AddMissed(new MissedLogRow(Ts: NowUnix(), Provider, Model, Status, Reason))`——同步小量写（原 `AddMissed` 语义；provider/model 为空则忽略）。
- 计数：内存 `Interlocked.Increment` 仅作诊断（`InFlightCaptures` 类）；**横幅/卡片计数一律来自 `usage_missed` 表查询**（原 handleStats 语义），漏抓计数跨重启不丢。
- provider/model 为空（body 无 model 已在 §1.1 拒绝，理论上不出现）→ 忽略写入（原实现防御保持）。
- 回归要点：2xx 无 usage → 入表；429 → 不入表且横幅计数不变；DNS 失败 → 不入表；连接建立后 wsarecv 强制关闭 → 入表（原 TestRecordUpstreamAbortCountsAsMissed 移植）；status=0 保守计入。

### 1.8 /v1/models、/health、panic 保护

- `/v1/models`：`{"object":"list","data":[{"id":<prefix>,"object":"model","owned_by":<provider.name>}]}`，遍历 providers×model_prefix 合成（原 handleModels 逐字段保持；api_key 绝不出现在响应）。
- `/health`：`{"status":"ok"}`。
- 每请求 `try/catch(Exception)` → `[Proxy]` Error 日志 + 500（转发前）/502（转发中）；监听线程池永不因单请求崩溃 [A8]。
- 差异 D1-8：原版 CORS `*` 中间件取消（无 /api 面，C10 架构根除）；未知路径 404（D1-1）。

---

## 2. 用量归一化（Core.Parser）

### 2.1 归一化算法（别名映射 + 推导顺序）

输入：SSE `data:` 行已剥前缀的 Payload（或完整 JSON 响应体）；输出 `ParseResult`（不抛异常）。

**别名优先级表**（原 `rawUsage` + `normalizeUsage` 逐字段读出；★=新实现新增）：

| 目标字段 | 取值顺序 |
|---|---|
| `prompt` | `prompt_tokens` →（仅当其为 0）`input_tokens`（DashScope，存在性判定） |
| `completion` | `completion_tokens` →（仅当其为 0）`output_tokens` |
| `hit` | `prompt_cache_hit_tokens`（DeepSeek）→ `prompt_tokens_details.cached_tokens`（OpenAI 系）→ `input_tokens_details.cached_tokens`（DashScope）→ 0 |
| `miss` | `prompt_cache_miss_tokens`（DeepSeek，存在性判定）→ 推导 `prompt - hit` |
| `reasoning` | `completion_tokens_details.reasoning_tokens` → ★顶层 `reasoning_tokens` → 0（D2-1：原版仅前者；context/01 §5 明确要求两处皆取） |
| `total` | `total_tokens` →（仅当 ≤0）`prompt + completion`（§2.3） |

**存在性判定**：原实现以 Go 指针 nil-ness 判定（显式 `0` 算"存在"，会覆盖推导）。新实现等价：`TryGetInt` 返回 `(bool present, long value)`，`present` 表示键存在且可读为整数。

**恒等式收口（两道防线，顺序固定）**：

```csharp
// 防线 1：hit 越界防御（hit > prompt 视为坏数据）
if (hit > prompt) { hit = prompt; }                     // D2-2：原版会产生负 miss；与导入清洗规则(§5.9)一致
// 防线 2：恒等式强制（原 normalizeUsage 末段判定保持——显式 miss 字段与恒等式冲突时一律推翻重推导）
if (prompt != hit + miss) { miss = Math.Clamp(prompt - hit, 0, prompt); }
```

推导顺序因此是：`prompt/completion 别名补齐 → hit 三级取值 → miss（显式或推导）→ reasoning → 恒等式收口 → total 铁律`。

### 2.2 total 统一来源铁律 [C3]

- **原实现**：`TotalTokens==0` 时推导 `prompt+completion`（parser.go:172-174）；但 UTC 桶/`usage_daily` 用厂商 total（stats.go:190、storage.go:312 upsert 写入 `l.TotalTokens`），而 Local 桶/AggregateLocal/Hourly **重新**用 `prompt+completion`（stats.go:221、storage.go:921,1028）；倍率值恒按 `prompt+completion`（stats.go:201）。厂商 total ≠ prompt+completion 时两口径与导出互不一致 [C3]。
- **新实现（一次定型，处处引用）**：

```csharp
long total = rawTotal > 0 ? rawTotal : prompt + completion;   // Parser 内唯一决策点
```
此后所有消费者**只读** `UnifiedUsage.TotalTokens`：UTC 桶与 Local 桶累加（§3.2）、`usage_log.total_tokens` 写入、`usage_daily.total_tokens` 聚合、RecalcDerived（含 [S2] 回退，§5.4）、AggregateLocal/Hourly（§5.5）、XLSX"总Token"列（§7）、卡片 DeltaTokens（§3.2）。倍率值的分量口径见铁律 2（`MulTotal=MulPrompt+MulCompletion`，与 total 字段并行不悖——原始"总Token"与倍率"总Token"分别有唯一来源）。
- 与原实现的差异 D2-3：Local 口径聚合不再自行推导 total，改为读取统一字段；仅对"落库前未知"的历史行保留 [S2] 回退（§5.4/§5.5）。
- 复杂度：O(1)。

### 2.3 边界条件

- **全零 usage**：`{"usage":{}}` → 全字段 0、total 推导 `0+0=0`；**事件仍捕获**（RequestCount+1，token +0）——原实现 `resp.Usage != nil` 即捕获，保持。
- **字段类型容错**（D2-4）：原版 Go `json.Unmarshal` 对 `"prompt_tokens":"100"`（字符串）整包失败 → 行为为"漏抓"；新实现 `TryGetInt`：JSON Number→取整（非整数值截断）；String→`long.TryParse(InvariantCulture)`；其它类型（bool/object/array/null）→视为缺失。整包解析失败仍走漏抓（FailureReason）。
- **行快路径**：SSE 行不含子串 `"usage"` → 直接返回 null（原 `strings.Contains` 快路径保持，避免逐行全量反序列化）。
- **畸形 JSON 行**：解析失败返回 `Usage=null`（原 ParseSSEUsageLine 返回 nil），不中断流解析。
- 回归要点：DeepSeek 三字段用例（原 parser_test 用例 1）；miss 缺失推导；DashScope 别名用例（原用例 2：input_tokens_details.cached_tokens→hit=150、miss=50、total=250）；显式 miss 与恒等式冲突被推翻；`hit>prompt` 防御；顶层 `reasoning_tokens` 别名；字符串数值容错；全零 usage 仍计数。

---

## 3. 双口径累积器（Core.Stats：Accumulator + UsageCoordinator）

### 3.1 数据结构

```csharp
sealed class BucketValues {                    // 单口径的全部累加值（UTC 桶与 Local 桶结构相同）
    long RequestCount, Prompt, CacheHit, CacheMiss, Completion, Reasoning, Total;   // Total 遵铁律 1
    long MulTotal, MulPrompt, MulCacheHit, MulCacheMiss, MulCompletion, MulReasoning;
    double CostCNY, CostUSD;                   // 冻结成本（该口径独立冻结，两桶可不同）
    long DeltaTokens;                          // 最近一笔请求的 TotalTokens（卡片"+N"）
    long LastActiveUnix;
}
sealed class ModelAccumulator { ModelKey Key; BucketValues Utc; BucketValues Local; }
sealed class Accumulator : IAccumulator {
    readonly object _gate = new();                       // 一把锁护双桶（原 sync.RWMutex 等价）
    Dictionary<string,int> _index; List<ModelAccumulator> _order;   // 插入序稳定（卡片渲染序）
    readonly IPricingEngine _pricing;
}
```

### 3.2 AddUsage 累加路径（同时写两桶）

```csharp
public AccumulateResult AddUsage(UsageEvent evt)
{
    var key = evt.ModelKey;
    var u = evt.Usage;                                   // prompt=hit+miss 恒成立；total 遵铁律 1
    // —— UTC 口径 ——
    var rateU = _pricing.GetMultiplier(key, evt.CompletedAtUnix, BucketScope.Utc).Rate;   // 无匹配→1.0
    var costU = _pricing.GetCost(key, evt.CompletedAtUnix, u.CacheHitTokens, u.CacheMissTokens,
                                 u.CompletionTokens, BucketScope.Utc);                    // 冻结于完成时刻
    // —— Local(UTC+offset) 口径（同一 ts、不同日历/时段表）——
    var rateL = _pricing.GetMultiplier(key, evt.CompletedAtUnix, BucketScope.Local).Rate;
    var costL = _pricing.GetCost(..., BucketScope.Local);
    lock (_gate)
    {
        var ma = GetOrCreate(evt.Provider, evt.Model);
        Add(ma.Utc,  u, rateU, costU);                   // DeltaTokens = u.TotalTokens；LastActive = ts
        Add(ma.Local, u, rateL, costL);                  // 同式；rateL/costL 为 Local 报价
        return MulFields(u, rateU);                      // 返回 UTC 口径增量 → 供 usage_daily 增量 UPSERT
    }
}
void Add(BucketValues b, UnifiedUsage u, double rate, (double cny,double usd) cost)
{
    b.RequestCount++; b.LastActiveUnix = ts;
    b.Prompt += u.PromptTokens; b.CacheHit += u.CacheHitTokens; b.CacheMiss += u.CacheMissTokens;
    b.Completion += u.CompletionTokens; b.Reasoning += u.ReasoningTokens;
    b.Total += u.TotalTokens;                            // [C3] 两桶同源（原：UTC 用厂商 total、Local 用 p+c）
    (b.MulPrompt, b.MulCacheHit, b.MulCacheMiss, b.MulCompletion, b.MulReasoning) = MulFields(u, rate);
    b.MulTotal = b.MulPrompt + b.MulCompletion;          // [C11] 分量和（原：int((p+c)*rate) 截断）
    b.CostCNY += cost.cny; b.CostUSD += cost.usd;
    b.DeltaTokens = u.TotalTokens;                       // 语义：最近一笔的原始 total（非倍率、非累计）
}
(long mulP, long mulH, long mulM, long mulC, long mulR) MulFields(UnifiedUsage u, double rate)
{
    long H = Round(u.CacheHitTokens * rate), M = Round(u.CacheMissTokens * rate);
    return (Round(u.PromptTokens * rate), H, M, Round(u.CompletionTokens * rate), Round(u.ReasoningTokens * rate));
}
```

- **冻结成本计算点**：报价以 `evt.CompletedAtUnix`（响应完成时刻）为唯一时间输入；内部换算该口径的分数小时（UTC 桶按 UTC 日历、Local 桶按 `UTC+offset` 日历）[C4]。原实现等价（`ts` 传 `time.Now().Unix()` 后取整小时）——修复点仅在分数小时（§4.4）。
- **倍率舍入 [C11]**：原版 `int(float64(x)*rate)` 逐字段截断 → `mulTotal ≠ Σ分量`；新实现逐字段 `Round(AwayFromZero)` 且 `MulTotal=MulPrompt+MulCompletion`。`AccumulateResult`（写 daily 的增量）与桶内值出自同一计算 → 实时路径与 RecalcDerived 重算路径公式全同（幂等前提，见 §5.4 注意）。
  示例：rate=0.55、prompt=101、completion=1：原版 `mulTotal=⌊102×0.55⌋=56` 而 `mulPrompt+mulCompletion=⌊55.55⌋+⌊0.55⌋=55+0=55`（且 total 与分量和差 1）；新实现 `mulPrompt=56, mulCompletion=1, mulTotal=57`。
- **DeltaTokens 语义**：最近一笔请求的 `TotalTokens`（两桶各存一份、值相同）；卡片"+N"显示；整桶替换/重置/恢复后为 0（[S1]，§3.4）。原版 `ma.stats.DeltaTokens = total` 双桶赋值，语义保持。
- **Local 桶倍率/成本独立性**：同一笔在两视图金额可不同（请求时刻落在各自口径的不同时段）——原 `stats.go AddUsage` 注释定义的"瞬时属性"模型原样保留，并有原 stats_test 两个 Orchestrated 用例锁定（移植，见 §9.2）。
- 复杂度：O(1) 均摊（版本链扫描 O(H)、时段匹配 O(P) 或平移缓存命中 O(P)、P 极小）；快照构建 O(模型数)。
- 回归要点：两桶同加一笔后 `Utc.Total == Local.Total`（[C3] 回归，构造 `total ≠ prompt+completion` 数据）；`MulTotal == MulPrompt+MulCompletion`（任意 rate）；DeltaTokens 等于最后一笔 total；UTC 09:30 与 Local 09:30 各按本口径时段计价/倍率。

### 3.3 不可变快照发布（200ms/1s 双频、仅保留最新）

- `StatsSnapshot` 为**不可变 record**：构建时深拷贝各模型两桶值 + 计算合计（`UtcSummary`/`LocalSummary`，Provider="合计"、Model="ALL"）+ 注入漏抓。
- 漏抓注入：`missedByModel = store.GetMissedCountByModel(本地今日窗口)`（1s TTL 缓存，原 handleStats 每 200ms 查询改缓存）；每模型 `MissedCount = dict[modelKey]`；全局 `MissedCaptures = dict 全部值求和` **[S4]**——原实现只累加"出现在快照中的模型"的漏抓（handleStats 遍历 `snap.Models`），纯漏抓新模型不计入总数（S4）；新实现遍历字典全部键。
- 发布：`StatsTicker` 每 200ms 构建 → `bus.PublishCoalesced(PanelStatsTick)`；每 1s 构建同一实例 → `BallStatsTick`。合并式发布=仅保留最新（UI 阻塞不堆积）。替代原 200ms HTTP 轮询 `/api/stats`（C10 一部分）。
- UI 消费线程切换见 §8.3。
- 回归要点：快照不可变（外部改不到内部集合）；MissedCaptures 含无卡片模型的漏抓（S4）；快照构建期间并发 AddUsage 无撕裂（值取自加锁拷贝）。

### 3.4 整桶替换（SetTodayStats → ReplaceUtcBucket [S1]）

原 `SetTodayStats` 只覆盖 UTC 侧数值列，不清 `DeltaTokens`、不动 `localStats`（S1：恢复后卡片可能残留旧"+N"、Local 桶与恢复流程耦合易漏清）。新实现改为**完整替换语义**：

```csharp
void ReplaceUtcBucket(IReadOnlyList<ModelDailyAggregate> todayUtcRows)
{
    lock (_gate) {
        foreach (var ma in _order) ZeroUtcSide(ma);          // 计数/Mul/Cost/DeltaTokens/LastActive 全清
        foreach (var r in todayUtcRows) {
            var ma = GetOrCreate(r.Provider, r.Model);
            Set(ma.Utc, r);                                  // Mul*/Cost* 直接取 usage_daily 列，不重新报价
            ma.Utc.DeltaTokens = 0;                          // [S1] 恢复即归零
        }
    }
}
void ReplaceLocalBucket(IReadOnlyList<LocalReplayRow> rows)  // 原 reloadAcc 的 AddLocalUsage 循环等价物
{
    lock (_gate) {
        foreach (var ma in _order) ZeroLocalSide(ma);
        foreach (var r in rows) {
            var ma = GetOrCreate(r.Provider, r.Model);
            var rateL = _pricing.GetMultiplier(key, r.Ts, BucketScope.Local).Rate;   // 按行 ts 重报价（与 AddUsage Local 路径同口径）
            var costL = _pricing.GetCost(key, r.Ts, hit, miss, comp, BucketScope.Local);
            Add(ma.Local, r.Usage, rateL, costL);
        }
    }
}
```
- Local 桶不持久化 → 恢复必须重放 `usage_log` 并**重新冻结**（原 `AddLocalUsage` 语义；原版该函数 total 用 `p+c` [C3] → 新实现读统一 total 字段）。
- `SeedModelSkeleton(provider, model)`：0 值条目（历史模型卡片保持渲染，原 `seedHistoricalModels` 语义）。
- 回归要点：恢复后 DeltaTokens==0（S1）；Local 桶值不受 ReplaceUtcBucket 影响；恢复后 Local 倍率/成本与实时路径同口径（同一 ts 逐行重放报价一致）。

### 3.5 跨午夜守望与 restore 流程

- `DayWatch`（线程池定时器，30s 周期——保持原 30s）：
```
每 30s：utcToday = UtcDate(now)；localToday = LocalDate(now, offsetMin)
  若 offsetMin ≠ 上次记录 → 仅更新基准（时区切换由 SetTimezone 主动处理，本轮跳过）——原守望线程语义保持
  若 utcToday 变化 或 localToday 变化 → UsageCoordinator.RebuildToday() → bus.PublishCoalesced(DayRolledOver)
```
- `RebuildToday()`（§3.6 协议）即原 `restoreTodayFromDB + rebuildLocalToday + seedHistoricalModels` 的合并：
  1. `store.Flush()`（把缓冲落库，恢复窗口数据完整）；
  2. `utcRows = store.GetTodayUtc()`（`usage_daily WHERE date=UTC今日`）→ `acc.ReplaceUtcBucket(utcRows)`；
  3. `localRows = store.GetLogsByRange(本地今日 [start, end))`（`conv.DayRange(conv.Today())` 换算，原语义）→ `acc.ReplaceLocalBucket(localRows)`；
  4. `foreach store.GetAllModels() → acc.SeedModelSkeleton(...)`；
  5. 发布 `DayRolledOver`（UI 缓存失效 [C17]）。
- 归属跨午夜正确性：长流式请求 23:59 发起、次日 00:10 完成 → `ts` 为次日 → 两桶按次日累计、`usage_daily` 落次日行（铁律 4，与厂商账单口径一致）。
- 回归要点：模拟 UTC 与本地日期先后变化（如 UTC+8 下本地先跨天）各触发一次重建且重建后两桶与 DB 一致；重建期间到达的 Ingest 在锁上排队、恢复后计入新桶（C5，§8.2）。

### 3.6 时区/生效口径变更（[C5] 摘要，协议全文见 §8.2）

原 `refreshStatsAndProxy`：停代理→`RecalcDerived`→**替换全局 `acc`**→重建代理——在途请求把 usage 写进被丢弃的旧桶（DB 有行、卡片丢失），且包级全局变量无锁读写（真实数据竞争）[C5]。新实现：单进程不重建监听；`SetTimezone/SetEffectiveDateMode` = 存 settings → `pricing.SetEffectiveContext`（锁内快照交换+平移缓存失效 [S3]）→（口径变更另加 `store.RecalcDerived` 后台执行）→ `coordinator.RebuildToday()` → 事件广播。在途 Ingest 与重建由同一把串行锁互斥——无间隙、无丢失、无死锁。

### 3.7 差异与理由汇总（§3）

| 编号 | 原实现 | 新实现 | 理由 |
|---|---|---|---|
| D3-1 [C3] | Local 桶 total=prompt+completion | 两桶读统一 total 字段 | 双口径一致性 |
| D3-2 [C11] | `int(x*rate)` 截断、mulTotal 独立截断 | Round + 分量和 | 恒等式一致 |
| D3-3 [S1] | SetTodayStats 不清 Delta/local | 整桶替换语义 | 恢复态纯净 |
| D3-4 [S4] | 漏抓总数只数快照内模型 | 字典全键求和 | 横幅不漏 |
| D3-5 [C5] | 替换全局 acc+重建代理 | 串行锁内换桶，监听不动 | 无丢失无竞争 |
| D3-6 | 200ms HTTP 轮询 /api/stats | 200ms/1s 事件推送（Coalesced） | 单进程架构（C10） |
| D3-7 | 漏抓计数查询每轮直查 DB | 1s TTL 缓存 | 200ms 频率下减压（S7 同向） |

---

## 4. 倍率/计价匹配（Core.Pricing）

### 4.1 数据模型与加载迁移 [C13]

文档模型与 JSON schema 见 01 §3.2（键=模型名，版本链 history 链尾=最近保存）。加载归一化 `Normalize()`（原 `Load` + 两个 `UnmarshalJSON` 逐步读出）：

1. 旧平铺 `{periods:[...]}` → `{history:[{effective_from:null, periods}]}`；`{currency,rules}` 同理（原 UnmarshalJSON 分支）。
2. per-1K 迁移：对每条规则，`input_per_1k≠0 && input_per_1m==0` → `per_1m = per_1k * 1000`，三个单价同式；迁移后旧字段清零（原逻辑）。
3. 版本去重：同一 `effective_from` 重复 → **保留最后一次保存的值，占据首次出现的位置**（原 `dedupeMultiplierVersions`：`out[i] = v` 语义——非"留最后一条"，是"首位置、后值"，实现时勿写成留链尾）。
4. 规范化后回写 pricing.json（原 migrated 重写语义）。
- 回归要点：原 pricing_test 的 TestMigrateOldFormat / TestDedupeVersions 移植；注意去重后 1 月请求取值 9.0（后值）。

### 4.2 版本链选取（最近保存优先）

```csharp
static string EffDate(string? ef) => string.IsNullOrEmpty(ef) ? "0000-00-00" : ef;

MultiplierVersion? MultiplierVersionFor(string modelKey, string dateStr)   // 计价同构
{
    if (!_doc.Multipliers.TryGetValue(modelKey, out var mc)) return null;
    for (var i = mc.History.Count - 1; i >= 0; i--)        // 链尾=最近保存 → 倒序找
        if (string.CompareOrdinal(EffDate(mc.History[i].EffectiveFrom), dateStr) <= 0)
            return mc.History[i];
    return null;
}
```
- `dateStr` = 请求 ts 在**生效日期口径**下的日期（§4.3）。
- `upsert` 语义（Update* 方法）：同 `EffectiveFrom` 的旧版本移除、新版本插到链尾（原 `upsertMultiplierVersion`）——数组顺序即保存顺序，与倒序选取配合实现"最近保存优先"（原 pricing_test TestLatestSavedWins/TestUpsertMovesToEnd 锁定）。
- 复杂度 O(H)，H 极小，不缓存。
- 回归要点：生效日前一天用旧版本；空生效日期一直生效且作为更早日期兜底；同日期重复设置覆盖并移尾。

### 4.3 生效日期口径（UTC/Local 双基准）

```csharp
string EffectiveDate(long ts) =>
    _effMode == "local" ? LocalDate(ts, _offsetMin) : UtcDate(ts);
```
- 原实现：`effectiveDate(ts)` 按 `time.FixedZone(offset)` 或 UTC 格式化——语义逐字保持。`SetEffectiveContext(mode, offsetMin)`：mode 非 "local" 一律归 "utc"（原 SetEffectiveDateMode 防御保持）；变更即失效平移缓存并广播 ConfigChanged [C17]。
- 口径只影响**版本选择与星期判定**的日历；时段数值恒按 UTC 存储、匹配前按视图口径平移（§4.5）。
- 委托出口 `QuoteRate/QuoteCost(key, ts, hour, offsetMin, …)` 的 `offsetMin` **只**决定时段表/星期口径（0 → UTC 表，非 0 → 按该 offset 平移；`RecalcDerived` 固定传 0），版本选择的日历恒取 `_offsetMin`——若改用调用方 offset，`RecalcDerived` 在 mode=local 下会退回 UTC 日历选版本，与 `Accumulator` 实时路径选中不同版本，[C1] 幂等前提被破坏。

### 4.4 分数小时匹配 [C4]

- **原实现**：`stats.go` 三处只取 `Hour()`（整数 0–23），`pricing.go` 用 `float64(hour) >= p.Start && float64(hour) < p.End` → 配 9.5 起的时段，09:00–09:59 全按前段（1.0 倍率/0 成本）[C4]。
- **新实现**：引擎对外只收 `completedAtUnix`，内部换算分数小时：
  - `Utc` 口径：`UtcFractionalHour(ts)`；
  - `Local` 口径：`LocalFractionalHour(ts, offsetMin)`（如 UTC+5:30 下 UTC 04:00 → 本地 09:30 → **9.5**，恰落 `Start=9.5` 边界，含）。
- 匹配式保持左闭右开：`hour >= p.Start && hour < p.End`；分数小时上界 < 24（`23:59→23.983`），`End=24` 表示覆盖全天尾段；`00:00 → 0.0` 命中 `[0,x)`。
- **一致性硬约束**：实时路径（§3.2）与重算路径（§5.4/§5.5）必须使用同一分数小时函数与同一舍入函数——两处公式任何偏离都会破坏 RecalcDerived 幂等性。实现上收敛为 `Core.Pricing` 的静态纯函数，两路径共用。
- 回归要点：`Start=9.5`：09:29→前段费率、09:30→新费率；`End=12.5`：12:29→段内、12:30→段外；00:00 与 23:59 边界；UTC+5:30 半时区同验（原 TestHalfHourBoundary 扩展分数小时版）。

### 4.5 Local 窗口平移算法（含星期平移、30/45 分钟时区）[S3 缓存]

时段/规则恒按 UTC 存储；Local 视图匹配前平移 `+shift`（`shift = offsetMin / 60.0`，可为负，如 UTC-3:30 → −3.5）。两个纯函数与前端 `tzShift.js` 同口径，供引擎与对话框共用（Local 口径编辑时双向转换：`utcToLocal = normalize(items, +shift)`、`localToUtc = normalize(items, -shift)`）。

- **对话框侧换算已落地（`App.Infrastructure.TimeBasis`，[C13] 回归）**：`effective_date_mode=local` 时，打开对话框把存量 UTC 时段 `+offset` 平移到本地时钟显示、保存时把用户填写的本地时段 `−offset` 平移回 UTC 落库；`utc` 时原样。缺这一步的后果是**对不上账**：用户在"基准 LOCAL"下按本地时间填的时段被引擎当作 UTC 时段匹配，本地晚间（厂商优惠段）落到峰时窗——实测同一份配置"显示 ¥4.40 / 厂商后台 ¥2.2"，补上换算后为 ¥2.53（与按本地时钟手算的 ¥2.5432 一致）。
- 不变式（`PricingApplyTests.DialogRoundTrip_*`）：`stored →(+offset)→ 显示 →(−offset)→ 落库` 后，UTC 与 Local 两口径在整周每个半小时格上取值完全相同。

**A. `ShiftPeriodsToLocal(periods, offsetMin)`**（倍率时段，无星期维度；对齐原 `shiftPeriodsToLocal`）：

```
ε = 1e-9；segments = []
for p in periods:
    s = p.Start + shift; e = p.End + shift
    while s >= 24: { s -= 24; e -= 24 }              // 归一 s 进 [0,24)，e 同步平移
    while s <  0: { s += 24; e += 24 }
    if e > 24 + ε:                                   // 跨午夜：拆两段（同 rate）
        if 24 - s > ε: push { s, 24, p.Rate }
        if e - 24 > ε: push { 0, e - 24, p.Rate }
    else if e > s + ε: push { s, e, p.Rate }         // 零长段丢弃
按 start 升序稳定排序 → 全表合并（tzShift.js mergeAdjacent 变体，见下）
```

**B. `ShiftPriceRulesToLocal(rules, offsetMin)`**（计价规则，含星期平移；对齐原 `shiftPriceRulesToLocal`）：

```
for r in rules:
    s = r.Start + shift; e = r.End + shift
    dayOffset = floor((r.Start + shift) / 24)        // ★ 用"原始起点+shift"计算，先于归一；floor 向 −∞
    while s >= 24: { s -= 24; e -= 24 }
    while s <  0:  { s += 24; e += 24 }
    if e > 24 + ε:                                   // 跨午夜拆分：两段各带星期（次日/当日）
        if 24 - s > ε: push { Start:s, End:24, Days: ShiftDays(r.Days, dayOffset),     单价=r }
        if e - 24 > ε: push { Start:0, End:e-24, Days: ShiftDays(r.Days, dayOffset+1), 单价=r }
    else if e > s + ε:
        push { Start:s, End:e, Days: ShiftDays(r.Days, dayOffset), 单价=r }
按 start 升序稳定排序 → 全表合并（同 start/end 邻接 && priceRuleSameValue）
priceRuleSameValue(a,b)：三个单价逐项相等 && Days 排序后长度逐元素相等（空=每天，空对空相等）
```

`ShiftDays(days, n)`：`days` 空表 → 空表（每天）；否则 `d → ((d - 1 + n) % 7 + 7) % 7 + 1`（n 先归一 mod 7；1=周一..7=周日循环）。

**dayOffset 精确规则与例证**（必须与原实现逐位一致）：
- `dayOffset = floor((Start+shift)/24)` 在**归一前**计算，可为负。例：`Start=2, shift=-8 → -6/24 → floor = -1`。
- 正偏移整段越日：`Start=20,End=24,shift=+8` → s=28,e=32；dayOffset=1；归一 s=4,e=8；e≤24 → 单段 `[4,8)` days+1（UTC 周六 22:00 = 本地周日 06:00 的正确平移）。
- 负偏移跨午夜：`Start=2,End=10,shift=-8` → s=-6,e=2；dayOffset=floor(-0.25)=-1；归一 s=18,e=26；e>24 → `[18,24)` days−1 与 `[0,2)` days+0。
- 全天平移：`Start=0,End=24,shift=+8` → `[8,24)` days+0 与 `[0,8)` days+1（两段 days 不同**不合并**，合并判据含 Days）。

**合并算法（全表变体）**：排序后对每段，在已输出列表中查找 `|out[i].End − seg.Start| < ε` 且同值（倍率：Rate 相等；规则：priceRuleSameValue）者 → 延伸 `out[i].End = seg.End`；否则追加。与 Go 版"只看前一条"的差异 D4-1：同 start 的片段因稳定排序插入顺序可能隔开可合并对，全表扫描保证与前端 `tzShift.js` 结果逐条一致（对匹配结果无影响——匹配遍历全部片段；合并只是规范化，供 Local 口径编辑往返不失真）。

**平移缓存 [S3]**：
- 原实现每请求对每候选时段表重算平移 O(P log P)/O(R log R)（`GetRateAtLocal/CalcCostAtLocal` 每次调用 `shift*ToLocal`）[S3]。
- 新实现：`Dictionary<ShiftCacheKey, ShiftedTable>`；`ShiftCacheKey = (modelKey, 表类型, 版本引用, offsetMin)`——**必须含版本引用**（版本随请求日期变化，缺失该维度会跨生效日期命中错表；与 01 §2.3.2 的"版本引用"一致；effmode 变更经整体失效覆盖）。`ShiftedTable` 含平移后的 Periods 与 Rules 两组（惰性分别构建亦可）。
- 失效：`SetEffectiveContext / ReplaceDocument / UpdateMultiplierVersion / UpdatePricingVersion / 导入` → `cache.Clear()`（文档修订号整体失效，避免逐一追踪）。
- 复杂度：命中 O(1) 查表 + O(P)/O(R) 匹配；未命中一次构建 O(P log P)。
- 回归要点：同 offset 同版本二次请求不重算（构建计数器断言，S3）；`UpdateMultiplierVersion` 后缓存失效重算；跨生效日期的两版本各自平移正确；UTC+5:45 下 `Start=9.0` 平移为 `[14.75, …)` 边界匹配。

### 4.6 报价出口与兜底

```csharp
MultiplierQuote GetMultiplier(modelKey, ts, scope):
    v = MultiplierVersionFor(modelKey, EffectiveDate(ts))
    if v == null || v.Periods.Count == 0 → { Rate: 1.0, Matched: false }        // 兜底 1
    table = 缓存平移（scope==Utc → 原表直接用，shift=0）
    foreach p in table: if hour ∈ [p.Start, p.End) → { Rate: p.Rate, Matched: true }
    → { 1.0, false }                                                             // 兜底 2：无匹配时段

CostQuote GetCost(modelKey, ts, hit, miss, comp, scope):
    v = PricingVersionFor(modelKey, EffectiveDate(ts))
    if v == null || v.Rules.Count == 0 → { 0, 0, Priced: false }                 // "不计价/Coding Plan 套餐"
    wd = scope==Utc ? IsoWeekdayUtc(ts) : IsoWeekdayLocal(ts, offset)            // 1=周一..7=周日（周日 0→7）
    foreach r in 缓存平移表:
        if DayMatch(r.Days, wd) && hour ∈ [r.Start, r.End):
            cost = hit/1e6*r.CachePer1M + miss/1e6*r.InputPer1M + comp/1e6*r.OutputPer1M
            return v.Currency == "CNY" ? (cost, 0, true) : (0, cost, true)       // 单规则命中即冻结
    return (0, 0, true)                                                          // 版本在但无命中规则 → 0 成本（与原一致）
```
- `DayMatch`：Days 空/缺省 = 每天（向后兼容，原 `dayMatches`）。
- `Priced` 语义：版本存在且规则非空 = true。未命中任何规则 → 成本 0 且 `Priced=true`（UI 显示 ¥0.0000 而非"不计价"——与原版数值行为一致）。
- 回归要点：无倍率配置 rate=1.0；倍率配置但时段不命中 rate=1.0；无计价配置 Priced=false；CNY/USD 分桶；星期命中（原 TestCostByWeekday：周一 1.0、周六/周日 2.0）与空 Days 每天生效（原 TestCostWeekdayDefaultAll）。

### 4.7 差异与理由汇总（§4）

| 编号 | 原实现 | 新实现 | 理由 |
|---|---|---|---|
| D4-2 [C4] | 整数小时匹配 | 分数小时（引擎内换算） | 9.5 起时段漏配 |
| D4-3 [S3] | 每请求重算平移表 | (model,表,版本,offset) 缓存 + 文档/口径变更整体失效 | O(rules)→O(1) |
| D4-1 | Go 邻接合并 | 全表合并（tzShift.js 口径） | 与前端编辑器往返一致 |
| D4-4 | GetRateAt(key, ts, hour int) | GetMultiplier(key, ts, scope)（引擎内算分数小时） | C4 收口 + 双口径统一入口 |
| D4-5 | 平移函数仅引擎用 | 引擎与 Local 口径对话框共用同一纯函数 | 编辑/匹配同语义 |

---

## 5. 存储算法（Core.Storage）

连接与 DDL 见 01 §4（`Data Source=…;Mode=ReadWriteCreate;Default Timeout=5` + `PRAGMA journal_mode=WAL; busy_timeout=5000; synchronous=NORMAL`；单连接 + 单锁串行）。本节给算法与完整 SQL。

### 5.1 批量刷写（3 条或 5 秒）[C12]

```csharp
void AddUsage(UsageLogRow row)   // 摄入线程调用；只进缓冲
{
    List<UsageLogRow> toFlush = null;
    lock (_gate) { _buffer.Add(row); if (_buffer.Count >= 3) toFlush = TakeBatch(); }
    if (toFlush != null) FlushBatch(toFlush);        // 攒 3 条立即刷（原 flushCnt>=3 语义）
}
// 5s 定时器（_flushLoop）：TakeBatch() → FlushBatch（原 flushLoop 5s 语义）

void FlushBatch(List<UsageLogRow> batch)             // 供 Flush() 公开调用（退出/备份/重算前）
{
    try {
        using var tx = _conn.BeginTransaction();
        using var ins = _conn.CreateCommand(); tx bind;
        ins.CommandText = @"INSERT INTO usage_log
            (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
             completion_tokens, reasoning_tokens, total_tokens, estimated)
            VALUES (@ts,@provider,@model,@p,@hit,@miss,@comp,@reas,@total,@estimated);";
        var ok = new List<UsageLogRow>(batch.Count);
        foreach (var r in batch)
            try { Bind(ins, r); ins.ExecuteNonQuery(); ok.Add(r); }
            catch (Exception ex) { Logger.Warn($"[Storage] 插入失败，本行整体跳过(log+daily): {ex.Message}"); } // [C12]
        foreach (var r in ok) UpsertDaily(r, tx);    // ★ 与 INSERT 同事务（原版在事务外→C12 根因之一）
        tx.Commit();
    }
    catch (Exception) {                              // Begin/Prepare/Commit 失败（DB 锁满等）
        lock (_gate) _buffer.InsertRange(0, batch);  // 整批放回缓冲头部重试（原 requeue 语义保持）
        Logger.Warn("[Storage] 事务失败，整批回队重试");
    }
}
```
- 差异 D5-1 [C12]：原版 daily upsert 在**事务外**且**失败行仍聚合**（flush 中 `stmt.Exec` 失败仅记日志，之后仍 `upsertDaily(l)`）→ log 无行而 daily 有值；新实现单事务 + 失败行 log/daily 一并跳过，commit 失败整批回队（回队后逐行重执行——事务已回滚，无重复）。
- 差异 D5-2：原 flush 的 INSERT 不含 `estimated` 列（建表列默认 0）——新实现显式写 0，结果等价；`import_batch` 运行期恒 NULL。
- 复杂度：O(batch)；缓冲按序保序（回队插头部）。
- 回归要点：3 条触发、5s 触发；单行 INSERT 失败→该行不入 log 也不入 daily、同批其余行提交（C12）；Commit 失败→整批回队且重试成功后无重复行；退出前 Flush 落尽。

### 5.2 usage_daily 增量 UPSERT（写入路径，列级公式与原对齐）

```sql
-- UpsertDaily(row, tx)；@date = UtcDate(row.Ts)（usage_daily 即 UTC 桶）
INSERT INTO usage_daily (
  date, provider, model,
  prompt_tokens, prompt_cache_hit, prompt_cache_miss,
  completion_tokens, reasoning_tokens, total_tokens, request_count,
  mul_total, mul_prompt, mul_cache_hit, mul_cache_miss, mul_completion, mul_reasoning,
  cost_cny, cost_usd)
VALUES (@date, @provider, @model, @p, @hit, @miss, @comp, @reas, @total, 1,
        @mulTotal, @mulPrompt, @mulHit, @mulMiss, @mulComp, @mulReas, @cny, @usd)
ON CONFLICT(date, provider, model) DO UPDATE SET
  prompt_tokens      = prompt_tokens      + excluded.prompt_tokens,
  prompt_cache_hit   = prompt_cache_hit   + excluded.prompt_cache_hit,
  prompt_cache_miss  = prompt_cache_miss  + excluded.prompt_cache_miss,
  completion_tokens  = completion_tokens  + excluded.completion_tokens,
  reasoning_tokens   = reasoning_tokens   + excluded.reasoning_tokens,
  total_tokens       = total_tokens       + excluded.total_tokens,
  request_count      = request_count      + 1,
  mul_total          = mul_total          + excluded.mul_total,
  mul_prompt         = mul_prompt         + excluded.mul_prompt,
  mul_cache_hit      = mul_cache_hit      + excluded.mul_cache_hit,
  mul_cache_miss     = mul_cache_miss     + excluded.mul_cache_miss,
  mul_completion     = mul_completion     + excluded.mul_completion,
  mul_reasoning      = mul_reasoning      + excluded.mul_reasoning,
  cost_cny           = cost_cny           + excluded.cost_cny,
  cost_usd           = cost_usd           + excluded.cost_usd;
```
与原 `upsertDaily`（storage.go:291-316）逐列一致（含 `request_count` 走字面量 1 + `+1` 更新）。@total 遵铁律 1；@mul* 遵铁律 2。执行失败记 Warn 不抛（原日志语义；单事务下已随批处理）。

### 5.3 查询基础

- `GetTodayUtc()`：`SELECT <daily 全列> FROM usage_daily WHERE date = @utcToday;`（原 GetTodayStats）。
- `QueryDailyRange(startUtc, endUtc)`：`... WHERE date >= @s AND date <= @e ORDER BY date, provider, model;`（原 ExportData，闭区间）。
- `GetLogsByRange(startTs, endExclusiveTs)`：
  `SELECT ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss, completion_tokens, reasoning_tokens, total_tokens FROM usage_log WHERE ts >= @s AND ts < @e ORDER BY ts ASC;`
  差异 D5-3：原 SELECT 不含 `total_tokens`（AggregateLocal 被迫用 p+c，C3 根因之一）→ 新实现带出 total，供 [S2]/[C3] 统一（01 §2.3.4 契约同）。
- `GetAllModels()`：`SELECT DISTINCT provider, model FROM usage_daily ORDER BY provider, model;`
- `GetMissed(startTs, endEx)` / `GetMissedCountByModel(startTs, endEx)`：原 SQL 保持（后者 `GROUP BY provider, model` → `map[provider/model]=count`）。
- `GetOpLogs(modelKey)`：null/"all" → `SELECT id, ts, model, action, detail, effective_from FROM op_log ORDER BY ts DESC, id DESC LIMIT 500`；否则 `WHERE model = @k` 同排序同 LIMIT（原语义）。写入侧裁剪至最近 5000（01 §4.4，D5-4 防膨胀）。

### 5.4 RecalcDerived（[C1][C4][S2][C11] 全量重算，幂等）

**原实现问题**：第 3 步只有 `UPDATE … WHERE date=? AND provider=? AND model=?`——usage_log 有而 usage_daily 无的组合被静默丢弃（C1）；`hour` 取整小时（C4）；`a.total += l.total` 对旧库 0 值行放大为 0（S2）；倍率截断（C11）。

**新算法**：

```
RecalcDerived(rateFn, costFn):
  1. Flush()                                   // 缓冲先落库，保证读到全量
  2. rows = SELECT ts,provider,model,prompt_tokens,prompt_cache_hit,prompt_cache_miss,
            completion_tokens,reasoning_tokens,total_tokens FROM usage_log
  3. agg := map[(date,provider,model)] → 累加器
     foreach r in rows:
        date   = UtcDate(r.ts)                                   // usage_daily 即 UTC 桶
        hour   = UtcFractionalHour(r.ts)                         // [C4] H + M/60.0
        effTotal = r.total > 0 ? r.total : r.prompt + r.completion  // [S2] 回退（铁律 1 读侧等价式）
        rate   = rateFn(key, r.ts, hour, 0)                      // offsetMin=0 → UTC 口径
        mulHit = Round(r.hit*rate); mulMiss = Round(r.miss*rate)
        mulPrompt = Round(r.prompt*rate); mulCompletion = Round(r.comp*rate)
        mulTotal = mulPrompt + mulCompletion                     // [C11]（与实时路径同式→幂等）
        mulReas  = Round(r.reas*rate)
        (cny, usd) = costFn(key, r.ts, hour, 0, r.hit, r.miss, r.comp)
        累加 prompt/hit/miss/comp/reas/effTotal/req/mul*/cny/usd 入 agg
  4. 单事务：
     foreach g in agg:
        INSERT … ON CONFLICT(date,provider,model) DO UPDATE SET <15 数值列全量 = excluded.*>   -- [C1]
     DELETE 孤儿行（daily 中存在而 agg 键集中没有的组合）                                        -- 铁律 5
  5. 失败 → 回滚并抛 StorageException（daily 旧值保留，数据无损）；调用方放后台线程
```

**完整 UPSERT 语句 [C1]**：

```sql
INSERT INTO usage_daily (
  date, provider, model,
  prompt_tokens, prompt_cache_hit, prompt_cache_miss,
  completion_tokens, reasoning_tokens, total_tokens, request_count,
  mul_total, mul_prompt, mul_cache_hit, mul_cache_miss, mul_completion, mul_reasoning,
  cost_cny, cost_usd)
VALUES (@date,@provider,@model,@p,@hit,@miss,@comp,@reas,@total,@req,
        @mulTotal,@mulPrompt,@mulHit,@mulMiss,@mulComp,@mulReas,@cny,@usd)
ON CONFLICT(date,provider,model) DO UPDATE SET
  prompt_tokens=excluded.prompt_tokens,  prompt_cache_hit=excluded.prompt_cache_hit,
  prompt_cache_miss=excluded.prompt_cache_miss,  completion_tokens=excluded.completion_tokens,
  reasoning_tokens=excluded.reasoning_tokens,  total_tokens=excluded.total_tokens,
  request_count=excluded.request_count,
  mul_total=excluded.mul_total,  mul_prompt=excluded.mul_prompt,
  mul_cache_hit=excluded.mul_cache_hit,  mul_cache_miss=excluded.mul_cache_miss,
  mul_completion=excluded.mul_completion,  mul_reasoning=excluded.mul_reasoning,
  cost_cny=excluded.cost_cny,  cost_usd=excluded.cost_usd;
```

**孤儿清理**（同事务）：

```sql
CREATE TEMP TABLE IF NOT EXISTS _recalc_keys
  (date TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL,
   PRIMARY KEY(date, provider, model));
DELETE FROM _recalc_keys;
-- 每个聚合组执行一次：
INSERT OR IGNORE INTO _recalc_keys VALUES (@date, @provider, @model);
DELETE FROM usage_daily WHERE NOT EXISTS (
  SELECT 1 FROM _recalc_keys k
  WHERE k.date = usage_daily.date AND k.provider = usage_daily.provider
    AND k.model = usage_daily.model);
DROP TABLE _recalc_keys;
```

- 复杂度：O(N) 读 + O(G) upsert（N=全表行数，G=组数），全量扫表属重任务——仅配置变更/启动/补录/导入后后台执行一次 [S7 不做高频调用]。
- **幂等性硬约束**：第 3 步的分数小时、Round、`mulTotal=分量和`、成本公式必须与 §3.2 实时路径逐字相同（同一批静态函数），否则"实时写→重算"结果漂移。
- 触发点：启动一次（修正历史）、倍率/计价变更后、生效日期口径变更后、手动补录后、旧数据导入后（均后台）。
- 回归要点：log 有行而 daily 无行 → 重算后 daily 行生成且数值正确（C1 核心）；"重置今日（删 daily 留 log）→重算"可恢复（反向验证幂等）；total=0 旧行重算后 = prompt+completion（S2）；倍率/成本与实时路径逐字段一致（同数据双路径断言相等）；孤儿 daily 行被清除；与 §5.5 的 Local 聚合在非跨日窗口内一致（对照原版 RecalcDerived 注释的口径说明）。

### 5.5 AggregateLocal / AggregateHourly（本地口径现算）

```
AggregateLocal(startTs, endEx, offsetMin, rateFn, costFn):
  logs = GetLogsByRange(startTs, endEx)
  foreach l:
    date = LocalDate(l.ts, offset)                 // 本地日界切分（conv.Date 语义）
    hour = LocalFractionalHour(l.ts, offset)       // [C4] 分数小时（原 conv.HourAt 整小时）
    rate = rateFn(key, l.ts, hour, offset)         // offset>0 → Local 平移表匹配
    effTotal = l.total > 0 ? l.total : l.prompt + l.completion   // [S2]+[C3]（原硬编码 p+c → D5-5）
    mul 分量 = Round(x*rate)；mulTotal = mulPrompt + mulCompletion // [C11]（原截断 → D5-6）
    (cny,usd) = costFn(key, l.ts, hour, offset, hit, miss, comp)   // Local 口径冻结（原语义保持）
    累加 (date,provider,model)
  按 (Date, Provider, Model) 升序输出
AggregateHourly(startTs, endEx, offsetMin, rateFn, costFn):
  同上；维度 (本地日期, 本地小时 0–23, provider, model)；无 mul 列；cost 同 Local 口径
  按 (Date, Hour, Provider, Model) 升序输出
```
- 复杂度 O(N)（窗口内行数）；内存 O(组数)。
- 回归要点：跨本地午夜两行归不同 date；total 统一字段生效（[C3]）；total=0 旧行回退（S2）；09:30 请求在 UTC+8 下 hour=9.5 命中半点时段（C4 在聚合侧）；排序确定性。

### 5.6 近 N 天与范围查询（[C7][C9]）+ 查询缓存 [S7]

**近 N 天（两口径恰为 N 个日历日）**：

```sql
-- GetRecentDaysUtc(days)：[C7] 原 "-N days" 返回 N+1 个日历日（off-by-one）
SELECT <daily 全列> FROM usage_daily
WHERE date >= date('now', @offset)
ORDER BY date ASC, provider ASC, model ASC;
-- @offset = $"-{days - 1} days"（days ≤ 0 → 按 1 处理）
```
Local 侧（原 `GetRecentDaysLocal` 已正确，保持）：`startDate = 本地今日 −(days−1)` → `AggregateLocal(LocalDayStart(startDate), LocalDayStart(本地今日)+86400)`。

**范围查询 [C9]**（IStatsQueryService.GetRange / 导出共用换算）：

```csharp
long LocalDayStart(string date, int offsetMin)      // 本地日历 00:00 的 Unix 秒（原 conv.DayRange 等价）
    => new DateTimeOffset(DateTime.ParseExact(date, "yyyy-MM-dd", Invariant), TimeSpan.Zero)
         .AddMinutes(-offsetMin).ToUnixTimeSeconds();

GetRange(start, end, scope):
  scope == Utc   → QueryDailyRange(start, end)                       // 直接按 UTC 日期列，闭区间
  scope == Local → AggregateLocal(LocalDayStart(start), LocalDayStart(end) + 86400, offset, rateFn, costFn)
```
- 原 UI 把自定义日期一律标 "(UTC)" 却把原始日期直送 Local 查询（边界错位至多 offset 小时）[C9]；新实现日期的日历解释由 `scope` 显式决定（对话框按卡片口径标注）。
- **查询缓存 [S7]**：`StatsQueryService` 缓存键 `(方法, days/start/end, scope, offsetMin, pricingRevision)`；失效事件 `ConfigChanged(Pricing|Settings) / DayRolledOver / CalibrateCompleted / ImportCompleted`。原版每 5 秒被前端拉取即全年聚合重扫（S7）→ 新实现按需查询 + 事件失效缓存。
- 回归要点：插 8 天数据 GetRecent(7) 恰 7 行（UTC 与 Local 各测）；UTC+8 下自定义 2026-09-01→09-02，UTC 16:00 前后各造一条，断言 Local 口径归属（C9）；配置变更后缓存失效重查值正确（S7）。

### 5.7 重置 / 删除 [C8]（含 usage_missed）

```sql
-- ResetToday(modelKey?, offsetMin)：窗口 A=UTC 今日，窗口 B=本地今日（铁律 5：必须删 log，否则 RecalcDerived 复活）
DELETE FROM usage_daily WHERE date = @utcToday [AND provider=@p AND model=@m];
DELETE FROM usage_log  WHERE ts >= @aStart AND ts < @aEnd [AND provider=@p AND model=@m];
DELETE FROM usage_log  WHERE ts >= @bStart AND ts < @bEnd [AND provider=@p AND model=@m];  -- A≠B 时第二条必要
DELETE FROM usage_missed WHERE ts >= @aStart AND ts < @aEnd [AND provider=@p AND model=@m]; -- [C8]
DELETE FROM usage_missed WHERE ts >= @bStart AND ts < @bEnd [AND provider=@p AND model=@m];
-- DeleteModelData(modelKey)：三表按 (provider, model) 全时段删除（原只删 daily+log → C8）
DELETE FROM usage_daily  WHERE provider=@p AND model=@m;
DELETE FROM usage_log    WHERE provider=@p AND model=@m;
DELETE FROM usage_missed WHERE provider=@p AND model=@m;
-- DeleteAllData()：三表全清
DELETE FROM usage_daily; DELETE FROM usage_log; DELETE FROM usage_missed;
```
- 模型键切分：`ModelKey.Parse`（第一个 `/`，C19-②；原 `strings.SplitN(key,"/",2)` 语义等价保持）。
- 窗口 A/B 相等（offset=0）时第二条/第四条删除为空操作，幂等。
- 差异 D5-7 [C8]：原版三处删除均不含 `usage_missed` → 卡片残留红框、横幅虚高；新实现同步清理。
- 差异 D5-8（铁律 5）：原 ResetToday 只删 `usage_daily` 当日（log 保留）——C1 修复后重算会"复活"数据；新实现删双窗口 log（01 §4.6，附录 B-6）。
- 每次重置/删除后：`RebuildToday()` + `LogOp(reset_today|delete_model_data|delete_all_data)` + ConfigChanged。
- 回归要点：造漏抓→ResetToday/DeleteModelData/DeleteAllData→`usage_missed` 对应窗口/模型清零、横幅计数归零（C8）；重置后手动 RecalcDerived 不复活（铁律 5）；模型名含 `/` 时三表过滤正确（C19-②）。

### 5.8 备份与回滚 [C6]

```
Backup():
  Flush()                                   -- 快照一致性
  dir = data/backups/（确保存在）
  path = token_monitor_backup_<UTC yyyyMMdd_HHmmss_fff>.db；重名追加 _001.._9999（原语义）
  VACUUM INTO '<path 单引号翻倍转义>'        -- SQLite 不支持绑定参数（原注释保持）
  return path

ReplaceDatabase(backupPath):                -- [C6] 原实现两个坑：不删 -wal/-shm（错 WAL 回放可损坏数据）、
                                            -- sql.Open 用裸路径 DSN（无 busy_timeout/WAL）
  lock (_gate):
    Flush()                                 -- 先把缓冲写入旧库（01 附录 B-12）
    _conn.Close(); _conn.Dispose()          -- 1) 关连接（释放文件锁，触发 WAL checkpoint）
    File.Delete(dbPath + "-wal")            -- 2) 删侧车 [C6-①]
    File.Delete(dbPath + "-shm")
    File.Copy(backupPath, dbPath, overwrite:true)   -- 3) 覆盖主库
    _conn = new SqliteConnection(<与 New() 完全相同的连接串>)  -- 4) 同 DSN 重开 [C6-②]
    Open(); PRAGMA 三连；EnsureTables()      -- 5) 建表幂等
    _buffer.Clear()                         -- 6) 清空内存缓冲（缓冲不属于备份状态）
```
- 回滚上层流程见 §6.3。
- 回归要点：制造 -wal/-shm → 回滚 → 断言侧车被删、库可读、`PRAGMA journal_mode` 返回 wal、数据=备份内容（C6）；回滚后新写入正常落盘（重开连接健康）。

### 5.9 旧库导入转换算法（G1；四表逐列映射）

前置与预览（试连、`PRAGMA table_info` 内省、SHA256、manifest 判重）见 01 §6.1/§6.5。转换核心：

```
Import(options):
  Backup()                                        -- 先备份新库（VACUUM INTO）
  用只读连接打开旧库；tx = 新库 BeginTransaction
  batch = manifest 序号（import_batch）
  -- 表 1：usage_log（唯一全量导入的用量表；列缺失按 §6.3 兼容：total→0 触发回退、estimated→0、reasoning→0）
  foreach row in 旧库 SELECT 全部:
     ts 非法（<=0 或 > now+1d）→ 丢弃并计数 Warn
     provider/model Trim；空 → 丢弃
     prompt  = max(0, prompt_tokens)
     hit     = max(0, prompt_cache_hit)
     miss    = clamp(prompt - hit, 0, prompt)       -- 重算，不信任旧值（旧库可能违反恒等式）
     comp    = max(0, completion_tokens)
     reas    = min(max(0, reasoning_tokens), comp)  -- reasoning ⊆ completion
     total   = max(0, total_tokens); total = total > 0 ? total : prompt + comp   -- [S2] 落库即归一
     INSERT INTO usage_log(ts,provider,model,prompt_tokens,prompt_cache_hit,prompt_cache_miss,
                           completion_tokens,reasoning_tokens,total_tokens,estimated,import_batch)
           VALUES(@ts,@provider,@model,@p,@hit,@miss,@comp,@reas,@total,@estimated原值,@batch)
  -- 表 2：usage_daily → 不导入（铁律 5）：daily 全列可由 log+新 pricing 精确重算；
  --       导入完成后自动 RecalcDerived（[C1] UPSERT 保证 daily 行生成）
  -- 表 3：usage_missed → 1:1 复制 ts/provider/model/status/reason（ts 非法行丢弃）
  -- 表 4：op_log → 1:1 复制 ts/model/action/detail/effective_from（保留旧操作历史，读取恒 500）
  commit；任一步失败 → 回滚（新库保持原状）并抛 ImportException
  收尾：RecalcDerived → RebuildToday → 缓存失效(ImportCompleted) → LogOp("all","import_legacy",…)
```
- `estimated` 语义：原值 0/1 保留（1=当年补录/估算行）——新库该列含义不变，RecalcDerived 不区分对待（同真实行重算）。
- 差异 D5-9：旧 daily 不导入而由 log 重算（原两表独立导入会造成双表口径漂移）；mul_*/cost_* 列 usage_log 本就不存（原 INSERT 仅 9 列），无需清理。
- 复杂度 O(N) 单事务；WAL 下与运行期摄入共存（Store 单锁自然排队）。
- 回归要点：旧库（缺 total_tokens 列 + total=0 行混布）导入后新库 total 全部 >0（S2）；四表行数与清洗规则一致；重复导入被 manifest 拒绝；导入后 UTC 视图可见历史当日数据（依赖 C1）。

### 5.10 每日文件日志（usage_logs）

格式照原 `usagelog.go`（原 usagelog_test 移植锁定）：
- 路径：`data/usage_logs/<LocalDate>_<Provider>_<Model>.log` / `.csv`；文件名非法字符 `/ \ : * ? " < > | 空格 → _`（`sanitizeModel` 保持）。
- .log 行：`[{本地 yyyy-MM-dd HH:mm:ss}] provider=P model=M prompt=… cache_hit=… cache_miss=… completion=… reasoning=… total=… cost_cny=%.7f cost_usd=%.7f`。
- .csv：首行 UTF-8 **BOM** + 表头 `local_time,provider,model,prompt_tokens,cache_hit,cache_miss,completion_tokens,reasoning_tokens,total_tokens,cost_cny,cost_usd`；空文件才写表头（追加不重复）。
- 内部锁串行追加；`Configure(offsetMin)` 时区切换后生效（后续记录按新本地日历切文件）。
- 回归要点：双格式同字段；BOM 与表头唯一性；UTC 23:51 → 本地次日 07:51 文件名（原用例值 2026-08-19 23:51:17Z → 2026-08-20 文件）；含 `/` 的模型名文件名净化（C19-②）。

---

## 6. 手动补录校准（Core.Storage + Coordinator）

### 6.1 均分拆分（余数分配，原语义逐字保持）

原 `splitManualRows`（proxy.go:1351-1383）读出的精确语义：**各分量整数均分，余数全部记入最后一笔**：

```csharp
IReadOnlyList<UsageLogRow> SplitManualRows(string provider, string model,
                                           int count, long hit, long miss, long output)
{
    if (count < 1) count = 1;
    long bH = hit / count,    rH = hit % count;
    long bM = miss / count,   rM = miss % count;
    long bO = output / count, rO = output % count;
    long ts = NowUnix();                                   // 全部同一秒（原语义）
    var rows = new List<UsageLogRow>(count);
    for (var i = 0; i < count; i++)
    {
        var last = i == count - 1;
        long h = bH + (last ? rH : 0), m = bM + (last ? rM : 0), o = bO + (last ? rO : 0);
        long prompt = h + m, total = prompt + o;           // prompt=hit+miss 恒等式；total=prompt+output
        rows.Add(new UsageLogRow(ts, provider, model, prompt, h, m, o, ReasoningTokens: 0, total));
    }
    return rows;
}
```
- 入口校验（原 handleCalibrateApply）：provider/model 非空；count<1 按 1；`hit+miss+output==0` → ArgumentException（"均为 0 无需补录"）。
- `InsertEstimatedUsage(rows)`：**事务直写** `estimated=1`；保留原防御推导：`prompt==0 → hit+miss`、`total==0 → prompt+completion`（补录路径输入已满足，防御针对其它调用方）；任一行失败整批回滚并抛 StorageException。

```sql
INSERT INTO usage_log (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                       completion_tokens, reasoning_tokens, total_tokens, estimated)
VALUES (@ts,@provider,@model,@p,@hit,@miss,@comp,@reas,@total,1);
```

### 6.2 应用流程（备份 → 补录 → 清漏抓 → 重算 → 热重载）

```
ApplyManualCalibrate(provider, model, count, hit, miss, output):   -- 方法级串行锁；重入抛
  rows    = SplitManualRows(...)                                    -- §6.1
  backup  = store.Backup()                                          -- VACUUM INTO（先 Flush）
  store.InsertEstimatedUsage(rows)                                  -- 事务直写 estimated=1
  store.ClearMissedByModel(provider, model)                         -- 清该模型漏抓 → 红框消失（原语义）
  store.RecalcDerived(rateFn, costFn)                               -- [C1] UPSERT：当日无 daily 行也会生成
  coordinator.RebuildToday()                                        -- 热重载两桶（不重启监听；单进程监听本就不动）
  LogOp(key, "manual_calibrate", "补录 count=… hit=… miss=… output=… 备份=…", "")
  持久化 calibrate_state { has_calibrated: true, last_backup: backup }
  bus.Publish(CalibrateCompleted(backup))                           -- 托盘启用"回滚上一轮数据"
  return backup
```
- 与原 `ApplyManualCalibrate` 的差异 D6-1：`reloadAcc`（ResetKeepModels→SetTodayStats→seed→rebuildLocalToday）改为 `RebuildToday()` 整桶替换——修复 S1 的残留态问题，行为等价且更纯净；补录行 ts=now → 落 UTC 今日 daily 行（C1 保证无 daily 行时也生成 → UTC 视图/近 N 天/导出可见，原版该场景静默丢数）。
- `ClearMissedIDs`（按主键精确清除）不用于此流程（原实现即用 ByModel），保留接口用于将来按列表校准——文档化保持。
- 复杂度：O(N) 重算为主（全量 RecalcDerived）。
- 回归要点：端到端——当日无任何 daily 行的模型补录 3 笔（hit=10, miss=0, output=7, count=3 → 行 [(4,0,2),(4,0,2),(2,0,3)]，prompt=4/4/2，total=6/6/5）→ usage_log 3 行 estimated=1、daily 行生成、UTC 视图/快照可见、漏抓清零（C1+§6 联合回归）；余数分配断言（上例）；回滚可完整撤销。

### 6.3 回滚

```
RollbackLastCalibration():
  state = 读 calibrate_state.json
  if !state.HasCalibrated || state.LastBackup == "" → InvalidOperationException("尚无上一轮校准记录")
  if !File.Exists(state.LastBackup)  → InvalidOperationException("备份文件不存在")
  store.ReplaceDatabase(state.LastBackup)      -- §5.8：关连接→删侧车→覆盖→同 DSN 重开 [C6]
  coordinator.RebuildToday()
  持久化 { has_calibrated: false, last_backup: "" }（该轮备份已消费；再次回滚前需新校准——原语义）
  LogOp("all", "rollback_calibrate", "回滚到 " + backup, "")
```
- 回归要点：回滚后 DB 内容与备份逐表一致、侧车已删、快照回滚可见；二次回滚抛异常；备份缺失抛异常。

---

## 7. XLSX 导出（IExportService，MiniExcel）

### 7.1 Sheet、列定义与数据来源（照原 `ExportToXLSX/write*Sheet` 逐列对齐）

Sheet 固定顺序：`UTC总表 / UTC明细表 / LOCAL总表 / LOCAL明细表 [+ HOUR用量 / HOUR消费]`（IncludeHourly 时追加后两张；无默认 Sheet1）。

| Sheet | 表头（列序固定） | 数据来源 |
|---|---|---|
| UTC总表 | 厂商,模型,输入Token,缓存命中,缓存未命中,输出Token,推理Token,总Token,请求次数,消费(元),消费(美元) | `usage_daily` 全期（`2000-01-01..2099-12-31`，原 handleExport 边界保持）→ 按 (provider,model) 分组累加；成本=组内 CostCNY/USD 累加 |
| UTC明细表 | 日期,厂商,模型,输入Token,缓存命中,缓存未命中,输出Token,推理Token,总Token,请求次数,消费(元),消费(美元) | `usage_daily WHERE date∈[start,end]`（UTC 口径闭区间），行序 (date,provider,model) |
| LOCAL总表 | 同 UTC总表 表头 | `AggregateLocal(全期, offset)` → 同法分组 |
| LOCAL明细表 | 同 UTC明细表 表头 | `AggregateLocal(本地窗口[start,end], offset)`（[C9] 换算见 §5.6） |
| HOUR用量 | 开始时间,结束时间,厂商,模型,请求次数,输入Token(缓存命中),输入Token(缓存未命中),输出Token,推理Token,总Token | `AggregateHourly(本地今日全天, offset)`；`开始/结束 = "<date> HH:00" 与 +1h`（格式 `yyyy-MM-dd HH:mm`，原 `hourRange` 保持） |
| HOUR消费 | 开始时间,结束时间,厂商,模型,消费(元),消费(美元) | 同上（每段每模型金额） |

- 行序：总表按 (provider, model) 升序——原版遍历 Go map 顺序随机（D7-1 确定化，理由：可测试性与人工核对友好；列内容不变）；明细/HOUR 表按原排序保持。
- 列宽：A-B=12、C-I=14、J-K=12（原 SetColWidth 保持，MiniExcel 以 ColumnWidth 属性实现）。
- 差异 D7-2：原实现把成本经 `buildCostMaps`（key=`p/m` 与 `p/m|date`）二次映射写入；新实现 `ModelDailyAggregate` 行已含成本，直接写行值（数值等价，去中间映射）。
- Local 侧成本/倍率在聚合时按本地分数小时冻结（§5.5），导出零再计算。
- 文件：`data/export/token_usage_{modelTag|all}_{start}_{end}_{yyyyMMdd_HHmmss}.xlsx`（modelTag=模型键中 `/`→`-`，原语义）；同步 IO，调用方必须放后台线程。

### 7.2 "今日"口径统一 [C19-①]

- 原实现：托盘"导出今日"把 **UTC 今日**同时传给 UTC 与 LOCAL 明细表（LOCAL 表窗口错位 ≤offset 小时），而 HOUR 表用**本地今日**——同一动作三种"今日"基准。
- 新统一规则：日期窗口的日历解释由 `ExportRequest.RangeScope` **显式**随请求传入；"今日/本月/近 7 日"预设按请求口径解析各自日历的今日（UTC 口径→UTC 今日；Local 口径→本地今日）；HOUR 表恒为**本地今日**（其维度即本地小时，无 UTC 语义）。托盘快捷导出保持 UTC 日历传参（01 附录 B-13）；导出对话框按用户所选口径（C9 同规则）。文件内各 Sheet 名已含 UTC/LOCAL/HOUR 标识口径。
- 回归要点：UTC+8 下"导出今日"——UTC 表含 UTC 今日行、LOCAL 明细表含本地今日行（两窗口不同步时各含各自"今日"）、HOUR 表段首=本地 00:00（C19-①）；HOUR 表仅 IncludeHourly 时存在；明细表成本列与快照冻结值一致。

---

## 8. 并发与同步模型

### 8.1 锁分层清单（每把锁的保护对象）

| 锁 | 类型 | 保护对象 | 调用方 |
|---|---|---|---|
| `UsageCoordinator._gate` | `object`（可重入同线程） | **摄入与整桶重建互斥** [C5 核心锁]：Ingest 全流程 / RebuildToday / Replace 桶序列 | 代理捕获、守望、Engine 命令 |
| `Accumulator._gate` | `object` | `_index/_order`、全部 `ModelAccumulator` 双桶字段 | 仅 Coordinator（快照构建走其读路径） |
| `PricingEngine._rw` | `ReaderWriterLockSlim` | 文档引用、`_effMode/_offsetMin`、`_shiftCache`；Update*/Replace/SetEffectiveContext 写锁 | 全部读者（叶子锁） |
| `Store._gate` | `object` | SQLite 单连接使用、`_buffer`、Backup/ReplaceDatabase 全程 | 仅 Store 公开方法（叶子锁，除 rateFn 委托） |
| `UsageFileLogger._gate` | `object` | 当前 offset、文件追加序 | 仅 Logger（叶子锁） |
| `EventBus` 每主题队列锁 | `object` | 有界队列/合并槽 | 叶子锁；发布方在锁外组装载荷 |
| `ConfigService._saveLock` | `SemaphoreSlim(1,1)` | 三个 JSON 文件原子写 | 保存路径 |
| `StatsQueryService._cacheLock` | `object` | 查询结果缓存 [S7] | 查询路径 |

### 8.2 时区/计价变更协议（锁内快照交换 + DB 回放 [C5]）

```
SetTimezone(offsetMin):                          -- UI 线程调用，内部转后台
  1. settings.OffsetMin = offsetMin 落盘
  2. pricing.SetEffectiveContext(effMode, offsetMin)   -- 写锁内：换 offset、清平移缓存 [S3]
  3. coordinator.RebuildToday()                   -- 串行锁内：Flush→按新 offset 读库→原子换两桶→补骨架
     （重建期间 Ingest 在锁上排队；恢复后写入新桶——无丢失、无旧桶残留）
  4. bus.PublishCoalesced(DayRolledOver + ConfigChanged(Settings))
SetEffectiveDateMode(mode):
  1-2 同上（mode 归一化；与现值相同 → 直接返回，原 setEffectiveDateMode 防御保持）
  3. 后台 store.RecalcDerived(rateFn, costFn)     -- 版本生效日期日历变了，daily 必须重算
  4. coordinator.RebuildToday() → 事件广播
倍率/计价变更（Update*）:
  pricing 写锁内换文档/版本 → 释放锁后触发 Changed → Engine：
  后台 RecalcDerived → RebuildToday → ConfigChanged(Pricing)
  （原版 sleep 400ms 再重算——D8-1 移除：新架构重算与摄入经 Store/Accumulator 锁自然串行，无竞态窗口）
```
- 原实现对照（C5 全部三个子问题）：①在途请求写旧桶 → 新协议下 Ingest 与 Rebuild 互斥，排队回放；②全局变量无锁读写 → 全部状态经 §8.1 锁保护；③重建期间代理重启 → 单进程不重启监听，捕获管线全程不断。
- 回归要点：确定性测试——T1 持 Coordinator 锁注入一笔事件（测试钩子停在桶写入中点），T2 调 RebuildToday 阻塞至 T1 完成；断言 T1 事件计入且重建数据完整。并发压测——N 线程持续 Ingest 同时切时区 K 次 → 快照累计 == 注入总量（无丢失）、无死锁（限时完成）。

### 8.3 UI 消费线程切换

- 事件总线 `EventDispatch.UiThread`：订阅时捕获 `SynchronizationContext`（必须在 UI 线程订阅），分发经 `SynchronizationContext.Post` → WPF Dispatcher 队列；`BackgroundPool` 直接线程池。
- ViewModel 只消费不可变 `StatsSnapshot`/`ModelDailyAggregate`，不持有可变共享态；异步回包（范围查询/导出）落地前校验目标卡片键 [C16]。
- 长工作（导出/导入/重算）一律后台线程；UI handler 快速返回（总线契约，01 §5）。

### 8.4 Graceful shutdown 顺序 [S5]

```
ITokenMonitorEngine.StopAsync():                    -- 托盘"退出"/系统关机/致命异常共用
  1. 停 StatsTicker(200ms/1s) 与 DayWatch（先断 UI 数据流）
  2. proxy.Stop(gracefulTimeout: 10s)：停止接受新连接 → 等待 InFlightCaptures 归零或超时
     → 取消捕获 CTS（在途 SSE 续读随 30 分钟上限 CTS 链接中止）→ HttpListener.Stop
     （原 Server.Close() 掐断在途响应 [S5] → 改为优雅等待 + 超时兜底）
  3. store.Flush() → store.CheckpointWal()（PRAGMA wal_checkpoint(TRUNCATE)）
  4. 强制落盘 ui_state/settings 防抖队列
  5. 托盘 Dispose → 关闭悬浮球/面板窗口
  6. store.Dispose() → 释放单实例 Mutex/EventWaitHandle
  7. 3s 内托管线程未结束 → 记日志后 Environment.Exit(0) 兜底
```
- 回归要点：在途捕获进行中 Stop → 等待其完成（usage 不丢）；超 10s 强制中止且 Flush 已落库；WAL checkpoint 后 -wal 文件收缩。

### 8.5 锁序防死锁规则

1. **叶子锁**（不调用任何其它组件）：`PricingEngine`、`UsageFileLogger`、`EventBus`、`ConfigService`、`StatsQueryService`。
2. **允许嵌套方向**：`Coordinator → Accumulator → Pricing`；`Coordinator → Store → Pricing`（rateFn/costFn 委托在 Store 锁内回调 Pricing）；`Coordinator` 对 Accumulator/Store/Logger **顺序调用、即取即放**（Ingest：报价(Pricing)→Accumulator→Store 缓冲→FileLogger，各段独立持锁）。
3. **禁止**：Pricing 反向调用任何组件；Accumulator↔Store 互相嵌套；任何组件持锁时**发布事件**（Pricing.Changed 在写锁释放后触发；Coordinator 的 DayRolledOver/ConfigChanged 在 RebuildToday 返回后发布）；事件 handler 内同步回调 Engine 命令（只允许转后台任务）。
4. Store 锁内不做网络/文件 IO（Backup 的 VACUUM INTO 与 ReplaceDatabase 除外——独占场景，已与摄入互斥）。
- 回归要点：压力测试（Ingest × Recalc × 备份 × 时区切换交错）限时完成无死锁；CI 中以循环次数 1000 的确定性交错测试守护。

---

## 9. 回归测试矩阵总表

### 9.1 Bug 回归（C1–C19 + S1–S7；实施代理照此写 xUnit，不得遗漏）

| 编号 | 测试方法（`TokenMonitor.Core.Tests`） | Arrange / Act / Assert 要点 |
|---|---|---|
| C1 | `Storage_RecalcDerived_UpsertsRowsMissingFromDaily` | A：仅写 usage_log（无对应 daily 行，含跨多日多模型）；A：RecalcDerived；Assert：daily 行生成且 15 数值列与逐行公式一致 |
| C1(端到端) | `Calibrate_ApplyIntoEmptyDay_VisibleInUtcView` | A：干净库+计价配置；A：ApplyManualCalibrate(当日无 daily 行)；Assert：usage_log 有 estimated=1 行、daily 行生成、GetTodayUtc/快照/导出均可见 |
| C2a | `Proxy_StreamFalse_RespondsJson_AndCapturesUsage` | A：上游返回 JSON+usage；A：发 `stream:false` 请求经代理；Assert：客户端收到 `application/json` 原文、usage 已入快照、请求体未被改写（上游收到的 body 不含 stream_options） |
| C2b | `Proxy_NonJsonBody_PassthroughNoParseNoCapture` | A：multipart body POST /v1/audio/transcriptions；Assert：200 透传、上游收到原始 body、无捕获无漏抓记录 |
| C2c | `Proxy_StreamTrue_InjectsIncludeUsage_KeepsStreamValue` | A：`stream:true`；Assert：上游收到 `stream_options.include_usage=true` 且 `stream` 仍为 true、其余字段保留 |
| C3 | `Parser_TotalUnification_AllConsumersUseSameField` | A：usage `total=999, prompt+completion=1100`；A：AddUsage→两桶、daily、AggregateLocal、Export；Assert：所有"总Token"==999（Local 桶亦 999） |
| C4a | `Pricing_FractionalHour_0929_FallbackRate_0930_NewRate` | A：时段 [9.5,12.5) rate=2；A：ts=09:29 与 09:30 各一笔；Assert：09:29→1.0、09:30→2.0 |
| C4b | `Pricing_HourBoundaries_MidnightAndFullDay` | A：时段 [0,0.5) rate=3 与 [23.5,24) rate=4；Assert：00:00→3、00:29→3、00:30→1.0、23:59→4 |
| C4c | `Pricing_HalfHourTimezone_Utc530_MatchesShiftedBoundary` | A：offset=330、时段 [9.5,12.5)；A：UTC 04:00（本地 09:30）；Assert：rate=2.0（UTC 03:59→1.0） |
| C4d | `Aggregators_UseFractionalHour` | A：09:29/09:30 各一行 log + 9.5 时段；A：RecalcDerived + AggregateLocal；Assert：mul_* 分属 1.0/2.0 段，两口径一致 |
| C5 | `Coordinator_ConcurrentIngestAndRebuild_NoLossNoDeadlock` | A：N 线程持续 Ingest，同时 K 次 SetTimezone/RebuildToday（限时）；Assert：注入总量==快照累计、无死锁 |
| C5b | `Coordinator_IngestBlockedDuringRebuild_ReplayedToNewBuckets` | A：钩子令 Ingest 停在中点，触发 RebuildToday；Assert：Rebuild 等待 Ingest 完成，事件计入新桶一次 |
| C6 | `Storage_ReplaceDatabase_RemovesWalShm_ReopensWithSameDsn` | A：写数据不 checkpoint 制造 -wal；A：Backup→变更→ReplaceDatabase；Assert：侧车删除、数据==备份、`journal_mode`==wal、可继续写 |
| C7 | `Storage_GetRecentDaysUtc_ExactlySevenRows` | A：插 8 个 UTC 日数据；Assert：GetRecentDays(7) 恰 7 个日期（最早一日为 today-6） |
| C7b | `Storage_GetRecentDays_BothScopesSameWindow` | A：同上（UTC+8 本地日错位数据）；Assert：Local 侧亦恰 7 个本地日 |
| C8a | `Storage_DeleteModelData_ClearsUsageMissed` | A：造 daily/log/missed 三表数据；A：DeleteModelData；Assert：三表该模型全空、其余模型不受影响 |
| C8b | `Storage_DeleteAllData_ClearsUsageMissed` | 同上全表断言 |
| C8c | `Storage_ResetToday_ClearsMissedDoubleWindow` | A：UTC 今日 23:00 与本地今日（跨 UTC 日界）各造 log+missed；A：ResetToday；Assert：双窗口 log/missed 清零、daily 当日清零、RecalcDerived 不复活 |
| C9 | `Query_GetRange_LocalScope_BoundaryAttributionUtcPlus8` | A：UTC 2026-08-31 16:30 与 17:30 各一笔；A：GetRange("2026-09-01","2026-09-01",Local,offset=480)；Assert：两笔均落入 09-01（UTC 口径查 09-01 则不含第一笔） |
| C10 | `HttpSurface_UnknownPathAndApiPaths_Return404` | A：启动引擎；Assert：GET /api/stats、/api/shutdown、/foo → 404；/health→200；/v1/models→列表 |
| C11 | `Pricing_MulRounding_MulTotalEqualsSumOfComponents` | A：rate=0.55、prompt=101、completion=1；Assert：mulPrompt=56、mulCompletion=1、mulTotal=57==分量和（原截断值 56 被否定）；随机 100 组 (x,rate) 性质断言 |
| C12 | `Storage_Flush_RowFailure_SkippedEntirely_OthersCommit` | A：注入一行触发约束失败（测试替身/非法 provider 为 NULL 不可行时用 fail-on-second 注入命令）；Assert：失败行不入 log 也不入 daily、其余行提交；Commit 失败路径整批回队且重试后无重复 |
| C13 | `Pricing_Normalize_LegacyFlat_Per1K_DuplicateEf` | A：旧平铺+per-1K+重复 effective_from JSON；A：Load/Normalize；Assert：history 化、×1000、同 ef 留后值占首位并回写文件 |
| C14a | `Proxy_UnknownModel_NoDefault_Returns404` | A：providers 不含前缀匹配、default=null；Assert：404、上游未收到请求、密钥未外发 |
| C14b | `Proxy_UnknownModel_DefaultProvider_Routes` | A：default_provider="GLM"；Assert：路由 GLM、Authorization=GLM 密钥 |
| C15 | `Proxy_StripParams_PerProvider` | A：provider A strip=[e]、B strip=[]；Assert：A 剥 `enable_thinking` 不剥 `reasoning_effort`；B 两者都保留 |
| C16 | `RangeDialog_AsyncResult_ValidateCardKey`（App.Tests） | A：切卡后回包旧卡结果；Assert：不写入新卡选中态 |
| C17 | `Events_TimezoneChange_InvalidatesCaches` | A：查询缓存已填充；A：SetTimezone；Assert：ConfigChanged/DayRolledOver 发布、再查返回新 offset 结果 |
| C18 | （不适用） | 死代码（TeeReaderSplit/ScanSSEData）未移植；以"Parser 程序集不含该类型"编译期保障，无运行时测试 |
| C19① | `Export_Today_ScopeExplicitPerSheet` | 见 §7.2 回归要点 |
| C19② | `ModelKey_ParseSplitsOnFirstSlash` | A："P/openai/gpt-4"；Assert：Provider="P"、Model="openai/gpt-4"；文件名净化不含 `/` |
| C19③ | `Capture_SseOver30Min_RecordedMissedWithCapReason` | A：上游挂起不发 usage、模拟 30min CTS 到期（注入短时限）；Assert：missed 入表 reason="超过 30 分钟读取上限"、status=200 |
| C19④ | `Proxy_Start_PortBusy_PrecheckFailsBeforeBind` | A：占用端口；Assert：Start 抛 ProxyStartException、StateChanged(false,error)、进程存活 |
| S1 | `Accumulator_ReplaceUtcBucket_FullReplaceResetsDeltaAndLeavesLocal` | A：注入含 Delta 的两桶数据 + Local 值；A：ReplaceUtcBucket(空列表)→ReplaceUtcBucket(行)；Assert：Delta=0、UTC 值==行值（不重报价）、Local 桶不变 |
| S2a | `Storage_RecalcDerived_ZeroTotal_FallsBackPromptPlusCompletion` | A：log 行 total=0；Assert：daily total=prompt+completion、mul/cost 正常 |
| S2b | `Storage_AggregateLocal_ZeroTotal_FallsBack` | 同上 Local 侧 + 导入路径（§5.9）落库后 total>0 |
| S3 | `Pricing_ShiftCache_InvalidatedOnOffsetAndConfigChange` | A：构建计数器；Assert：同 (model,版本，offset) 二次报价零构建；offset/Update*/SetEffectiveContext 后重建；不同生效日期各版本各建各表 |
| S4 | `Snapshot_MissedCaptures_SumsAllModelsIncludingCardless` | A：模型 X 无卡片（不在累积器）但有 3 条 missed；Assert：MissedCaptures 含 3、横幅总数正确 |
| S5 | `Proxy_Stop_GracefulWaitsInflightThenAborts` | A：在途慢捕获；A：Stop(10s)；Assert：等待完成不丢 usage；Stop(50ms) 超时强制中止且进程可退出 |
| S6 | `Config_EmptyProviders_MergedDefaultsWarn` | A：config.json providers=[]；Assert：合并默认 4 项+告警日志、listen_addr/default_provider 保留、监听正常 |
| S7 | `QueryCache_ResultsCached_InvalidatedByEvents` | A：同参两次 GetRecentDays；Assert：第二次命中缓存（查询计数=1）；CalibrateCompleted/ConfigChanged 后计数重置且值刷新 |

### 9.2 移植的原 Go 测试对照（语义回归，防移植走样）

| 原测试 | 新测试方法 | 锁定语义 |
|---|---|---|
| parser_test 全部 | `Parser_ParseSseDataLine_*`、`Parser_DashScopeAlias_*` | usage 块识别、miss 推导、畸形行 null、DashScope 别名+total 推导 |
| proxy_test TestInterceptSSE* 两例 | `Proxy_Sse_*`（§1.5 回归要点） | 断开后继续捕获、正常流转发捕获 |
| proxy_test TestUpstreamRequestSent 五例 | `Proxy_UpstreamRequestSent_Classification` | DNS/拨号拒绝/不可达=未送达；读写中断=已送达 |
| usagelog_test | `UsageFileLogger_DualFormat` | 双格式、BOM、表头唯一、字段序 |
| pricing_test 版本链四例 | `Pricing_VersionSelection_*` | 生效日选取、空 ef 恒生效、最近保存优先、upsert 移尾、去重 |
| pricing_test TestCostByWeekday/DefaultAll | `Pricing_Weekday_*` | 星期匹配与空 Days |
| stats_test 两个 Orchestrated | `Stats_CostPerView_*`、`Stats_MultiplierPerView_*` | 两口径时段平移+分数小时下的瞬时属性（含跨午夜回归点） |
| （新增）`Storage_UpsertDaily_ConflictAccumulates` | — | §5.2 SQL 与原逐列一致（两笔同键累加、request_count+1） |

---

## 附录：差异清单总索引（含理由，正文章节为准）

| 编号 | 一句话 | 章节 |
|---|---|---|
| D1-1 | HTTP 面收窄为 /v1/*、/v1/models、/health | §1.1 |
| D1-2 | 缺 CT 且 body 首字符 `{` 视为 JSON | §1.1 |
| D1-3 | 非 JSON body 透传不 400 | §1.1 [C2] |
| D1-4 | hop-by-hop 头显式剥离 | §1.4 |
| D1-5 | SSE 宽容 `data:` 无空格前缀 | §1.5 |
| D1-6 | 30 分钟上限漏抓 reason 显式文案 | §1.5/§1.7 [C19-③] |
| D1-7 | 429 不入 usage_missed（原码语义文档化） | §1.7 |
| D1-8 | 取消 CORS 中间件 | §1.8 [C10] |
| D2-1 | reasoning 新增顶层别名 | §2.1 |
| D2-2 | hit>prompt 防御、miss 钳制 | §2.1 |
| D2-3 | Local 聚合读统一 total 字段 | §2.2 [C3] |
| D2-4 | 字段级类型容错 | §2.3 |
| D3-1..7 | 桶统一 total/Round/整桶替换/漏抓求和/锁内重建/事件推送/漏抓缓存 | §3.7 |
| D4-1..5 | 全表合并/分数小时/平移缓存/签名收口/对话框共用 | §4.7 |
| D5-1 | flush 单事务+失败行整行跳过 | §5.1 [C12] |
| D5-2 | flush 显式写 estimated=0 | §5.1 |
| D5-3 | GetLogsByRange 带 total 列 | §5.3 [C3/S2] |
| D5-4 | op_log 写侧裁剪 5000 | §5.3 |
| D5-5/6 | Local 聚合 total 回退+Round | §5.5 [S2/C3/C11] |
| D5-7 | 重置/删除含 usage_missed | §5.7 [C8] |
| D5-8 | ResetToday 删双窗口 log | §5.7（铁律 5） |
| D5-9 | 旧 daily 不导入、由 log 重算 | §5.9 |
| D6-1 | 补录热重载改整桶替换 | §6.2 [S1/C1] |
| D7-1 | 总表行序确定化 | §7.1 |
| D7-2 | 成本直写行值（去 cost map） | §7.1 |
| D8-1 | 移除 400ms sleep | §8.2 |
