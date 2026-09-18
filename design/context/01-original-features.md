# 原项目功能与数据完整清单（移植依据）

> 原项目：`D:\GLM Workspace\Projects\Token Calculator\neon-token-monitor`（Go 3 进程架构）。
> 本文档是"要移植什么"的唯一权威清单；设计时如需精确细节请直接读原 Go 源码（路径见文末索引）。

## 1. 原架构概览（3 进程，新项目改为单进程）

| 原进程 | 技术 | 职责 | 新项目归属 |
|---|---|---|---|
| ntm-proxy.exe | Go | 本地反向代理 127.0.0.1:8280 + 托盘 + SQLite 数据源 | TokenMonitor.Core.Proxy |
| neon-token-monitor.exe | Go + Wails v2 + Vue3 | 无边框置顶 HUD 面板，200ms 轮询 stats 推送前端 | TokenMonitor.App 主窗口 |
| ntm-ball.exe | C# WPF | 悬浮球：1s 轮询 /api/stats | TokenMonitor.App 悬浮球窗口 |

## 2. 端到端数据流（必须完整复刻）

1. LLM 客户端（ZCode/ChatBox/Cursor 等）把 baseURL 指向 `http://127.0.0.1:8280/v1/...`。
2. 代理读请求体，按 `model` 前缀匹配提供商（base_url + api_key，Authorization 重写），注入
   `stream_options:{include_usage:true}`、钳制 `max_tokens`、剥离部分厂商不支持的参数。
3. `httputil.ReverseProxy` 转发；自定义 transport 拦截上游响应：
   - SSE 流：逐行边转发给客户端、边解析；**客户端断开后仍继续读完上游流直到最终 usage 块**（30 分钟上限）——计费数据不丢。
   - JSON：缓冲解析后回放。
4. parser 把多厂商 usage 归一化成 UnifiedUsage（见 §5）。
5. stats.Accumulator 把事件累积进 UTC 与 Local（UTC+offset）两个"当日桶"，各含原始 token、
   倍率调整后 token、冻结的 CNY/USD 成本。**归属时间 = 响应完成时间**（跨午夜与厂商账单一致）。
6. 持久化：每笔原始请求追加 `usage_log` 表 + 批量 upsert `usage_daily`（3 条或 5 秒刷一次，WAL）；
   同时追加 `data/usage_logs/YYYY-MM-DD_Provider_Model.log` 与 `.csv`。
7. UI 消费实时快照（原 200ms 轮询；新架构改为进程内事件总线推送）。

## 3. 完整功能清单

### A 核心代理引擎（Core.Proxy）
- A1 本地 OpenAI 兼容反向代理，监听 `127.0.0.1:8280`（listen_addr 可配）。
- A2 多提供商路由：`model` 前缀 → provider（name/base_url/api_key/model_prefix[]/max_tokens?）。
  未匹配模型：原实现静默落到第一提供商（**BUG C14**，新实现必须拒绝或可配默认）。
- A3 SSE 流式捕获：请求含 `stream:true` 时注入 `stream_options.include_usage`；
  逐行解析 SSE 直到最终 usage 块；客户端断开继续读（goroutine→C# task 分离，30 分钟上限计时）。
  原实现强制 `stream=true` 覆盖非流式请求（**BUG C2**，新实现必须尊重客户端流偏好）。
- A4 非流式 JSON 捕获：缓冲完整响应解析 usage 后原样回放（容忍部分读）。
- A5 请求体整备：剥离 `enable_thinking`/`reasoning_effort`（原对所有提供商生效 **BUG C15**，
  新实现改为按提供商配置 `strip_params`）；`max_tokens` 钳制到提供商上限。
- A6 漏抓统计：2xx 但无可用 usage 块（或上游发送后中断）→ `missed_captures` 计数 + `usage_missed`
  表记录原因（429 标"限流"；区分"未发出上游"与"发出后中断"）。
- A7 `/v1/models`：由提供商 model_prefix 合成 OpenAI 风格模型列表；`/health` 存活检查。
- A8 泛 panic/recovery 保护（单笔请求异常不得拖垮进程）。

