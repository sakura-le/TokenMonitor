using Microsoft.Data.Sqlite;
using TokenMonitor.Core.Config;
using TokenMonitor.Core.Events;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>旧数据导入回归（G1）：S2 导入路径 + manifest 幂等 + 配置映射。</summary>
public class ImportTests : IDisposable
{
    private readonly TempDir _dir = new();      // 新库目录
    private readonly TempDir _legacy = new();   // 旧项目 data 目录
    private readonly Store _store;
    private readonly EventBus _bus = new();
    private readonly ConfigService _config;
    private readonly PricingEngine _pricing;
    private readonly ImportService _import;

    public ImportTests()
    {
        _store = new Store(_dir.Path, flushInterval: TimeSpan.FromMilliseconds(50));
        _config = new ConfigService(_dir.Path);
        _pricing = new PricingEngine(Path.Combine(_dir.Path, "pricing.json"));
        _import = new ImportService(_store, _config, _pricing,
            (_, _, _, _) => 2.0, (_, _, _, _, _, _, _) => (0.5, 0.25),
            () => { }, _dir.Path, _bus);
    }

    public void Dispose()
    {
        _store.Dispose();
        _config.Dispose();
    }

    /// <summary>构造旧库：缺 total_tokens/estimated 列（旧 schema）+ 缺 reasoning；一行 total=0（有 total 列则回退）。</summary>
    private void CreateLegacyDb()
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_legacy.Path, "token_monitor.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        Exec(conn, """
            CREATE TABLE usage_log (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                ts INTEGER NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL,
                prompt_tokens INTEGER NOT NULL DEFAULT 0,
                prompt_cache_hit INTEGER NOT NULL DEFAULT 0,
                prompt_cache_miss INTEGER NOT NULL DEFAULT 0,
                completion_tokens INTEGER NOT NULL DEFAULT 0
            )
            """);
        Exec(conn, "INSERT INTO usage_log (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss, completion_tokens) VALUES (1000, 'P', 'm', 100, 60, 40, 50)");
        Exec(conn, "INSERT INTO usage_log (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss, completion_tokens) VALUES (2000, 'P', 'm', 10, 30, 99, 5)"); // hit>prompt 违反恒等式 → miss 重算
        Exec(conn, "INSERT INTO usage_log (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss, completion_tokens) VALUES (99999999999, 'P', 'badts', 1, 0, 1, 1)"); // ts 非法 → 丢弃
        Exec(conn, "INSERT INTO usage_log (ts, provider, model, prompt_tokens, prompt_cache_hit, prompt_cache_miss, completion_tokens) VALUES (3000, '', 'empty', 1, 0, 1, 1)"); // provider 空 → 丢弃
        Exec(conn, """
            CREATE TABLE usage_missed (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, provider TEXT NOT NULL, model TEXT NOT NULL, status INTEGER NOT NULL DEFAULT 0, reason TEXT NOT NULL DEFAULT '')
            """);
        Exec(conn, "INSERT INTO usage_missed (ts, provider, model, status, reason) VALUES (1000, 'P', 'm', 200, 'legacy miss')");
        Exec(conn, """
            CREATE TABLE op_log (id INTEGER PRIMARY KEY AUTOINCREMENT, ts INTEGER NOT NULL, model TEXT NOT NULL, action TEXT NOT NULL, detail TEXT NOT NULL, effective_from TEXT NOT NULL DEFAULT '')
            """);
        Exec(conn, "INSERT INTO op_log (ts, model, action, detail, effective_from) VALUES (1000, 'P/m', 'set_multiplier', 'legacy op', '')");
        // 旧 JSON 配置
        File.WriteAllText(Path.Combine(_legacy.Path, "config.json"),
            """{ "listen_addr": "127.0.0.1:7777", "providers": [ {"name":"L","base_url":"https://legacy.example.com","api_key":"sk-legacy","model_prefix":["l-"]} ] }""");
        File.WriteAllText(Path.Combine(_legacy.Path, "pricing.json"),
            """{ "multipliers": { "P/m": { "periods": [ {"start":0,"end":24,"rate":3} ] } } }""");
        File.WriteAllText(Path.Combine(_legacy.Path, "settings.json"),
            """{ "offset_min": 480, "effective_date_mode": "local", "ball_opacity": 0 }""");
        File.WriteAllText(Path.Combine(_legacy.Path, "multiplier_states.json"),
            """{ "P/m": true }""");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Import_LegacyDb_TotalFallbackAndCleaning()
    {
        // [S2b 导入路径] 旧库缺 total 列 → 落库即归一 total=prompt+comp（此后读侧永不再回退）；
        // 恒等式重算；非法行丢弃；daily 不导而由 RecalcDerived 重建
        CreateLegacyDb();
        var preview = _import.Preview(new ImportOptions(_legacy.Path, false, ImportMode.MergeIfNew));
        Assert.True(preview.LegacyDbFound);
        Assert.Equal(4, preview.EstimatedUsageRows);
        Assert.Equal(1, preview.EstimatedMissedRows);
        Assert.Equal(1, preview.EstimatedOpRows);
        Assert.True(preview.ConfigFound);
        Assert.True(preview.PricingFound);
        Assert.True(preview.SettingsFound);
        Assert.True(preview.MultiplierStatesFound);
        Assert.False(preview.AlreadyImported);
        Assert.NotNull(preview.SourceSha256);

        var result = _import.Import(new ImportOptions(_legacy.Path, false, ImportMode.MergeIfNew));

        Assert.True(result.Success);
        Assert.Equal(2, result.ImportedUsageRows);  // 2 合法行
        Assert.Equal(1, result.ImportedMissedRows);
        Assert.Equal(1, result.ImportedOpRows);
        Assert.True(File.Exists(result.BackupPath));

        var logs = _store.GetLogsByRange(0, long.MaxValue);
        Assert.True(logs.Count == 2, "应恰好 2 条合法行");
        var r1 = logs.Single(l => l.Ts == 1000);
        Assert.Equal(150, r1.TotalTokens);  // 旧库无 total 列 → prompt+comp = 100+50
        Assert.Equal(40, r1.CacheMissTokens);
        var r2 = logs.Single(l => l.Ts == 2000);
        Assert.Equal(0, r2.CacheMissTokens); // §5.9 清洗：miss = clamp(prompt-hit,0,prompt) = 0（hit 不钳制）
        Assert.Equal(30, r2.CacheHitTokens);
        Assert.Single(_store.GetAllModels()); // daily 已由 RecalcDerived 重建（[C1]）

        // manifest 已写 → 重复导入被拒（MergeIfNew）
        var again = Assert.Throws<ImportException>(() =>
            _import.Import(new ImportOptions(_legacy.Path, false, ImportMode.MergeIfNew)));
        Assert.Contains("导入过", again.Message);
        Assert.True(_import.Preview(new ImportOptions(_legacy.Path, false, ImportMode.MergeIfNew)).AlreadyImported);
    }

    [Fact]
    public void Import_ReplaceAll_ClearsAndReimports()
    {
        CreateLegacyDb();
        // 新库预置已有数据 → ReplaceAll 清空后重导
        _store.AddUsage([new UsageLogRow(TimeMath.UtcDayStart("2026-09-18") + 3600, "P", "existing", 1, 0, 1, 1, 0, 2)]);
        _store.Flush();

        _import.Import(new ImportOptions(_legacy.Path, false, ImportMode.ReplaceAll));

        var logs = _store.GetLogsByRange(0, long.MaxValue);
        Assert.True(logs.Count == 2, "应恰好 2 条合法行");
        Assert.DoesNotContain(logs, l => l.Model == "existing");
    }

    [Fact]
    public void Import_JsonConfigs_MappedPerContract()
    {
        // §6.4 映射：api_key 默认不迁移；strip_params 一律默认；default_provider=null；settings 补齐
        CreateLegacyDb();
        _import.Import(new ImportOptions(_legacy.Path, false, ImportMode.MergeIfNew));

        var provider = Assert.Single(_config.Proxy.Providers);
        Assert.Equal("L", provider.Name);
        Assert.Equal("", provider.ApiKey);                       // 默认不迁移（安全红线）
        Assert.Null(_config.Proxy.DefaultProvider);              // 新版未知模型 404
        Assert.Equal(["enable_thinking", "reasoning_effort"], provider.StripParams); // [C15] 默认剥离
        Assert.Equal(480, _config.Settings.OffsetMin);
        Assert.Equal("local", _config.Settings.EffectiveDateMode);
        Assert.Equal(255, _config.Settings.BallOpacity);         // ≤0 → 255
        Assert.True(_config.Settings.BallTopmost);               // 缺省 true
        Assert.True(_config.Ui.CardMultiplierView["P/m"]);       // multiplier_states 并入
        // pricing 导入：旧平铺 → history，键 P/m
        Assert.True(_pricing.Document.Multipliers.ContainsKey("P/m"));

        // MigrateApiKeys=true → 密钥以 dpapi: 落盘
        var result2 = _import.Import(new ImportOptions(_legacy.Path, true, ImportMode.ReplaceAll));
        Assert.True(result2.ConfigImported);
        Assert.Equal("sk-legacy", _config.Proxy.Providers.Single().ApiKey); // 内存态明文
        var text = File.ReadAllText(Path.Combine(_dir.Path, "config.json"));
        Assert.Contains("dpapi:", text);
        Assert.DoesNotContain("sk-legacy", text);
    }

    [Fact]
    public void Import_MissingDb_PreviewStillReturns()
    {
        // 目录缺 db → Preview 恒可返回 + Warnings 说明
        var preview = _import.Preview(new ImportOptions(_legacy.Path, false, ImportMode.MergeIfNew));
        Assert.False(preview.LegacyDbFound);
        Assert.NotEmpty(preview.Warnings);
    }

    [Fact]
    public void Import_Failure_RollsBackTransaction()
    {
        // 事务回滚：导入过程中失败 → 新库保持原状
        CreateLegacyDb();
        _store.AddUsage([new UsageLogRow(TimeMath.UtcDayStart("2026-09-18") + 3600, "P", "keep", 1, 0, 1, 1, 0, 2)]);
        _store.Flush();
        // 旧库损坏 → 打开即失败（事务未开始）→ 新库原状
        File.WriteAllText(Path.Combine(_legacy.Path, "token_monitor.db"), "definitely not a sqlite db");

        Assert.ThrowsAny<Exception>(() => _import.Import(new ImportOptions(_legacy.Path, false, ImportMode.MergeIfNew)));
        Assert.Single(_store.GetLogsByRange(0, long.MaxValue)); // 新库原状（仅 keep 行）
    }
}
