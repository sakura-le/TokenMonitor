using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using TokenMonitor.Core.Config;
using TokenMonitor.Core.Pricing;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Storage;

/// <summary>旧项目数据一键导入契约（01-§2.3.8 / §6）。方法级串行。</summary>
public interface IImportService
{
    /// <summary>扫描旧目录，产出预览（不修改任何文件）。目录缺 db → Warnings 说明、Preview 恒可返回。</summary>
    ImportPreview Preview(ImportOptions options);

    /// <summary>执行导入：先 Backup 新库 → 事务导入 → RecalcDerived → RebuildToday → 写 manifest → 触发 ImportCompleted。
    /// 失败抛 ImportException（导入事务已回滚，新库保持原状）。</summary>
    ImportResult Import(ImportOptions options);
}

/// <summary>导入完成事件（导入向导、QueryService 缓存失效）。</summary>
public sealed record ImportCompleted(ImportResult Result) : TokenMonitor.Core.Events.IEvent;

/// <summary>IImportService 默认实现（02-§5.9 / 01-§6）：旧库四表逐列映射清洗；daily 不导（铁律 5）由 log 重算。</summary>
public sealed class ImportService : IImportService
{
    private readonly Store _store;
    private readonly IConfigService _config;
    private readonly IPricingEngine _pricing;
    private readonly RateFunc? _rateFn;
    private readonly CostFunc? _costFn;
    private readonly Action _rebuildToday;
    private readonly string _manifestPath;
    private readonly string _backupDir;
    private readonly TokenMonitor.Core.Events.IEventBus _bus;
    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    public ImportService(Store store, IConfigService config, IPricingEngine pricing,
                         RateFunc? rateFn, CostFunc? costFn, Action rebuildToday,
                         string dataDir, TokenMonitor.Core.Events.IEventBus bus, TimeProvider? clock = null)
    {
        _store = store;
        _config = config;
        _pricing = pricing;
        _rateFn = rateFn;
        _costFn = costFn;
        _rebuildToday = rebuildToday;
        _manifestPath = Path.Combine(dataDir, "import_manifest.json");
        _backupDir = Path.Combine(dataDir, "backups");
        _bus = bus;
        _clock = clock ?? TimeProvider.System;
    }

    public ImportPreview Preview(ImportOptions options)
    {
        var warnings = new List<string>();
        var legacyDb = Path.Combine(options.LegacyDataDir, "token_monitor.db");
        long usageRows = 0, missedRows = 0, opRows = 0;
        string? sha = null;
        var found = File.Exists(legacyDb);
        if (!found)
        {
            warnings.Add($"未找到旧库 {legacyDb}，将仅导入 JSON 配置");
        }
        else
        {
            try
            {
                sha = Sha256(legacyDb);
                using var conn = OpenLegacy(legacyDb);
                var cols = Columns(conn, "usage_log");
                if (cols.Count == 0) warnings.Add("旧库 usage_log 表不存在");
                usageRows = CountRows(conn, "usage_log");
                missedRows = CountRows(conn, "usage_missed");
                opRows = CountRows(conn, "op_log");
            }
            catch (Exception ex)
            {
                warnings.Add($"旧库不可读: {ex.Message}");
            }
        }
        return new ImportPreview(
            found, usageRows, missedRows, opRows,
            File.Exists(Path.Combine(options.LegacyDataDir, "config.json")),
            File.Exists(Path.Combine(options.LegacyDataDir, "pricing.json")),
            File.Exists(Path.Combine(options.LegacyDataDir, "settings.json")),
            File.Exists(Path.Combine(options.LegacyDataDir, "multiplier_states.json")),
            sha is not null && IsAlreadyImported(sha, out _),
            sha, warnings);
    }