### B 统计与计价（Core.Stats / Core.Pricing）
- B1 双口径双桶：UTC 桶 + Local 桶（UTC+offset_min），互不干扰同时累计。
- B2 每桶字段：请求数、prompt/cache-hit/cache-miss/completion/reasoning/total、
  倍率调整后同组字段、冻结成本（CNY+USD）、实时增量 DeltaTokens。
- B3 token 倍率引擎：按模型的分时段倍率表，0.5h 粒度（start/end 可为 9.0/9.5/…）。
  原实现只用整数小时匹配（**BUG C4**：9.5 起的时段 9:00-9:59 全部漏配 → 新实现必须传分数小时）。
  倍率应用于 token 数（显示"倍率值"，原始值同时保留），可按模型开关（multiplier_states）。
- B4 计价引擎：按模型的分时段+分星期单价规则（`start,end` 0.5h 粒度、`days?` 1=周一…7=周日），
  三种单价：`input_per_1m`（未命中）/`cache_per_1m`（命中）/`output_per_1m`，货币 CNY/USD。
  每笔请求按完成时刻冻结成本。无规则时显示"不计价/Coding Plan 套餐"。
- B5 版本化配置：multiplier 与 pricing 均为 `history:[{effective_from,...}]` 版本链，
  按生效日期取"最新生效版本"；加载时去重、自动迁移旧格式（旧 `{periods}` 平铺、旧 per-1K 价→per-1M）。
- B6 生效日期基准模式（effective_date_mode: "utc"|"local"）：决定版本生效日期按哪个日历解释；
  Local 口径下时段/规则窗口按 offset 平移跨午夜（含星期平移，见原 `shiftPeriodsToLocal`/`shiftPriceRulesToLocal`
  与前端 `tzShift.js`——支持 30/45 分钟时区如 UTC+5:30，前端有参考实现 `mergeAdjacent`）。
  （性能：原实现每请求重算平移表 **S3**，新实现按 (模型,offset) 缓存并在配置变更时失效。）
- B7 计费归属：响应完成时间（跨午夜归入正确日期）。
- B8 跨午夜守望：UTC 或本地日期变更时从 DB 重建当日两桶（原 30s 轮询）。
- B9 时区切换：UTC−12:00…UTC+14:00 每 30 分钟步进；切换后两桶按新 offset 全量重建。
  原实现重建期间在途请求写入旧桶+全局变量竞争（**BUG C5**）→ 新实现：锁内快照交换 + 间隙回放。
- B10 配置变更热重算：倍率/计价变更后异步全量重算 `usage_daily`（RecalcDerived）。
  原实现仅 UPDATE 不 INSERT（**BUG C1**）→ 新实现必须 UPSERT。

### C 存储（Core.Storage）
- C1 SQLite（新项目用 Microsoft.Data.Sqlite）表：
  - `usage_log`：每笔请求原始记录（含 estimated 标志；列语义对齐原库，便于导入——精确 DDL 读原 `storage.go`）。
  - `usage_daily`：PK(date,provider,model)，原始+倍率+成本聚合列（UTC 口径）。
  - `usage_missed`：漏抓记录（date/provider/model/count/reason）。
  - `op_log`：操作日志（时间/动作/模型/详情，保留最近 500）。
- C2 WAL + busy_timeout；批量刷写（攒 3 条或 5 秒）；失败行重排重试。
  原实现失败行仍进 daily 聚合（**BUG C12**）→ 新实现跳过失败行。
- C3 本地口径聚合不落库：`AggregateLocal`/`AggregateHourly` 从 `usage_log` 现算。
- C4 每日文件日志：`data/usage_logs/YYYY-MM-DD_Provider_Model.log`（人类可读）+ `.csv`（UTF-8 BOM，本地时间）。
- C5 XLSX 导出（MiniExcel）：Sheet = UTC总表/UTC明细表/LOCAL总表/LOCAL明细表（范围导出）+ HOUR用量/HOUR消费（小时导出），
  写入 `data/export/`，与厂商官方用量导出模板字段对齐。
- C6 手动补录校准：先 `VACUUM INTO` 备份到 `data/backups/` → 把输入的总量均分为 N 笔
  `usage_log` 行（estimated=1）→ 清该模型漏抓记录 → RecalcDerived → 热重载累积器（不重启监听）。
