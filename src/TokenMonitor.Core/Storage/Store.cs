using Microsoft.Data.Sqlite;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Storage;

/// <summary>
/// IStore 默认实现：SQLite（WAL + busy_timeout + synchronous=NORMAL）单连接 + 单锁串行（01-§4）。
/// 批量刷写攒 3 条或 5 秒；log+daily 同事务、失败行整行跳过 [C12]；RecalcDerived UPSERT 全量重算 [C1]；
/// 备份/回滚处理 WAL 侧车 [C6]；重置/删除同步清理 usage_missed [C8]。
/// </summary>
public sealed class Store : IStore
{
    private readonly object _gate = new();
    private readonly string _dbPath;
    private readonly string _backupDir;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _flushInterval;
    private readonly string _connectionString;
    private SqliteConnection _conn;
    private readonly List<UsageLogRow> _buffer = new();
    private CancellationTokenSource? _flushLoopCts;
    private Task? _flushLoopTask;
    private bool _disposed;

    /// <summary>行级插入失败注入点（仅测试用；返回非空异常则该行被整体跳过）。</summary>
    internal Func<UsageLogRow, Exception?>? InsertFailureHook { get; set; }

    /// <summary>提交失败注入点（仅测试用；返回 true 则 Commit 前抛异常 → 整批回队重试）</summary>
    internal Func<bool>? CommitFailureHook { get; set; }