    public ImportResult Import(ImportOptions options)
    {
        lock (_gate)
        {
            var legacyDb = Path.Combine(options.LegacyDataDir, "token_monitor.db");
            string? sha = File.Exists(legacyDb) ? Sha256(legacyDb) : null;
            if (sha is not null && IsAlreadyImported(sha, out var prev) &&
                options.Mode == ImportMode.MergeIfNew)
            {
                throw new ImportException($"该旧库已于 {prev} 导入过（如需覆盖请选择 ReplaceAll 模式）");
            }

            var backup = _store.Backup(); // 先备份新库（§6.1）
            long usageRows = 0, missedRows = 0, opRows = 0;
            try
            {
                if (sha is not null)
                {
                    using var legacy = OpenLegacy(legacyDb);
                    var logCols = Columns(legacy, "usage_log");
                    var missedCols = Columns(legacy, "usage_missed");
                    var opCols = Columns(legacy, "op_log");
                    var batch = NextBatchNo();

                    using var conn = new SqliteConnection(StoreConnectionString());
                    conn.Open();
                    StorePragma(conn);
                    using var tx = conn.BeginTransaction();
                    if (options.Mode == ImportMode.ReplaceAll)
                    {
                        // ReplaceAll：清空四表后重新全量导入（§6.5）
                        Exec(conn, tx, "DELETE FROM usage_log");
                        Exec(conn, tx, "DELETE FROM usage_daily");
                        Exec(conn, tx, "DELETE FROM usage_missed");
                        Exec(conn, tx, "DELETE FROM op_log");
                    }
                    usageRows = ImportUsageLog(conn, tx, legacy, logCols, batch);
                    missedRows = ImportMissed(conn, tx, legacy, missedCols);
                    opRows = ImportOpLog(conn, tx, legacy, opCols);
                    tx.Commit();
                }
            }
            catch (ImportException) { throw; }
            catch (Exception ex)
            {
                throw new ImportException($"导入失败（事务已回滚，新库保持原状）: {ex.Message}", ex);
            }

            // —— JSON 配置导入（config/settings/pricing 不受 manifest 限制，最后者胜 §6.5）——
            var configImported = ImportProxyConfig(options);
            var settingsImported = ImportSettings(options);
            ImportMultiplierStates(options);
            ImportCalibrateState(options);
            var pricingImported = ImportPricing(options);

            // —— 收尾（§6.5）：RecalcDerived → RebuildToday → 缓存失效(ImportCompleted) → LogOp ——
            _store.RecalcDerived(_rateFn, _costFn);
            _rebuildToday();
            if (sha is not null) AppendManifest(sha, options.LegacyDataDir, usageRows, missedRows, opRows);
            _store.LogOp("all", "import_legacy", $"导入旧数据 {usageRows} 条, 来源 {options.LegacyDataDir}", "");
            var result = new ImportResult(true, null, usageRows, missedRows, opRows,
                configImported, pricingImported, settingsImported, backup);
            _bus.Publish(new ImportCompleted(result));
            return result;
        }
    }

    // —— 表导入（02-§5.9 清洗规则）——