- C7 校准回滚：恢复最近备份。原实现覆盖 db 后不删 `-wal/-shm` 侧车且连接串不一致（**BUG C6**）→ 新实现必须处理。
- C8 数据重置/删除：重置今日（全部/按模型）、按模型删除、全部删除。
  原实现不清理 usage_missed（**BUG C8**）→ 新实现同步清理。
- C9 近 N 天查询：原实现 UTC 侧 off-by-one（**BUG C7**：`-(days)` 应为 `-(days-1)`）→ 新实现修正且两口径一致。
- C10 旧库兼容：`total_tokens` 列为 0 的历史行，聚合时回退 prompt+completion（原 **S2**）。

### D 主面板（信息架构不变；⚠️ 视觉由 UI-Designer 全新原创，不参照原样式）
- D1 无边框置顶小部件窗口：拖拽移动、窗口透明度可调、位置尺寸记忆、PerMonitorV2 DPI。
- D2 模型卡片网格：提供商徽章+模型名、请求数、实时增量(+N)、总 Token、
  H(缓存命中)/M(未命中)/O(输出) 三条占比条、漏抓红色标记（"漏N"）。
- D3 卡片级倍率切换：实际值/倍率值二态。
- D4 卡片级口径切换：双击切 UTC/LOCAL（边框/徽章随口径变色）。
- D5 详情侧栏（选中卡片）：环形图（缓存命中率%）、四项统计行（命中/未命中/输出/推理）、
  近 7 日总 Token 折线图（悬停 tooltip；倍率模式下画 mul_total）、
  成本条（¥ 与 $ 各 4 位小数；无计价显示"不计价/Coding Plan 套餐"）、UTC+LOCAL 双时钟条（每秒）。
- D6 卡片数据范围：预设（本日/本周/近7日/本月/上月/本季度/本年）+ 自定义日期（起止）。
  原实现自定义日期标"(UTC)"实按本地换算（**BUG C9**）→ 新实现按卡片口径正确换算。
- D7 右键菜单三层：卡片（倍率配置/计价配置/手动补录/操作日志/导出数据/打开配置文件/重置今日/退出）、
  模型名（刷新数据+范围预设）、标题栏（显示隐藏卡片/操作日志全部）。
- D8 对话框 7 个：倍率配置（时段行+0.5h 选择+倍率+生效日期+当前/下一版本展示；Local 口径编辑时
  UTC↔Local 转换）、计价配置（货币+规则卡：时段+星期 chips+三单价+生效日期）、手动补录
  （模型选择/笔数/命中/未命中/输出 token，total 自动合计）、操作日志（列表，按模型或全部）、
  导出（范围预设+自定义+模型范围）、时间范围（预设+自定义）、显示隐藏卡片（勾选+拖拽排序+删除选中+二次确认）。
- D9 漏抓横幅：存在漏抓时顶部红色警示"N 个漏抓请求，请校准补录"。
- D10 流量感应动效：流量激增时环境动效加速（原为粒子，形态由新 UI 设计决定）。
- D11 UI 状态持久化：窗口位置尺寸、卡片顺序（拖拽）、隐藏卡片、每卡片口径、倍率开关。

### E 托盘（Core/App.Tray）
- E1 菜单全项：状态+监听地址（禁用项）、打开面板、打开配置文件、倍率配置、计价配置、
  统计时区子菜单（UTC−12…+14 每 30 分钟，当前项勾选）、生效日期基准切换（UTC/Local 勾选）、
  导出（今日/本月/近7日 + 今日小时明细）、打开导出目录、手动补录、校准回滚、重载配置
  （原"重启代理"，单进程下改为重读配置重建监听）、开机自启（勾选）、悬浮球子菜单（透明度 40/60/80/100、置顶开关）、
  导入旧数据（新增）、退出。
- E2 开机自启：注册表 HKCU\...\Run。

### F 悬浮指示窗（视觉由 UI-Designer 设计）
- F1 悬浮球：透明分层窗口、桌面边缘吸附、拖拽、实时轮播各模型统计（1s）、跑马灯文本、
  右键菜单=托盘菜单、双击回主面板、透明度/置顶可调。
- F2 面板收起↔悬浮球联动。

### G 新增功能
- G1 一键导入旧项目数据：读旧 `token_monitor.db`（usage_log/usage_daily/usage_missed/op_log）、
  旧 pricing.json/config.json/settings.json → 转换入新库；API 密钥默认不迁移、勾选确认；
  导入前自动备份新库。
