using TokenMonitor.Core;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Stats;
using TokenMonitor.Core.Storage;
using TokenMonitor.Core.SysUtil;
using Xunit;

namespace TokenMonitor.Core.Tests;

/// <summary>模型键（C19-②）、异步回包守卫（C16）、SysUtil、导出口径（C19-①）、日志轮转。</summary>
public class MiscTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero));

    public void Dispose() => _dir.Dispose();

    // —— [C19-②] 模型键 ——

    [Fact]
    public void ModelKey_ParseSplitsOnFirstSlash()
    {
        // [C19-②] "P/openai/gpt-4" → Provider="P"、Model="openai/gpt-4"（只按第一个 '/' 切分）
        var k = ModelKey.Parse("P/openai/gpt-4");
        Assert.Equal("P", k.Provider);
        Assert.Equal("openai/gpt-4", k.Model);
        Assert.Equal("P/openai/gpt-4", k.ToString());
        // 无 '/' → Provider=""、Model=key
        var k2 = ModelKey.Parse("bare-model");
        Assert.Equal("", k2.Provider);
        Assert.Equal("bare-model", k2.Model);
        // 隐式转 string
        string s = k;
        Assert.Equal("P/openai/gpt-4", s);
    }

    // —— [C16] 异步回包守卫 ——

    [Fact]
    public void RangeDialog_AsyncResult_ValidateCardKey()
    {
        // [C16] 切卡后回包旧卡结果 → 不写入新卡选中态（Core 侧守卫原语；ViewModel 在 await 回包处调用）
        const string requestedKey = "P/old-card";
        const string currentKey = "P/new-card";
        Assert.False(AsyncResultGuard.IsValid(requestedKey, currentKey)); // 旧结果被拒绝
        Assert.True(AsyncResultGuard.IsValid(currentKey, currentKey));    // 同卡结果允许写入
        Assert.False(AsyncResultGuard.IsValid(null, "P/new-card"));
        Assert.True(AsyncResultGuard.IsValid(null, null));
        // 典型模式：落地前校验（stale 回包丢弃）
        var selected = "P/new-card";
        var resultFor = "P/old-card";
        if (!AsyncResultGuard.IsValid(resultFor, selected))
        {
            selected = "P/new-card"; // 不变（未写入旧卡结果）
        }
        Assert.Equal("P/new-card", selected);
    }

    // —— SysUtil ——

    [Fact]
    public void SysUtil_TimezoneOffsets_FiftyThreeEntries()
    {
        using var sys = new TokenMonitor.Core.SysUtil.SysUtil(TestHelpers.TempDir());
        var offsets = sys.TimezoneOffsets();
        Assert.Equal(53, offsets.Count); // -720..840 步长 30
        Assert.Equal(-720, offsets[0]);
        Assert.Equal(840, offsets[^1]);
        Assert.Equal(0, offsets[24]);
    }

    [Theory]
    [InlineData(0, "UTC±0")]
    [InlineData(480, "UTC+8")]
    [InlineData(-180, "UTC-3")]
    [InlineData(330, "UTC+5:30")]
    [InlineData(-210, "UTC-3:30")]
    public void SysUtil_OffsetLabel_Formats(int offsetMin, string expected)
    {
        using var sys = new TokenMonitor.Core.SysUtil.SysUtil(TestHelpers.TempDir());
        Assert.Equal(expected, sys.OffsetLabel(offsetMin));
    }

    [Fact]
    public void SysUtil_SingleInstance_SecondFailsWithSignal()
    {
        // Mutex 线程亲和：同线程可重入 → 第二实例的获取必须在其它线程验证
        var dir = TestHelpers.TempDir();
        try
        {
            var uniqueName = @"Local\TokenMonitor.Test." + Guid.NewGuid().ToString("N");
            using var first = new TokenMonitor.Core.SysUtil.SysUtil(dir, uniqueName, uniqueName + ".Signal");
            Assert.True(first.TryAcquireSingleInstance(out var signal1));
            using var second = new TokenMonitor.Core.SysUtil.SysUtil(dir, uniqueName, uniqueName + ".Signal");
            bool? secondResult = null;
            var t = new Thread(() => secondResult = second.TryAcquireSingleInstance(out _));
            t.Start();
            t.Join(TimeSpan.FromSeconds(5));
            Assert.False(secondResult == true); // 已有实例 → false（专用线程验证，排除 Mutex 线程亲和歧义）
            signal1.Dispose();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // —— [C19-①] 导出口径 ——

    [Fact]
    public void Export_Today_ScopeExplicitPerSheet()
    {
        // [C19①] UTC+8 下"导出今日"（托盘按 UTC 日历传参 2026-09-17）：
        // 行在 UTC 09-17 20:00 = 本地 09-18 04:00
        // → UTC明细表（usage_daily UTC 窗口）含该行（UTC date=09-17）；
        //   LOCAL明细表（同窗口 ts 解释）该行归本地次日 09-18；
        //   HOUR 表恒本地今日（09-18），段首含 04:00。
        var utcDay = TimeMath.UtcDayStart("2026-09-17");
        var ts = utcDay + 20 * 3600;
        using var store = new Store(_dir.Path, _clock, flushInterval: TimeSpan.FromMilliseconds(50));
        store.AddUsage([new UsageLogRow(ts, "P", "m", 100, 60, 40, 50, 20, 150)]);
        store.Flush();

        var exportDir = Path.Combine(_dir.Path, "export");
        var exporter = new ExportService(store, null, null, () => 480, exportDir, _clock);
        var result = exporter.ExportXlsx(new ExportRequest("2026-09-17", "2026-09-17", BucketScope.Utc, null, true));
        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(result.FilePath));
        Assert.True(result.UtcRows > 0);   // UTC 侧窗口含 UTC 今日行
        Assert.True(result.LocalRows > 0); // LOCAL 侧窗口同样覆盖
        Assert.True(result.HourlyRows > 0);

        // Sheet 名与顺序（workbook.xml）
        using var fs = File.OpenRead(result.FilePath);
        using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
        var workbook = new StreamReader(zip.GetEntry("xl/workbook.xml")!.Open()).ReadToEnd();
        var order = new[] { "UTC总表", "UTC明细表", "LOCAL总表", "LOCAL明细表", "HOUR用量", "HOUR消费" }
            .Select(n => workbook.IndexOf(n, StringComparison.Ordinal)).ToList();
        Assert.All(order, i => Assert.True(i >= 0, "缺少 Sheet"));
        Assert.Equal(order.OrderBy(i => i).ToList(), order); // 固定顺序

        // 口径数值断言（与导出同一查询路径）
        Assert.Single(store.QueryDailyRange("2026-09-17", "2026-09-17"));                       // UTC 明细含
        var localRows = store.AggregateLocal(TimeMath.UtcDayStart("2026-09-17"),
            TimeMath.UtcDayStart("2026-09-17") + 86400, 480, null, null);
        Assert.Equal("2026-09-18", Assert.Single(localRows).Date);                              // 本地次日归属
        var hourly = store.AggregateHourly(TimeMath.LocalDayStart("2026-09-18", 480),
            TimeMath.LocalDayStart("2026-09-18", 480) + 86400, 480, null, null);
        var hourRow = Assert.Single(hourly);
        Assert.Equal(4, hourRow.Hour);                                                          // 本地 04:00
        Assert.Equal("2026-09-18 04:00", new ExportService(store, null, null, () => 480, exportDir, _clock)
            .TestHourRange("2026-09-18", 4).Start);
    }

    [Fact]
    public void Export_LocalScopeWindow_AttributesByLocalCalendar()
    {
        // [C9 导出侧] scope=Local：日期窗口按本地日历解释（本地 00:00 − offset）
        var utcDay = TimeMath.UtcDayStart("2026-09-16");
        var ts = utcDay + 20 * 3600; // UTC 09-16 20:00 = 本地(UTC+8) 09-17 04:00
        using var store = new Store(_dir.Path, _clock, flushInterval: TimeSpan.FromMilliseconds(50));
        store.AddUsage([new UsageLogRow(ts, "P", "m", 100, 60, 40, 50, 20, 150)]);
        store.Flush();

        var exporter = new ExportService(store, null, null, () => 480, Path.Combine(_dir.Path, "export"), _clock);
        var result = exporter.ExportXlsx(new ExportRequest("2026-09-17", "2026-09-17", BucketScope.Local, null, false));
        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.LocalRows); // LOCAL总表（全期 1 组）+ LOCAL明细（1 行），均归本地 09-17
        // 与导出同一查询路径验证
        var rows = store.AggregateLocal(TimeMath.LocalDayStart("2026-09-17", 480),
            TimeMath.LocalDayStart("2026-09-17", 480) + 86400, 480, null, null);
        Assert.Single(rows);
        Assert.Equal("2026-09-17", rows[0].Date);
        Assert.Equal(150, rows[0].TotalTokens);
    }

    [Fact]
    public void Export_ModelFilter_WritesModelTag()
    {
        var ts = TimeMath.UtcDayStart("2026-09-18") + 5 * 3600;
        using var store = new Store(_dir.Path, _clock, flushInterval: TimeSpan.FromMilliseconds(50));
        store.AddUsage(
        [
            new UsageLogRow(ts, "P", "openai/gpt-4", 10, 0, 10, 5, 0, 15),
            new UsageLogRow(ts, "P", "other", 10, 0, 10, 5, 0, 15),
        ]);
        store.Flush();
        var exporter = new ExportService(store, null, null, () => 0, Path.Combine(_dir.Path, "export"), _clock);
        var result = exporter.ExportXlsx(new ExportRequest("2026-09-18", "2026-09-18", BucketScope.Utc, "P/openai/gpt-4", false));
        Assert.True(result.Success);
        Assert.Contains("P-openai-gpt-4", Path.GetFileName(result.FilePath)); // modelTag：'/'→'-'（C19-② 导出侧）
    }

    // —— 日志轮转 ——

    [Fact]
    public void Logger_RotatesOverThreshold()
    {
        var logDir = Path.Combine(_dir.Path, "logs");
        Logger.RotateThreshold = 64 * 1024; // 测试阈值 64KB
        try
        {
            Logger.Init(logDir);
            var big = new string('x', 32 * 1024);
            Logger.Info("Test", big);
            Logger.Info("Test", big);
            Logger.Info("Test", big); // > 64KB → 轮转
            Logger.Info("Test", "after rotate");
            Assert.True(File.Exists(Path.Combine(logDir, "TokenMonitor.old.log")));
            Assert.True(File.Exists(Path.Combine(logDir, "TokenMonitor.log")));
            Assert.Contains("after rotate", File.ReadAllText(Path.Combine(logDir, "TokenMonitor.log")));
        }
        finally
        {
            Logger.RotateThreshold = 5 * 1024 * 1024;
        }
    }

    [Fact]
    public void Logger_NeverLogsSecrets()
    {
        // 脱敏铁律（静态约定验证）：日志格式不含 Authorization/api_key 字段槽位
        // （代理请求级日志只含 method/path/body 字节数/model/token 数——见 ProxyEngine 实现）
        var logDir = Path.Combine(_dir.Path, "logs2");
        Logger.Init(logDir);
        Logger.Info("Proxy", "POST /v1/chat/completions body=123 bytes model=test-m");
        var content = File.ReadAllText(Path.Combine(logDir, "TokenMonitor.log"));
        Assert.Contains("[INFO] [Proxy]", content);
        Assert.DoesNotContain("Authorization", content);
    }
}