    private long ImportUsageLog(SqliteConnection conn, SqliteTransaction tx, SqliteConnection legacy,
                                IReadOnlyDictionary<string, bool> cols, long batch)
    {
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var maxTs = now + 86400;
        var hasTotal = cols.ContainsKey("total_tokens");
        var hasEstimated = cols.ContainsKey("estimated");
        var hasReasoning = cols.ContainsKey("reasoning_tokens");
        var dropped = 0;
        using var select = legacy.CreateCommand();
        select.CommandText = "SELECT ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss, completion_tokens"
            + (hasReasoning ? ", reasoning_tokens" : ", 0")
            + (hasTotal ? ", total_tokens" : ", 0")
            + (hasEstimated ? ", estimated" : ", 0")
            + " FROM usage_log ORDER BY id ASC";
        using var reader = select.ExecuteReader();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO usage_log (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss,
                                   completion_tokens, reasoning_tokens, total_tokens, estimated, import_batch)
            VALUES (@ts, @provider, @model, @p, @hit, @miss, @comp, @reas, @total, @estimated, @batch)
            """;
        var pTs = insert.Parameters.Add("@ts", SqliteType.Integer);
        var pProv = insert.Parameters.Add("@provider", SqliteType.Text);
        var pModel = insert.Parameters.Add("@model", SqliteType.Text);
        var pP = insert.Parameters.Add("@p", SqliteType.Integer);
        var pH = insert.Parameters.Add("@hit", SqliteType.Integer);
        var pM = insert.Parameters.Add("@miss", SqliteType.Integer);
        var pC = insert.Parameters.Add("@comp", SqliteType.Integer);
        var pR = insert.Parameters.Add("@reas", SqliteType.Integer);
        var pT = insert.Parameters.Add("@total", SqliteType.Integer);
        var pE = insert.Parameters.Add("@estimated", SqliteType.Integer);
        var pB = insert.Parameters.Add("@batch", SqliteType.Integer);
        long count = 0;
        while (reader.Read())
        {
            var ts = reader.GetInt64(0);
            var provider = (reader.IsDBNull(1) ? "" : reader.GetString(1)).Trim();
            var model = (reader.IsDBNull(2) ? "" : reader.GetString(2)).Trim();
            if (ts <= 0 || ts > maxTs || provider.Length == 0 || model.Length == 0) { dropped++; continue; }
            var prompt = Math.Max(0, reader.GetInt64(3));
            var hit = Math.Max(0, reader.GetInt64(4));
            var miss = Math.Clamp(prompt - hit, 0, prompt); // 重算，不信任旧值（旧库可能违反恒等式）
            var comp = Math.Max(0, reader.GetInt64(6));   // completion_tokens
            var reas = Math.Min(Math.Max(0, reader.GetInt64(7)), comp); // reasoning ⊆ completion
            var total = Math.Max(0, reader.GetInt64(8));  // total_tokens（缺列时为 0 → 回退）
            total = total > 0 ? total : prompt + comp; // [S2] 落库即归一，此后读侧永不再回退
            pTs.Value = ts;
            pProv.Value = provider;
            pModel.Value = model;
            pP.Value = prompt;
            pH.Value = hit;
            pM.Value = miss;
            pC.Value = comp;
            pR.Value = reas;
            pT.Value = total;
            pE.Value = hasEstimated && !reader.IsDBNull(9) && reader.GetInt64(9) != 0 ? 1L : 0L;
            pB.Value = batch;
            insert.ExecuteNonQuery();
            count++;
        }
        if (dropped > 0) Logger.Warn("Import", $"usage_log 丢弃 {dropped} 行（ts 非法或 provider/model 为空）");
        return count;
    }

    private long ImportMissed(SqliteConnection conn, SqliteTransaction tx, SqliteConnection legacy,
                              IReadOnlyDictionary<string, bool> cols)
    {
        if (cols.Count == 0) return 0;
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var maxTs = now + 86400;
        using var select = legacy.CreateCommand();
        select.CommandText = "SELECT ts, provider, model, status, reason FROM usage_missed";
        using var reader = select.ExecuteReader();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO usage_missed (ts, provider, model, status, reason) VALUES (@ts, @p, @m, @s, @r)";
        insert.Parameters.Add("@ts", SqliteType.Integer);
        insert.Parameters.Add("@p", SqliteType.Text);
        insert.Parameters.Add("@m", SqliteType.Text);
        insert.Parameters.Add("@s", SqliteType.Integer);
        insert.Parameters.Add("@r", SqliteType.Text);
        long count = 0;
        while (reader.Read())
        {
            var ts = reader.GetInt64(0);
            if (ts <= 0 || ts > maxTs) continue; // ts 非法行丢弃
            insert.Parameters["@ts"].Value = ts;
            insert.Parameters["@p"].Value = reader.GetString(1);
            insert.Parameters["@m"].Value = reader.GetString(2);
            insert.Parameters["@s"].Value = reader.GetInt64(3);
            insert.Parameters["@r"].Value = reader.IsDBNull(4) ? "" : reader.GetString(4);
            insert.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    private long ImportOpLog(SqliteConnection conn, SqliteTransaction tx, SqliteConnection legacy,
                             IReadOnlyDictionary<string, bool> cols)
    {
        if (cols.Count == 0) return 0;
        using var select = legacy.CreateCommand();
        select.CommandText = "SELECT ts, model, action, detail, effective_from FROM op_log";
        using var reader = select.ExecuteReader();
        using var insert = conn.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO op_log (ts, model, action, detail, effective_from) VALUES (@ts, @m, @a, @d, @e)";
        insert.Parameters.Add("@ts", SqliteType.Integer);
        insert.Parameters.Add("@m", SqliteType.Text);
        insert.Parameters.Add("@a", SqliteType.Text);
        insert.Parameters.Add("@d", SqliteType.Text);
        insert.Parameters.Add("@e", SqliteType.Text);
        long count = 0;
        while (reader.Read())
        {
            insert.Parameters["@ts"].Value = reader.GetInt64(0);
            insert.Parameters["@m"].Value = reader.GetString(1);
            insert.Parameters["@a"].Value = reader.GetString(2);
            insert.Parameters["@d"].Value = reader.IsDBNull(3) ? "" : reader.GetString(3);
            insert.Parameters["@e"].Value = reader.IsDBNull(4) ? "" : reader.GetString(4);
            insert.ExecuteNonQuery();
            count++;
        }
        return count;
    }

    // —— JSON 配置导入（01-§6.4 映射表）——

    private bool ImportProxyConfig(ImportOptions options)
    {
        var path = Path.Combine(options.LegacyDataDir, "config.json");
        if (!File.Exists(path)) return false;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject obj) return false;
            var providers = new List<ProviderConfig>();
            if (obj["providers"] is JsonArray arr)
            {
                foreach (var p in arr.OfType<JsonObject>())
                {
                    var name = p["name"]?.GetValue<string>() ?? "";
                    var baseUrl = p["base_url"]?.GetValue<string>() ?? "";
                    var prefixes = (p["model_prefix"] as JsonArray)?.OfType<JsonNode>()
                        .Select(x => x.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).ToList() ?? [];
                    int? maxTokens = p["max_tokens"] is { } mt && mt.GetValueKind() == JsonValueKind.Number ? (int)mt.GetValue<double>() : null;
                    // api_key 默认不迁移（安全红线）；仅勾选迁移时读旧明文（SaveProxy 落盘时加密为 dpapi:）
                    var legacyKey = p["api_key"]?.GetValue<string>() ?? "";
                    var apiKey = options.MigrateApiKeys && legacyKey.Length > 0 ? legacyKey : "";
                    // [C15] 导入旧配置一律补默认 strip_params（保持旧全局剥离行为）；default_provider=null（未知模型 404）
                    providers.Add(new ProviderConfig(name, baseUrl, apiKey, prefixes, maxTokens,
                        ["enable_thinking", "reasoning_effort"]));
                }
            }
            var listen = obj["listen_addr"]?.GetValue<string>();
            var cfg = new ProxyConfig(listen ?? _config.Proxy.ListenAddr, null, providers);
            _config.SaveProxy(cfg);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Import", $"导入旧 config.json 失败: {ex.Message}");
            return false;
        }
    }

    private bool ImportPricing(ImportOptions options)
    {
        var path = Path.Combine(options.LegacyDataDir, "pricing.json");
        if (!File.Exists(path)) return false;
        try
        {
            var doc = PricingDocumentIO.FromNode(JsonNode.Parse(File.ReadAllText(path)));
            _pricing.ReplaceDocument(doc.Normalize());
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Import", $"导入旧 pricing.json 失败: {ex.Message}");
            return false;
        }
    }

    private bool ImportSettings(ImportOptions options)
    {
        var path = Path.Combine(options.LegacyDataDir, "settings.json");
        if (!File.Exists(path)) return false;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject o) return false;
            var offset = o["offset_min"] is { } om ? (int)om.GetValue<double>() : 0;
            var mode = o["effective_date_mode"]?.GetValue<string>() is { } m && (m == "utc" || m == "local") ? m : "utc";
            var opacity = o["ball_opacity"] is { } bo ? (int)bo.GetValue<double>() : 255;
            if (opacity <= 0) opacity = 255;
            // 缺省视为 true（原可空 bool 语义）；仅显式 false 才为 false
            var topmost = o["ball_topmost"]?.GetValueKind() != JsonValueKind.False;
            _config.SaveSettings(new AppSettings(offset, mode, opacity, topmost));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Import", $"导入旧 settings.json 失败: {ex.Message}");
            return false;
        }
    }

    private void ImportMultiplierStates(ImportOptions options)
    {
        var path = Path.Combine(options.LegacyDataDir, "multiplier_states.json");
        if (!File.Exists(path)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject o) return;
            var map = new Dictionary<string, bool>(_config.Ui.CardMultiplierView, StringComparer.Ordinal);
            foreach (var kv in o)
                if (kv.Value is { } v)
                    map[kv.Key] = v.GetValueKind() == JsonValueKind.True;
            _config.SaveUiState(_config.Ui with { CardMultiplierView = map });
        }
        catch (Exception ex)
        {
            Logger.Warn("Import", $"导入旧 multiplier_states.json 失败: {ex.Message}");
        }
    }

    private void ImportCalibrateState(ImportOptions options)
    {
        var path = Path.Combine(options.LegacyDataDir, "calibrate_state.json");
        if (!File.Exists(path)) return;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node is not JsonObject o) return;
            var lastBackup = o["last_backup"]?.GetValue<string>() ?? "";
            if (o["has_calibrated"]?.GetValueKind() == JsonValueKind.True && File.Exists(lastBackup))
            {
                Directory.CreateDirectory(_backupDir);
                var imported = Path.Combine(_backupDir, "imported_" + Path.GetFileName(lastBackup));
                File.Copy(lastBackup, imported, overwrite: true);
                WriteCalibrateState(new CalibrateState(true, imported));
            }
            else
            {
                WriteCalibrateState(new CalibrateState(false, ""));
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Import", $"导入旧 calibrate_state.json 失败: {ex.Message}");
        }
    }

    private void WriteCalibrateState(CalibrateState s)
    {
        // 与 CalibrationService 共用 data/calibrate_state.json（沿用原文件名与字段）
        var statePath = Path.Combine(Path.GetDirectoryName(_manifestPath)!, "calibrate_state.json");
        var dto = new { has_calibrated = s.HasCalibrated, last_backup = s.LastBackup };
        File.WriteAllText(statePath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }),
            new System.Text.UTF8Encoding(false));
    }

    // —— manifest（§6.5 幂等依据）——

    private bool IsAlreadyImported(string sha, out string importedAtUtc)
    {
        importedAtUtc = "";
        try
        {
            if (!File.Exists(_manifestPath)) return false;
            var arr = JsonNode.Parse(File.ReadAllText(_manifestPath)) as JsonArray;
            if (arr is null) return false;
            foreach (var item in arr.OfType<JsonObject>())
            {
                if (item["source_sha256"]?.GetValue<string>() == sha)
                {
                    importedAtUtc = item["imported_at_utc"]?.GetValue<string>() ?? "";
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Import", $"读取导入清单失败: {ex.Message}");
        }
        return false;
    }

    private long NextBatchNo()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return 1;
            var arr = JsonNode.Parse(File.ReadAllText(_manifestPath)) as JsonArray;
            return (arr?.Count ?? 0) + 1;
        }
        catch
        {
            return 1;
        }
    }

    private void AppendManifest(string sha, string sourcePath, long usage, long missed, long op)
    {
        try
        {
            var arr = File.Exists(_manifestPath)
                ? JsonNode.Parse(File.ReadAllText(_manifestPath)) as JsonArray ?? new JsonArray()
                : new JsonArray();
            arr.Add(new JsonObject
            {
                ["source_sha256"] = sha,
                ["source_path"] = sourcePath,
                ["imported_at_utc"] = _clock.GetUtcNow().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                ["counts"] = new JsonObject
                {
                    ["usage_log"] = usage,
                    ["usage_missed"] = missed,
                    ["op_log"] = op,
                },
            });
            File.WriteAllText(_manifestPath, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Logger.Warn("Import", $"写入导入清单失败: {ex.Message}");
        }
    }

    // —— 基元 ——

    private static SqliteConnection OpenLegacy(string path)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        conn.Open();
        return conn;
    }

    private string StoreConnectionString() =>
        new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(Path.GetDirectoryName(_manifestPath)!, "token_monitor.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 5,
            Pooling = false,
        }.ToString();

    private static void StorePragma(SqliteConnection conn)
    {
        using var busy = conn.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        busy.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static IReadOnlyDictionary<string, bool> Columns(SqliteConnection conn, string table)
    {
        // PRAGMA table_info 内省每表实际列集（旧库可能缺 total_tokens/estimated 列，§6.3）
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) map[reader.GetString(1)] = true;
        }
        catch
        {
            // 表不存在 → 空集合
        }
        return map;
    }

    private static long CountRows(SqliteConnection conn, string table)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
        catch
        {
            return 0;
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