- G2 API 密钥 DPAPI 加密存储（config.json 中 `dpapi:` 前缀，CryptProtectData）。
- G3 全部 Bug 修复（见 02-bug-audit.md）。

## 4. 配置文件格式（保持与原版字段兼容以便导入；存于 exe 旁 data/）

```
config.json    { "listen_addr": "127.0.0.1:8280",
                 "providers": [ { "name","base_url","api_key","model_prefix":[...], "max_tokens":null,
                                  "strip_params":["enable_thinking","reasoning_effort"] } ] }
               # api_key 允许 "dpapi:<base64>" 密文（G2）
pricing.json   { "multipliers": { "<model>": { "history": [ { "effective_from":"YYYY-MM-DD",
                 "periods":[ { "start":9.0, "end":12.5, "rate":1.5 } ] } ] } },
                 "pricing":     { "<model>": { "history": [ { "effective_from":"YYYY-MM-DD",
                 "currency":"CNY", "rules":[ { "start","end","days":[1,2],"input_per_1m","cache_per_1m","output_per_1m" } ] } ] } } }
settings.json  { "offset_min": 480, "effective_date_mode": "local",
                 "ball_opacity": 80, "ball_topmost": true }
```

## 5. 用量归一化（UnifiedUsage）字段与兼容别名

- 字段：PromptTokens / CacheHitTokens / CacheMissTokens / CompletionTokens / ReasoningTokens / TotalTokens。
- 别名兼容：DeepSeek `prompt_cache_hit_tokens`/`prompt_cache_miss_tokens`；OpenAI 系
  `prompt_tokens_details.cached_tokens`；DashScope `input_tokens`/`output_tokens`；reasoning 从
  `completion_tokens_details.reasoning_tokens` 或 `reasoning_tokens` 取。
- 恒等式：强制 `prompt == hit + miss`（hit 缺失时 miss=prompt-hit 推导）。
- total 缺失时推导 = prompt + completion。⚠️ C3：新实现必须全链路统一 total 来源（厂商 total
  与 prompt+completion 不一致时选定一种并处处一致——双口径/导出/倍率值同源）。

## 6. 原 HTTP API 面（仅供理解功能语义；新架构取消全部 /api/*，改进程内调用）

`/api/stats`（实时快照：UTC+Local 桶/summary/漏抓/offset）、`/api/stats/recent[-local]?days=`、
`/api/stats/range[-local]?start=&end=`、`/api/stats/reset?model=&delete=`、`/api/export?start=&end=&model=&hourly=`、
`/api/pricing` GET/POST（set_multiplier/set_pricing 版本追加或全量）、`/api/models/all`、`/api/oplogs?model=`、
`/api/settings`、`/api/settings/timezone|effmode|ball`、`/api/multiplier-states(/save)`、
`/api/calibrate/models|apply|rollback|state`、`/api/shutdown`、`/health`、`/v1/models`、`/v1/*`（代理面）。
**新项目仅保留**：`/v1/*` 代理面、`/v1/models`、`/health`（供外部探活）。

## 7. 原源码索引（设计时精读用，均在 `D:\GLM Workspace\Projects\Token Calculator\neon-token-monitor\`）

- 反代+捕获：`internal/proxy/proxy.go`（handleProxy/interceptSSE/interceptJSON/recordMissed/ApplyManualCalibrate）
- 解析：`internal/parser/parser.go`（NormalizeUsage/ParseSSEUsageLine）
- 统计：`internal/stats/stats.go`（Accumulator/AddUsage/SetTodayStats/restoreTodayFromDB）
- 计价：`internal/pricing/pricing.go`（GetRateAt/CalcCostAt/shiftPeriodsToLocal/shiftPriceRulesToLocal/merge）
- 存储：`internal/storage/storage.go`（DDL/upsertDaily/RecalcDerived/AggregateLocal/AggregateHourly/Export/ReplaceDatabase）
- 进程编排+托盘：`cmd/ntm-proxy/main.go`（托盘菜单/日切换守望/refreshStatsAndProxy）
- 时区平移参考实现（JS）：`frontend/src/utils/tzShift.js`
- 单元测试：上述各目录 `*_test.go`
