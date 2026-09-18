# 原项目 Bug 审计报告（19 确认 + 7 风险）——新项目必须逐项修复并配回归测试

> 审计对象：原 Go 源码（路径见 01-original-features.md §7）。每项含：位置 / 问题 / 新项目修法。
> 编号沿用审计报告（C=Confirmed 确认，S=Suspected 风险）。

## 高危

### C1 RecalcDerived 仅 UPDATE 不 INSERT —— 重算静默丢数据
- 原：`storage.go:577-595` 重建 usage_daily 只执行 UPDATE，usage_log 有而 usage_daily 无的行被丢弃。
- 后果链：手动补录后若当日无 usage_daily 行（全是漏抓/跨午夜后），补录数据进 usage_log 但永不进
  UTC 视图/近 N 天/导出（Local 视图从 usage_log 现算反而有 → 两口径不一致可见）。
  "重置今日"（删 usage_daily 留 usage_log）后再重算也无法恢复。
- 新修法：第 3 步改 `INSERT ... ON CONFLICT(date,provider,model) DO UPDATE SET ...`。
- 回归测试：插入 usage_log 行（无 daily 行）→ RecalcDerived → 断言 daily 行生成且数值正确；
  补录流程端到端断言 UTC 视图可见。

### C2 强制 stream=true —— 非流式客户端收到 SSE
- 原：`proxy.go:226-227` 无条件 `modifiedBody["stream"]=true` + 注入 stream_options；
  `stream:false` 的客户端收到 text/event-stream 而非 JSON。且非 JSON 请求体（/v1/audio/* multipart、
  GET /v1/files 空 body）`json.Unmarshal` 失败被 400 拒绝。
- 新修法：尊重客户端流偏好——仅当请求本就是流式时注入 include_usage；非流式请求不改造
  （JSON 路径本就能捕获 usage）；Content-Type 非 JSON 的请求体透传不解析。
- 回归测试：stream:false 请求 → 响应为 application/json 且 usage 已捕获；multipart 透传 200。

### C4 半小时粒度时段永不匹配
- 原：`stats.go:179,199,231` 只取整点 `Hour()`；`pricing.go:295-299,411,561` 用
  `float64(hour) >= p.Start && float64(hour) < p.End`。配置 9.5（9:30）起倍率的时段，9:00-9:59
  全部按 1.0 倍率/0 成本，与厂商计费（9:30 起）不符。UI 与类型系统都支持 0.5 步进。
- 新修法：统一传分数小时 `h + minute/60.0` 进匹配函数；修正后更新对应测试。
- 回归测试：09:29 → 前段费率；09:30 → 新费率；00:00/24:00 边界；UTC+5:30 半时区下同样成立。

## 中危

### C3 双口径 total 来源不一致
- 原：UTC 桶/usage_daily.total_tokens 用厂商 TotalTokens（stats.go:190 / storage.go:312）；
  Local 桶/AggregateLocal/Hourly 用 prompt+completion（stats.go:221 / storage.go:921,1028）。
  厂商 total ≠ prompt+completion 时（部分厂商含额外成分/舍入），两口径卡片与导出表不一致。
  且倍率值恒按 prompt+completion（stats.go:201），倍率模式下"总Token"与实际模式对不上。
- 新修法：在 parser 归一化处一次性决策并全链路统一（推荐：TotalTokens = 厂商 total 缺失时
  推导 prompt+completion；倍率值与所有聚合同源引用该字段），写入设计文档作为铁律。
- 回归测试：构造 total≠prompt+completion 的 usage → 两口径/导出/倍率值全部一致。

### C5 时区/计价变更重建累积器：在途请求丢失 + 数据竞争
- 原：`main.go:635-681` 直接替换全局 acc/proxyInstance；包级全局变量（acc/offset/effmode）
  被多个 goroutine 无锁读写（30s 守望线程/HTTP handler/导出）——真实数据竞争。
  重建瞬间在途请求的 usage 写进被丢弃的旧桶：DB 有行、实时卡片丢失直到次日重建。
- 新修法：所有可变状态经锁保护；重建=锁内"取旧快照→换新累积器→按 DB 回放间隙窗口"；
  C# 侧用不可变快照对象发布给 UI。
- 回归测试：并发压测（捕获进行中切时区/改价）→ 断言无丢失、无死锁（可用确定性调度/计数器校验）。

### C6 回滚恢复 DB 的 WAL 侧车与连接串问题
- 原：`storage.go:767-799` 覆盖 db 文件后不删 `token_monitor.db-wal/-shm`（可能回放错 WAL 损坏数据），
  且 `sql.Open` 未用 New() 的 DSN（无 busy_timeout/WAL）。
- 新修法：关闭连接→删除 -wal/-shm→写备份→用同参数重开。
- 回归测试：制造 -wal 文件 → 回滚 → 断言侧车被清且库可读、journal_mode 正确。

### C7 近 N 天 off-by-one（UTC 侧）
- 原：`storage.go:352` `date('now', '-N days')` 返回 N+1 个日历日；Local 侧 `days-1` 正确。
  前端 GetRecentDays(365) 实取 366 天。7 日折线图窗口两口径差一天。
- 新修法：UTC 侧 `-(days-1)` 与 Local 对齐。
- 回归测试：插入 8 天数据 → GetRecent(7) 恰 7 行。

### C8 重置/删除不清理 usage_missed
- 原：`proxy.go:780-819`、`storage.go:390-426` 只删 usage_daily/usage_log → 卡片残留红框、横幅计数虚高。
- 新修法：DeleteModelData/DeleteAllData 同步删 usage_missed；ResetToday 清当日漏抓。
- 回归测试：造漏抓 → 删除 → 断言 missed 清零、横幅消失。

### C9 自定义时间范围标"(UTC)"实按本地换算
- 原：`TimeRangeDialog.vue:14-23,63` 全部标注 UTC；`App.vue:828-835,131-135,404` 把原始日期
  直接送 GetRangeStatsLocal → 边界错位至多 offset 小时。
- 新修法：范围对话框按当前卡片口径标注与换算（Local 口径：日期边界减 offset 后再查 UTC 日期列）。
- 回归测试：UTC+8 下自定义 2026-09-01→2026-09-02，边界数据归属断言。

## 低危

- C10 CORS `*` + 无鉴权破坏性接口（/api/shutdown、reset、pricing 任意网页可 fetch）。
  **新架构根除**：取消 /api 面，UI 进程内直调；HTTP 仅 /v1/*、/v1/models、/health。
- C11 倍率值逐字段 int 截断 → mulTotal ≠ mulPrompt+mulCompletion。
  修法：统一 Math.Round 且 mulTotal 由分量和推导（与 C3 的"同源铁律"合并处理）。
- C12 flush 中 usage_log 插入失败的行仍进 usage_daily 聚合。修法：失败行跳过聚合、进重排队列。
- C13 前端死代码 getMultiplierRate/DataViewerDialog 引用旧 schema（新项目无此代码，注意移植时
  不要把旧格式迁移逻辑写错即可）。
- C14 未知模型静默路由到第一提供商（错账+错密钥）。修法：未匹配 → 404/400 拒绝；
  可选配置 `default_provider` 显式指定兜底。
- C15 strip enable_thinking/reasoning_effort 对所有提供商生效。修法：per-provider `strip_params` 配置。
- C16 前端 await 后重读选中态 → 范围结果可能存错卡片（新项目 MVVM 下注意异步回包时校验卡片键）。
- C17 时区切换不清卡片范围缓存。修法：offset 变更时清空全部口径相关缓存（新架构由事件总线广播解决）。
- C18 死解析器代码含泄漏隐患（TeeReaderSplit 的 pr 无人关闭）。新项目不移植死代码。
- C19 杂项：①导出"今日"UTC 表与 HOUR 表的"今日"基准不同（文档化统一）②模型名含 `/` 的 split 兼容
  （用 SplitN(…,2) 语义）③上游 30 分钟硬上限（文档化为已知限制，UI 有漏抓提示）④端口占用 TOCTOU
  （改用绑定前预检+绑定失败即报）。

## 风险项（新项目一并加固）

- S1 SetTodayStats 不清 DeltaTokens/localStats → 新实现"设置即完整替换语义"。
- S2 旧库 total_tokens=0 行被重算放大为 0 → 聚合时回退 prompt+completion（导入功能必测）。
- S3 每请求重算 Local 平移表 O(rules) → 按 (model,offset,effmode) 缓存，配置变更失效。
- S4 纯漏抓新模型不计入横幅总数 → 快照汇总遍历全部模型（含无卡片者）。
- S5 Server.Close() 掐断在途响应 → 用 Shutdown(graceful)+超时兜底。
- S6 空 providers 配置导致全部 400 → 加载时校验合并默认值并告警，不 bricks。
- S7 每 5 秒全年聚合重扫 → 新架构：近 N 天/范围查询按需+结果缓存（事件失效），避免高频全表扫。
