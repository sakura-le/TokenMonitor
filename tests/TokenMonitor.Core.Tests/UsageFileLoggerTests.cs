using TokenMonitor.Core.Parser;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.Core.Tests;

/// <summary>每日文件日志回归（原 usagelog_test 移植 + C19-② 文件名净化）。</summary>
public class UsageFileLoggerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => Directory.Delete(_dir.Path, recursive: true);

    private static UnifiedUsage SampleUsage() => new(88660, 87936, 724, 576, 276, 89236);

    [Fact]
    public void UsageFileLogger_DualFormat()
    {
        // 原 usagelog_test：双格式同字段、BOM、表头唯一、字段序、本地日界
        var l = new UsageFileLogger(_dir.Path, 480); // UTC+8
        // 2026-08-19 23:51:17 UTC = 2026-08-20 07:51:17 (UTC+8)
        var ts = new DateTimeOffset(2026, 8, 19, 23, 51, 17, TimeSpan.Zero).ToUnixTimeSeconds();
        var evt = new UsageEvent("DeepSeek", "deepseek-v4-flash", SampleUsage(), ts, CaptureSource.Json);
        l.Log(evt, 0.0080748, 0.0);
        l.Log(new UsageEvent("DeepSeek", "deepseek-v4-flash", SampleUsage(), ts + 10, CaptureSource.Json), 0.0080748, 0.0);

        var logPath = Path.Combine(_dir.Path, "usage_logs", "2026-08-20_DeepSeek_deepseek-v4-flash.log");
        var csvPath = Path.Combine(_dir.Path, "usage_logs", "2026-08-20_DeepSeek_deepseek-v4-flash.csv");

        var logLines = File.ReadAllLines(logPath);
        Assert.Equal(2, logLines.Length);
        Assert.Equal(
            "[2026-08-20 07:51:17] provider=DeepSeek model=deepseek-v4-flash prompt=88660 cache_hit=87936 cache_miss=724 completion=576 reasoning=276 total=89236 cost_cny=0.0080748 cost_usd=0.0000000",
            logLines[0]);

        var csvBytes = File.ReadAllBytes(csvPath);
        Assert.Equal(0xEF, csvBytes[0]); // UTF-8 BOM
        Assert.Equal(0xBB, csvBytes[1]);
        Assert.Equal(0xBF, csvBytes[2]);
        var csvText = File.ReadAllText(csvPath);
        var csvLines = csvText.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, csvLines.Length); // 表头 + 2 行
        Assert.Equal("local_time,provider,model,prompt_tokens,cache_hit,cache_miss,completion_tokens,reasoning_tokens,total_tokens,cost_cny,cost_usd",
            csvLines[0].TrimStart('\uFEFF'));
        Assert.Equal("2026-08-20 07:51:17,DeepSeek,deepseek-v4-flash,88660,87936,724,576,276,89236,0.0080748,0.0000000",
            csvLines[1]);
        Assert.Equal(1, csvBytes.Select((b, i) => i <= csvBytes.Length - 3 && b == 0xEF && csvBytes[i + 1] == 0xBB && csvBytes[i + 2] == 0xBF ? 1 : 0).Sum()); // BOM 恰一次（追加不重复写）
    }

    [Fact]
    public void UsageFileLogger_SanitizesModelName()
    {
        // [C19-②] 文件名净化：模型名含 '/' 等非法字符 → _
        var l = new UsageFileLogger(_dir.Path, 0);
        var ts = new DateTimeOffset(2026, 8, 19, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        l.Log(new UsageEvent("P", "openai/gpt 4:model", SampleUsage(), ts, CaptureSource.Json), 0, 0);
        var files = Directory.GetFiles(Path.Combine(_dir.Path, "usage_logs"));
        Assert.All(files, f => Assert.DoesNotContain(Path.GetFileName(f), c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ' '));
        Assert.Contains(files, f => Path.GetFileName(f).StartsWith("2026-08-19_P_openai_gpt_4_model.log"));
    }

    [Fact]
    public void UsageFileLogger_Configure_SwitchesLocalCalendar()
    {
        // Configure(offsetMin) 后按新本地日历切文件
        var l = new UsageFileLogger(_dir.Path, 0);
        var ts = new DateTimeOffset(2026, 8, 19, 20, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        l.Log(new UsageEvent("P", "m", SampleUsage(), ts, CaptureSource.Json), 0, 0); // UTC 08-19
        l.Configure(480);
        l.Log(new UsageEvent("P", "m", SampleUsage(), ts, CaptureSource.Json), 0, 0); // 本地 08-20 04:00
        var files = Directory.GetFiles(Path.Combine(_dir.Path, "usage_logs")).Select(Path.GetFileName).ToList();
        Assert.Contains(files, f => f!.StartsWith("2026-08-19_"));
        Assert.Contains(files, f => f!.StartsWith("2026-08-20_"));
    }
}