    /// <summary>测试辅助：在内部连接上执行任意 SQL。</summary>
    internal void TestExecuteNonQuery(string sql)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>测试辅助：读取 journal_mode（验证 ReplaceDatabase 后同参重开保持 WAL）。</summary>
    internal string TestJournalMode()
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
    }

    public Store(string dataDir, TimeProvider? clock = null, TimeSpan? flushInterval = null)
    {
        _clock = clock ?? TimeProvider.System;
        _flushInterval = flushInterval ?? TimeSpan.FromSeconds(5);
        Directory.CreateDirectory(dataDir);
        _dbPath = Path.Combine(dataDir, "token_monitor.db");
        _backupDir = Path.Combine(dataDir, "backups");
        // Pooling=False：ReplaceDatabase 需要释放文件句柄（删侧车/覆盖主库），必须真实关连接
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false,
        }.ToString();
        _conn = new SqliteConnection(_connectionString);
        OpenAndPrepare(_conn);
        StartFlushLoop();
    }

    private void OpenAndPrepare(SqliteConnection conn)
    {
        conn.Open();
        ExecutePragma(conn, "PRAGMA journal_mode=WAL;");
        ExecutePragma(conn, "PRAGMA busy_timeout=5000;");
        ExecutePragma(conn, "PRAGMA synchronous=NORMAL;");
        EnsureTables(conn);
    }

    private static void ExecutePragma(SqliteConnection conn, string pragma)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = pragma;
        cmd.ExecuteNonQuery();
    }

    // —— DDL（01-§4.1，列名对齐原 storage.go 便于旧库导入）——

    private static void EnsureTables(SqliteConnection conn)
    {
        const string usageLog = """
            CREATE TABLE IF NOT EXISTS usage_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts INTEGER NOT NULL,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                prompt_cache_hit INTEGER NOT NULL DEFAULT 0,
                prompt_cache_miss INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens INTEGER NOT NULL DEFAULT 0,
                total_tokens INTEGER NOT NULL DEFAULT 0,
                estimated INTEGER NOT NULL DEFAULT 0,
                import_batch INTEGER
            )
            """;
        const string usageDaily = """
            CREATE TABLE IF NOT EXISTS usage_daily (
                date TEXT NOT NULL,
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
            )
            """;
        const string usageMissed = """
            CREATE TABLE IF NOT EXISTS usage_missed (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts INTEGER NOT NULL,
                provider TEXT NOT NULL,
                model TEXT NOT NULL,
                status INTEGER NOT NULL DEFAULT 0,
                reason TEXT NOT NULL DEFAULT ''
            )
            """;
        const string opLog = """
            CREATE TABLE IF NOT EXISTS op_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts INTEGER NOT NULL,
                model TEXT NOT NULL,
                action TEXT NOT NULL,
                detail TEXT NOT NULL,
                effective_from TEXT NOT NULL DEFAULT ''
            )
            """;
        string[] indexes =
        [
            "CREATE INDEX IF NOT EXISTS idx_usage_log_ts ON usage_log(ts)",
            "CREATE INDEX IF NOT EXISTS idx_usage_log_model ON usage_log(provider, model)",
            "CREATE INDEX IF NOT EXISTS idx_usage_daily_date ON usage_daily(date)",
            "CREATE INDEX IF NOT EXISTS idx_usage_missed_ts ON usage_missed(ts)",
            "CREATE INDEX IF NOT EXISTS idx_usage_missed_model ON usage_missed(provider, model)",
            "CREATE INDEX IF NOT EXISTS idx_op_log_model ON op_log(model)",
        ];
        foreach (var sql in new[] { usageLog, usageDaily, usageMissed, opLog }.Concat(indexes))
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    // —— 写路径 ——

    public void AddUsage(IReadOnlyList<UsageLogRow> rows)
    {
        List<UsageLogRow>? toFlush = null;
        lock (_gate)
        {
            _buffer.AddRange(rows);
            if (_buffer.Count >= 3) toFlush = TakeBatchNoLock();
        }
        if (toFlush is not null) FlushBatch(toFlush); // 攒 3 条立即刷（原 flushCnt>=3 语义），锁外执行
    }

    public void Flush()
    {
        List<UsageLogRow>? batch;
        lock (_gate)
        {
            batch = _buffer.Count > 0 ? TakeBatchNoLock() : null;
        }
        if (batch is not null) FlushBatch(batch);
    }

    private List<UsageLogRow> TakeBatchNoLock()
    {
        var batch = new List<UsageLogRow>(_buffer);
        _buffer.Clear();
        return batch;
    }

    private void FlushBatch(List<UsageLogRow> batch)
    {
        if (batch.Count == 0) return;
        // 全程持 Store 锁：单连接不支持嵌套事务，摄入线程触发的刷写与定时刷写必须串行
        // （Backup/ReplaceDatabase/RecalcDerived 同理经此锁互斥；同线程重入安全）
        lock (_gate)
        {
            try
            {
                using var tx = _conn.BeginTransaction();
                // 单事务内 log INSERT + daily UPSERT（原版 daily 在事务外 → C12 根因之一，附录 B-4）
                const string insertSql = """
                    INSERT INTO usage_log
                        (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                         completion_tokens, reasoning_tokens, total_tokens, estimated)
                    VALUES (@ts, @provider, @model, @p, @hit, @miss, @comp, @reas, @total, @estimated);
                    """;
                var ok = new List<UsageLogRow>(batch.Count);
                foreach (var r in batch)
                {
                    try
                    {
                        if (InsertFailureHook?.Invoke(r) is { } injected) throw injected; // 测试注入 [C12]
                        using var cmd = _conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = insertSql;
                        Bind(cmd, r);
                        cmd.ExecuteNonQuery();
                        ok.Add(r);
                    }
                    catch (Exception ex)
                    {
                        // [C12] 单行失败 → 该行整体跳过（log 与 daily 都不写），不拖垮同批其它行
                        Logger.Warn("Storage", $"插入失败，本行整体跳过(log+daily): {ex.Message}");
                    }
                }
                foreach (var r in ok) UpsertDaily(r, tx);
                if (CommitFailureHook?.Invoke() == true)
                    throw new InvalidOperationException("注入的提交失败（测试）");
                tx.Commit();
            }
            catch (Exception)
            {
                // Begin/Commit 失败（DB 锁满等）→ 整批放回缓冲头部重试（原 requeue 语义）
                _buffer.InsertRange(0, batch);
                Logger.Warn("Storage", "事务失败，整批回队重试");
            }
        }
    }

    private static void Bind(SqliteCommand cmd, UsageLogRow r)
    {
        cmd.Parameters.AddWithValue("@ts", r.Ts);
        cmd.Parameters.AddWithValue("@provider", r.Provider);
        cmd.Parameters.AddWithValue("@model", r.Model);
        cmd.Parameters.AddWithValue("@p", r.PromptTokens);
        cmd.Parameters.AddWithValue("@hit", r.CacheHitTokens);
        cmd.Parameters.AddWithValue("@miss", r.CacheMissTokens);
        cmd.Parameters.AddWithValue("@comp", r.CompletionTokens);
        cmd.Parameters.AddWithValue("@reas", r.ReasoningTokens);
        cmd.Parameters.AddWithValue("@total", r.TotalTokens);
        cmd.Parameters.AddWithValue("@estimated", r.Estimated ? 1L : 0L);
    }

    /// <summary>usage_daily 增量 UPSERT（01-§4.3，列级公式与原 upsertDaily 逐列一致；date = ts 的 UTC 日期）。</summary>
    private void UpsertDaily(UsageLogRow r, SqliteTransaction tx)
    {
        const string sql = """
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
            """;
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@date", TimeMath.UtcDate(r.Ts));
            cmd.Parameters.AddWithValue("@provider", r.Provider);
            cmd.Parameters.AddWithValue("@model", r.Model);
            cmd.Parameters.AddWithValue("@p", r.PromptTokens);
            cmd.Parameters.AddWithValue("@hit", r.CacheHitTokens);
            cmd.Parameters.AddWithValue("@miss", r.CacheMissTokens);
            cmd.Parameters.AddWithValue("@comp", r.CompletionTokens);
            cmd.Parameters.AddWithValue("@reas", r.ReasoningTokens);
            cmd.Parameters.AddWithValue("@total", r.TotalTokens);
            cmd.Parameters.AddWithValue("@mulTotal", r.MulTotal);
            cmd.Parameters.AddWithValue("@mulPrompt", r.MulPrompt);
            cmd.Parameters.AddWithValue("@mulHit", r.MulCacheHit);
            cmd.Parameters.AddWithValue("@mulMiss", r.MulCacheMiss);
            cmd.Parameters.AddWithValue("@mulComp", r.MulCompletion);
            cmd.Parameters.AddWithValue("@mulReas", r.MulReasoning);
            cmd.Parameters.AddWithValue("@cny", r.CostCNY);
            cmd.Parameters.AddWithValue("@usd", r.CostUSD);
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Logger.Warn("Storage", $"更新日聚合失败: {ex.Message}");
        }
    }

    private void StartFlushLoop()
    {
        _flushLoopCts = new CancellationTokenSource();
        var ct = _flushLoopCts.Token;
        _flushLoopTask = Task.Run(async () =>
        {
            try
            {
                using var timer = new PeriodicTimer(_flushInterval);
                while (await timer.WaitForNextTickAsync(ct)) Flush();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Logger.Warn("Storage", $"定时刷盘循环异常退出: {ex.Message}");
            }
        }, ct);
    }

    public void AddMissed(MissedLogRow m)
    {
        if (string.IsNullOrEmpty(m.Provider) || string.IsNullOrEmpty(m.Model)) return; // 原实现防御保持
        try
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO usage_missed (ts, provider, model, status, reason) VALUES (@ts, @provider, @model, @status, @reason)";
                cmd.Parameters.AddWithValue("@ts", m.Ts);
                cmd.Parameters.AddWithValue("@provider", m.Provider);
                cmd.Parameters.AddWithValue("@model", m.Model);
                cmd.Parameters.AddWithValue("@status", m.Status);
                cmd.Parameters.AddWithValue("@reason", m.Reason);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Storage", $"写入漏抓记录失败: {ex.Message}");
        }
    }

    public void LogOp(string model, string action, string detail, string effectiveFrom)
    {
        try
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "INSERT INTO op_log (ts, model, action, detail, effective_from) VALUES (@ts, @model, @action, @detail, @effectiveFrom)";
                cmd.Parameters.AddWithValue("@ts", _clock.GetUtcNow().ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("@model", model);
                cmd.Parameters.AddWithValue("@action", action);
                cmd.Parameters.AddWithValue("@detail", detail);
                cmd.Parameters.AddWithValue("@effectiveFrom", effectiveFrom);
                cmd.ExecuteNonQuery();
                // 写入侧裁剪至最近 5000（附录 B-5，防无界增长；读取恒 500）
                using var trim = _conn.CreateCommand();
                trim.CommandText = "DELETE FROM op_log WHERE id NOT IN (SELECT id FROM op_log ORDER BY id DESC LIMIT 5000)";
                trim.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Storage", $"写入操作日志失败: {ex.Message}");
        }
    }

    public void InsertEstimatedUsage(IReadOnlyList<UsageLogRow> rows)
    {
        if (rows.Count == 0) return;
        try
        {
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                foreach (var r in rows)
                {
                    try
                    {
                        using var cmd = _conn.CreateCommand();
                        cmd.Transaction = tx;
                        cmd.CommandText = """
                            INSERT INTO usage_log (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                                                   completion_tokens, reasoning_tokens, total_tokens, estimated)
                            VALUES (@ts, @provider, @model, @p, @hit, @miss, @comp, @reas, @total, 1)
                            """;
                        // 原防御推导保留：prompt==0 → hit+miss；total==0 → prompt+completion
                        var prompt = r.PromptTokens == 0 ? r.CacheHitTokens + r.CacheMissTokens : r.PromptTokens;
                        var total = r.TotalTokens == 0 ? prompt + r.CompletionTokens : r.TotalTokens;
                        cmd.Parameters.AddWithValue("@ts", r.Ts);
                        cmd.Parameters.AddWithValue("@provider", r.Provider);
                        cmd.Parameters.AddWithValue("@model", r.Model);
                        cmd.Parameters.AddWithValue("@p", prompt);
                        cmd.Parameters.AddWithValue("@hit", r.CacheHitTokens);
                        cmd.Parameters.AddWithValue("@miss", r.CacheMissTokens);
                        cmd.Parameters.AddWithValue("@comp", r.CompletionTokens);
                        cmd.Parameters.AddWithValue("@reas", r.ReasoningTokens);
                        cmd.Parameters.AddWithValue("@total", total);
                        cmd.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        throw new StorageException($"补录行插入失败（整批回滚）: {ex.Message}", ex);
                    }
                }
                tx.Commit();
            }
        }
        catch (StorageException) { throw; }
        catch (Exception ex)
        {
            throw new StorageException($"补录事务失败: {ex.Message}", ex);
        }
    }

    // —— 查询 ——

    public IReadOnlyList<ModelDailyAggregate> GetTodayUtc() =>
        QueryDailyRange(TimeMath.UtcDate(_clock.GetUtcNow().ToUnixTimeSeconds()),
                        TimeMath.UtcDate(_clock.GetUtcNow().ToUnixTimeSeconds()));

    public IReadOnlyList<ModelDailyAggregate> QueryDailyRange(string startUtc, string endUtc)
    {
        try
        {
            lock (_gate)
            {
                var rows = new List<ModelDailyAggregate>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    SELECT date, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                           completion_tokens, reasoning_tokens, total_tokens, request_count, mul_total,
                           mul_prompt, mul_cache_hit, mul_cache_miss, mul_completion, mul_reasoning,
                           cost_cny, cost_usd
                    FROM usage_daily WHERE date >= @s AND date <= @e
                    ORDER BY date ASC, provider ASC, model ASC
                    """;
                cmd.Parameters.AddWithValue("@s", startUtc);
                cmd.Parameters.AddWithValue("@e", endUtc);
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) rows.Add(ReadDaily(reader));
                return rows;
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"查询 usage_daily 失败: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<ModelDailyAggregate> GetRecentDaysUtc(int days)
    {
        if (days <= 0) days = 1;
        // [C7] 恰好 days 个日历日：startDate = UTC 今日 −(days−1)（原 "-N days" 返回 N+1 日为 off-by-one）
        var today = TimeMath.UtcDate(_clock.GetUtcNow().ToUnixTimeSeconds());
        var start = TimeMath.UtcDate(TimeMath.UtcDayStart(today) - (days - 1) * 86400L);
        return QueryDailyRange(start, today);
    }

    public IReadOnlyList<UsageLogRow> GetLogsByRange(long startTs, long endExclusiveTs)
    {
        try
        {
            lock (_gate)
            {
                var rows = new List<UsageLogRow>();
                using var cmd = _conn.CreateCommand();
                // D5-3：SELECT 带 total_tokens（原不带 → C3/S2 根因之一）
                cmd.CommandText = """
                    SELECT ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                           completion_tokens, reasoning_tokens, total_tokens
                    FROM usage_log WHERE ts >= @s AND ts < @e ORDER BY ts ASC
                    """;
                cmd.Parameters.AddWithValue("@s", startTs);
                cmd.Parameters.AddWithValue("@e", endExclusiveTs);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    rows.Add(new UsageLogRow(
                        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                        reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)));
                }
                return rows;
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"查询 usage_log 失败: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<ModelDailyAggregate> AggregateLocal(long startTs, long endExclusiveTs, int offsetMin,
                                                             RateFunc? rateFn, CostFunc? costFn)
    {
        // 锁内只读日志，聚合在锁外进行（§8.5：Store 锁内不回调 Pricing 委托之外的策略——这里连委托也不在锁内调）
        var logs = GetLogsByRange(startTs, endExclusiveTs);
        var agg = new Dictionary<(string Date, string Provider, string Model), Agg>();
        foreach (var l in logs)
        {
            var date = TimeMath.LocalDate(l.Ts, offsetMin);
            var hour = TimeMath.LocalFractionalHour(l.Ts, offsetMin); // [C4] 分数小时（原 conv.HourAt 整小时）
            var key = l.Provider + "/" + l.Model;
            var rate = rateFn?.Invoke(key, l.Ts, hour, offsetMin) ?? 1.0;
            var effTotal = l.TotalTokens > 0 ? l.TotalTokens : l.PromptTokens + l.CompletionTokens; // [S2]+[C3]
            var a = GetAgg(agg, (date, l.Provider, l.Model));
            a.Prompt += l.PromptTokens;
            a.Hit += l.CacheHitTokens;
            a.Miss += l.CacheMissTokens;
            a.Comp += l.CompletionTokens;
            a.Reas += l.ReasoningTokens;
            a.Total += effTotal;
            a.Req++;
            var mulP = TimeMath.Round(l.PromptTokens * rate);
            var mulH = TimeMath.Round(l.CacheHitTokens * rate);
            var mulM = TimeMath.Round(l.CacheMissTokens * rate);
            var mulC = TimeMath.Round(l.CompletionTokens * rate);
            var mulR = TimeMath.Round(l.ReasoningTokens * rate);
            a.MulPrompt += mulP;
            a.MulHit += mulH;
            a.MulMiss += mulM;
            a.MulCompletion += mulC;
            a.MulReas += mulR;
            a.MulTotal += mulP + mulC; // [C11] 分量和
            if (costFn is not null)
            {
                var (cny, usd) = costFn(key, l.Ts, hour, offsetMin, l.CacheHitTokens, l.CacheMissTokens, l.CompletionTokens);
                a.CostCNY += cny;
                a.CostUSD += usd;
            }
        }
        return agg.Select(kv => new ModelDailyAggregate(kv.Key.Date, kv.Key.Provider, kv.Key.Model,
                kv.Value.Prompt, kv.Value.Hit, kv.Value.Miss, kv.Value.Comp, kv.Value.Reas, kv.Value.Total,
                kv.Value.Req, kv.Value.MulTotal, kv.Value.MulPrompt, kv.Value.MulHit, kv.Value.MulMiss,
                kv.Value.MulCompletion, kv.Value.MulReas, kv.Value.CostCNY, kv.Value.CostUSD))
            .OrderBy(r => r.Date).ThenBy(r => r.Provider).ThenBy(r => r.Model)
            .ToList();
    }

    public IReadOnlyList<HourlyAggregate> AggregateHourly(long startTs, long endExclusiveTs, int offsetMin,
                                                          RateFunc? rateFn, CostFunc? costFn)
    {
        var logs = GetLogsByRange(startTs, endExclusiveTs);
        var agg = new Dictionary<(string Date, int Hour, string Provider, string Model), Agg>();
        foreach (var l in logs)
        {
            var local = TimeMath.ToOffset(l.Ts, offsetMin);
            var date = local.ToString("yyyy-MM-dd");
            var hour = local.Hour;
            var key = l.Provider + "/" + l.Model;
            var rate = rateFn?.Invoke(key, l.Ts, hour + local.Minute / 60.0, offsetMin) ?? 1.0;
            var effTotal = l.TotalTokens > 0 ? l.TotalTokens : l.PromptTokens + l.CompletionTokens; // [S2]+[C3]
            var a = GetAgg(agg, (date, hour, l.Provider, l.Model));
            a.Prompt += l.PromptTokens;
            a.Hit += l.CacheHitTokens;
            a.Miss += l.CacheMissTokens;
            a.Comp += l.CompletionTokens;
            a.Reas += l.ReasoningTokens;
            a.Total += effTotal;
            a.Req++;
            _ = rate; // HOUR 表无 mul 列（与原 AggregateHourly 一致），rate 仅保持委托调用契约
            if (costFn is not null)
            {
                var (cny, usd) = costFn(key, l.Ts, hour + local.Minute / 60.0, offsetMin,
                                        l.CacheHitTokens, l.CacheMissTokens, l.CompletionTokens);
                a.CostCNY += cny;
                a.CostUSD += usd;
            }
        }
        return agg.Select(kv => new HourlyAggregate(kv.Key.Date, kv.Key.Hour, kv.Key.Provider, kv.Key.Model,
                kv.Value.Prompt, kv.Value.Hit, kv.Value.Miss, kv.Value.Comp, kv.Value.Reas, kv.Value.Total,
                kv.Value.Req, kv.Value.CostCNY, kv.Value.CostUSD))
            .OrderBy(r => r.Date).ThenBy(r => r.Hour).ThenBy(r => r.Provider).ThenBy(r => r.Model)
            .ToList();
    }

    private static Agg GetAgg<TK>(Dictionary<TK, Agg> map, TK key) where TK : notnull
    {
        if (!map.TryGetValue(key, out var a))
        {
            a = new Agg();
            map[key] = a;
        }
        return a;
    }

    private sealed class Agg
    {
        public long Prompt, Hit, Miss, Comp, Reas, Total, Req;
        public long MulTotal, MulPrompt, MulHit, MulMiss, MulCompletion, MulReas;
        public double CostCNY, CostUSD;
    }

    public IReadOnlyList<MissedLogRow> GetMissed(long startTs, long endExclusiveTs)
    {
        try
        {
            lock (_gate)
            {
                var rows = new List<MissedLogRow>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT id, ts, provider, model, status, reason FROM usage_missed WHERE ts >= @s AND ts < @e ORDER BY ts ASC";
                cmd.Parameters.AddWithValue("@s", startTs);
                cmd.Parameters.AddWithValue("@e", endExclusiveTs);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    rows.Add(new MissedLogRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                        reader.GetString(3), (int)reader.GetInt64(4), reader.GetString(5)));
                return rows;
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"查询 usage_missed 失败: {ex.Message}", ex);
        }
    }

    public IReadOnlyDictionary<string, long> GetMissedCountByModel(long startTs, long endExclusiveTs)
    {
        try
        {
            lock (_gate)
            {
                var map = new Dictionary<string, long>(StringComparer.Ordinal);
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT provider, model, COUNT(*) FROM usage_missed WHERE ts >= @s AND ts < @e GROUP BY provider, model";
                cmd.Parameters.AddWithValue("@s", startTs);
                cmd.Parameters.AddWithValue("@e", endExclusiveTs);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    map[reader.GetString(0) + "/" + reader.GetString(1)] = reader.GetInt64(2);
                return map;
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"查询漏抓计数失败: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<OpLogRow> GetOpLogs(string? modelKey)
    {
        try
        {
            lock (_gate)
            {
                var rows = new List<OpLogRow>();
                using var cmd = _conn.CreateCommand();
                if (string.IsNullOrEmpty(modelKey) || modelKey == "all")
                    cmd.CommandText = "SELECT id, ts, model, action, detail, effective_from FROM op_log ORDER BY ts DESC, id DESC LIMIT 500";
                else
                {
                    cmd.CommandText = "SELECT id, ts, model, action, detail, effective_from FROM op_log WHERE model = @k ORDER BY ts DESC, id DESC LIMIT 500";
                    cmd.Parameters.AddWithValue("@k", modelKey);
                }
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    rows.Add(new OpLogRow(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
                        reader.GetString(3), reader.GetString(4), reader.GetString(5)));
                return rows;
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"查询 op_log 失败: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<(string Provider, string Model)> GetAllModels()
    {
        try
        {
            lock (_gate)
            {
                var rows = new List<(string, string)>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "SELECT DISTINCT provider, model FROM usage_daily ORDER BY provider ASC, model ASC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
                return rows;
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"查询模型清单失败: {ex.Message}", ex);
        }
    }

    /// <summary>清除指定厂商+模型的漏抓记录（手动补录后红框消失；具体类成员，供 CalibrationService 使用）。</summary>
    public void ClearMissedByModel(string provider, string model)
    {
        try
        {
            lock (_gate)
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = "DELETE FROM usage_missed WHERE provider = @p AND model = @m";
                cmd.Parameters.AddWithValue("@p", provider);
                cmd.Parameters.AddWithValue("@m", model);
                cmd.ExecuteNonQuery();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Storage", $"清除漏抓记录失败: {ex.Message}");
        }
    }

    // —— 维护/重算 ——

    public void RecalcDerived(RateFunc? rateFn, CostFunc? costFn)
    {
        // 1. 缓冲先落库（锁外调 Flush——Flush 自身取锁）
        Flush();
        List<UsageLogRow> logs;
        try
        {
            lock (_gate)
            {
                logs = new List<UsageLogRow>();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = """
                    SELECT ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                           completion_tokens, reasoning_tokens, total_tokens FROM usage_log
                    """;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    logs.Add(new UsageLogRow(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                        reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                        reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8)));
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"RecalcDerived 读取 usage_log 失败: {ex.Message}", ex);
        }

        // 2. 聚合（锁外；分数小时/Round/mulTotal=分量和 与实时路径同式 → 幂等）
        var agg = new Dictionary<(string Date, string Provider, string Model), Agg>();
        foreach (var l in logs)
        {
            var date = TimeMath.UtcDate(l.Ts);
            var hour = TimeMath.UtcFractionalHour(l.Ts); // [C4]
            var key = l.Provider + "/" + l.Model;
            var rate = rateFn?.Invoke(key, l.Ts, hour, 0) ?? 1.0;
            var effTotal = l.TotalTokens > 0 ? l.TotalTokens : l.PromptTokens + l.CompletionTokens; // [S2] 回退
            var a = GetAgg(agg, (date, l.Provider, l.Model));
            a.Prompt += l.PromptTokens;
            a.Hit += l.CacheHitTokens;
            a.Miss += l.CacheMissTokens;
            a.Comp += l.CompletionTokens;
            a.Reas += l.ReasoningTokens;
            a.Total += effTotal;
            a.Req++;
            var mulP = TimeMath.Round(l.PromptTokens * rate);
            var mulH = TimeMath.Round(l.CacheHitTokens * rate);
            var mulM = TimeMath.Round(l.CacheMissTokens * rate);
            var mulC = TimeMath.Round(l.CompletionTokens * rate);
            var mulR = TimeMath.Round(l.ReasoningTokens * rate);
            a.MulPrompt += mulP;
            a.MulHit += mulH;
            a.MulMiss += mulM;
            a.MulCompletion += mulC;
            a.MulReas += mulR;
            a.MulTotal += mulP + mulC; // [C11]
            if (costFn is not null)
            {
                var (cny, usd) = costFn(key, l.Ts, hour, 0, l.CacheHitTokens, l.CacheMissTokens, l.CompletionTokens);
                a.CostCNY += cny;
                a.CostUSD += usd;
            }
        }

        // 3. 单事务 UPSERT 全量写回 + 孤儿清理（[C1] 核心修复 + 铁律 5）
        try
        {
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                const string upsertSql = """
                    INSERT INTO usage_daily (
                      date, provider, model,
                      prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                      completion_tokens, reasoning_tokens, total_tokens, request_count,
                      mul_total, mul_prompt, mul_cache_hit, mul_cache_miss, mul_completion, mul_reasoning,
                      cost_cny, cost_usd)
                    VALUES (@date, @provider, @model, @p, @hit, @miss, @comp, @reas, @total, @req,
                            @mulTotal, @mulPrompt, @mulHit, @mulMiss, @mulComp, @mulReas, @cny, @usd)
                    ON CONFLICT(date, provider, model) DO UPDATE SET
                      prompt_tokens=excluded.prompt_tokens,  prompt_cache_hit=excluded.prompt_cache_hit,
                      prompt_cache_miss=excluded.prompt_cache_miss,  completion_tokens=excluded.completion_tokens,
                      reasoning_tokens=excluded.reasoning_tokens,  total_tokens=excluded.total_tokens,
                      request_count=excluded.request_count,
                      mul_total=excluded.mul_total,  mul_prompt=excluded.mul_prompt,
                      mul_cache_hit=excluded.mul_cache_hit,  mul_cache_miss=excluded.mul_cache_miss,
                      mul_completion=excluded.mul_completion,  mul_reasoning=excluded.mul_reasoning,
                      cost_cny=excluded.cost_cny,  cost_usd=excluded.cost_usd;
                    """;
                using var keys = _conn.CreateCommand();
                keys.Transaction = tx;
                keys.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS _recalc_keys
                      (date TEXT NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL,
                       PRIMARY KEY(date, provider, model));
                    DELETE FROM _recalc_keys;
                    """;
                keys.ExecuteNonQuery();
                foreach (var kv in agg)
                {
                    var a = kv.Value;
                    using (var cmd = _conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = upsertSql;
                        cmd.Parameters.AddWithValue("@date", kv.Key.Date);
                        cmd.Parameters.AddWithValue("@provider", kv.Key.Provider);
                        cmd.Parameters.AddWithValue("@model", kv.Key.Model);
                        cmd.Parameters.AddWithValue("@p", a.Prompt);
                        cmd.Parameters.AddWithValue("@hit", a.Hit);
                        cmd.Parameters.AddWithValue("@miss", a.Miss);
                        cmd.Parameters.AddWithValue("@comp", a.Comp);
                        cmd.Parameters.AddWithValue("@reas", a.Reas);
                        cmd.Parameters.AddWithValue("@total", a.Total);
                        cmd.Parameters.AddWithValue("@req", a.Req);
                        cmd.Parameters.AddWithValue("@mulTotal", a.MulTotal);
                        cmd.Parameters.AddWithValue("@mulPrompt", a.MulPrompt);
                        cmd.Parameters.AddWithValue("@mulHit", a.MulHit);
                        cmd.Parameters.AddWithValue("@mulMiss", a.MulMiss);
                        cmd.Parameters.AddWithValue("@mulComp", a.MulCompletion);
                        cmd.Parameters.AddWithValue("@mulReas", a.MulReas);
                        cmd.Parameters.AddWithValue("@cny", a.CostCNY);
                        cmd.Parameters.AddWithValue("@usd", a.CostUSD);
                        cmd.ExecuteNonQuery();
                    }
                    using (var mark = _conn.CreateCommand())
                    {
                        mark.Transaction = tx;
                        mark.CommandText = "INSERT OR IGNORE INTO _recalc_keys VALUES (@date, @provider, @model)";
                        mark.Parameters.AddWithValue("@date", kv.Key.Date);
                        mark.Parameters.AddWithValue("@provider", kv.Key.Provider);
                        mark.Parameters.AddWithValue("@model", kv.Key.Model);
                        mark.ExecuteNonQuery();
                    }
                }
                using (var orphan = _conn.CreateCommand())
                {
                    orphan.Transaction = tx;
                    orphan.CommandText = """
                        DELETE FROM usage_daily WHERE NOT EXISTS (
                          SELECT 1 FROM _recalc_keys k
                          WHERE k.date = usage_daily.date AND k.provider = usage_daily.provider
                            AND k.model = usage_daily.model);
                        """;
                    orphan.ExecuteNonQuery();
                }
                using (var drop = _conn.CreateCommand())
                {
                    drop.Transaction = tx;
                    drop.CommandText = "DROP TABLE IF EXISTS _recalc_keys";
                    drop.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        catch (Exception ex)
        {
            // 失败回滚并抛（daily 旧值保留，数据无损）；调用方放后台线程
            throw new StorageException($"RecalcDerived 重算失败: {ex.Message}", ex);
        }
    }

    public void ResetToday(string? modelKey, int offsetMin)
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var utcToday = TimeMath.UtcDate(now);
        var localToday = TimeMath.LocalDate(now, offsetMin);
        var aStart = TimeMath.UtcDayStart(utcToday);
        var aEnd = aStart + 86400;
        var bStart = TimeMath.LocalDayStart(localToday, offsetMin);
        var bEnd = bStart + 86400;
        var (p, m) = SplitModelKey(modelKey);
        try
        {
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                Exec(tx, "DELETE FROM usage_daily WHERE date = @d" + ModelFilter(p, m, " AND provider = @p AND model = @m"),
                    cmd => { cmd.Parameters.AddWithValue("@d", utcToday); if (p is not null) { cmd.Parameters.AddWithValue("@p", p); cmd.Parameters.AddWithValue("@m", m!); } });
                // 铁律 5：必须删 log 层，否则 [C1] 修复后的 RecalcDerived 会"复活"数据；双窗口保证两口径同时清零
                Exec(tx, "DELETE FROM usage_log WHERE ts >= @s AND ts < @e" + ModelFilter(p, m, " AND provider = @p AND model = @m"),
                    cmd => { cmd.Parameters.AddWithValue("@s", aStart); cmd.Parameters.AddWithValue("@e", aEnd); if (p is not null) { cmd.Parameters.AddWithValue("@p", p); cmd.Parameters.AddWithValue("@m", m!); } });
                Exec(tx, "DELETE FROM usage_log WHERE ts >= @s AND ts < @e" + ModelFilter(p, m, " AND provider = @p AND model = @m"),
                    cmd => { cmd.Parameters.AddWithValue("@s", bStart); cmd.Parameters.AddWithValue("@e", bEnd); if (p is not null) { cmd.Parameters.AddWithValue("@p", p); cmd.Parameters.AddWithValue("@m", m!); } });
                Exec(tx, "DELETE FROM usage_missed WHERE ts >= @s AND ts < @e" + ModelFilter(p, m, " AND provider = @p AND model = @m"),
                    cmd => { cmd.Parameters.AddWithValue("@s", aStart); cmd.Parameters.AddWithValue("@e", aEnd); if (p is not null) { cmd.Parameters.AddWithValue("@p", p); cmd.Parameters.AddWithValue("@m", m!); } });
                Exec(tx, "DELETE FROM usage_missed WHERE ts >= @s AND ts < @e" + ModelFilter(p, m, " AND provider = @p AND model = @m"),
                    cmd => { cmd.Parameters.AddWithValue("@s", bStart); cmd.Parameters.AddWithValue("@e", bEnd); if (p is not null) { cmd.Parameters.AddWithValue("@p", p); cmd.Parameters.AddWithValue("@m", m!); } });
                tx.Commit();
                // 同步清理内存缓冲：缓冲中属于删除窗口的行若残留，随后的 Flush 会"复活"已删数据
                // （破坏 usage_daily ≡ usage_log 派生铁律 5 的用户可见语义）。窗口与 DB 删除范围严格一致。
                lock (_gate)
                {
                    _buffer.RemoveAll(r => (InWindow(r.Ts, aStart, aEnd) || InWindow(r.Ts, bStart, bEnd))
                        && (p is null || (r.Provider == p && r.Model == m)));
                }
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"重置今日失败: {ex.Message}", ex);
        }
    }

    public void DeleteModelData(string modelKey)
    {
        var (p, m) = SplitModelKey(modelKey);
        if (p is null) return; // 原实现防御：无 '/' 键不删
        try
        {
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                // [C8] 三表同步删除（原版漏 usage_missed → 红框残留）
                Exec(tx, "DELETE FROM usage_daily WHERE provider = @p AND model = @m", c => BindKey(c, p!, m!));
                Exec(tx, "DELETE FROM usage_log WHERE provider = @p AND model = @m", c => BindKey(c, p!, m!));
                Exec(tx, "DELETE FROM usage_missed WHERE provider = @p AND model = @m", c => BindKey(c, p!, m!));
                tx.Commit();
                // 同步清理内存缓冲中该模型的行（防止后续 Flush 复活已删数据）
                _buffer.RemoveAll(r => r.Provider == p && r.Model == m);
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"删除模型数据失败: {ex.Message}", ex);
        }
    }

    public void DeleteAllData()
    {
        try
        {
            lock (_gate)
            {
                using var tx = _conn.BeginTransaction();
                Exec(tx, "DELETE FROM usage_daily", _ => { });
                Exec(tx, "DELETE FROM usage_log", _ => { });
                Exec(tx, "DELETE FROM usage_missed", _ => { });
                tx.Commit();
                _buffer.Clear(); // 全删 → 缓冲一并清空（防止 Flush 复活）
            }
        }
        catch (Exception ex)
        {
            throw new StorageException($"删除全部数据失败: {ex.Message}", ex);
        }
    }

    private static void BindKey(SqliteCommand cmd, string p, string m)
    {
        cmd.Parameters.AddWithValue("@p", p);
        cmd.Parameters.AddWithValue("@m", m);
    }

    private static string ModelFilter(string? p, string? m, string clause) =>
        p is null ? string.Empty : clause;

    private void Exec(SqliteTransaction tx, string sql, Action<SqliteCommand> bind)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private static bool InWindow(long ts, long start, long end) => ts >= start && ts < end;

    private static (string? Provider, string? Model) SplitModelKey(string? modelKey)
    {
        if (string.IsNullOrEmpty(modelKey) || modelKey == "all") return (null, null);
        var k = ModelKey.Parse(modelKey); // [C19-②] 只按第一个 '/' 切分
        if (k.Provider.Length == 0 || k.Model.Length == 0) return (null, null);
        return (k.Provider, k.Model);
    }

    // —— 备份与回滚 [C6] ——

    public string Backup()
    {
        Flush(); // 快照一致性
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(_backupDir);
                var stamp = _clock.GetUtcNow().ToString("yyyyMMdd_HHmmss_fff");
                var path = Path.Combine(_backupDir, $"token_monitor_backup_{stamp}.db");
                // VACUUM INTO 要求目标文件不存在；同毫秒重复备份追加序号（原语义）
                for (var i = 1; File.Exists(path); i++)
                {
                    if (i > 9999) throw new StorageException("备份目标文件名冲突过多");
                    path = Path.Combine(_backupDir, $"token_monitor_backup_{stamp}_{i:D3}.db");
                }
                // VACUUM INTO 需要字面量文件名（SQLite 不支持绑定参数）
                var quoted = path.Replace("'", "''");
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = $"VACUUM INTO '{quoted}'";
                cmd.ExecuteNonQuery();
                return path;
            }
        }
        catch (StorageException) { throw; }
        catch (Exception ex)
        {
            throw new StorageException($"备份失败(VACUUM INTO): {ex.Message}", ex);
        }
    }

    public void ReplaceDatabase(string backupPath)
    {
        try
        {
            lock (_gate)
            {
                Flush(); // 先把缓冲写入旧库（附录 B-12）
                _conn.Close();
                _conn.Dispose();
                // [C6-①] 删除 WAL 侧车，避免覆盖主库后回放错 WAL 损坏数据
                foreach (var sidecar in new[] { _dbPath + "-wal", _dbPath + "-shm" })
                {
                    try { File.Delete(sidecar); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
                }
                File.Copy(backupPath, _dbPath, overwrite: true);
                // [C6-②] 以与 New() 完全相同的连接串重开（busy_timeout/WAL 语义保持）
                _conn = new SqliteConnection(_connectionString);
                OpenAndPrepare(_conn);
                _buffer.Clear(); // 缓冲不属于备份状态（附录 B-12）
            }
            Logger.Info("Storage", $"已从备份 {backupPath} 恢复数据库");
        }
        catch (Exception ex)
        {
            throw new StorageException($"回滚数据库失败: {ex.Message}", ex);
        }
    }

    public void CheckpointWal()
    {
        try
        {
            lock (_gate)
            {
                ExecutePragma(_conn, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Storage", $"WAL checkpoint 失败: {ex.Message}");
        }
    }

    private static ModelDailyAggregate ReadDaily(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2),
        r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8),
        r.GetInt64(9), r.GetInt64(10), r.GetInt64(11), r.GetInt64(12), r.GetInt64(13), r.GetInt64(14),
        r.GetInt64(15), r.GetDouble(16), r.GetDouble(17));

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _flushLoopCts?.Cancel();
        try { _flushLoopTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _flushLoopCts?.Dispose();
        Flush();
        try { _conn.Close(); _conn.Dispose(); } catch { }
    }
}
