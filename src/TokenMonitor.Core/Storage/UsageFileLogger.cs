using System.Globalization;
using System.Text;
using TokenMonitor.Core.Parser;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Storage;

/// <summary>每日文件日志契约（01-§2.3.9）：data/usage_logs/YYYY-MM-DD_Provider_Model.log + .csv（本地时区）。</summary>
public interface IUsageFileLogger
{
    /// <summary>线程安全；内部锁串行追加。</summary>
    void Log(UsageEvent evt, double costCny, double costUsd);

    /// <summary>offsetMin 变更时调用（后续记录按新本地日历切文件）。</summary>
    void Configure(int offsetMin);
}

/// <summary>
/// IUsageFileLogger 默认实现（格式照原 usagelog.go，原 usagelog_test 移植锁定）：
///  - .log 行：[本地 yyyy-MM-dd HH:mm:ss] provider=… model=… prompt=… cache_hit=… cache_miss=… completion=… reasoning=… total=… cost_cny=%.7f cost_usd=%.7f
///  - .csv：首行 UTF-8 BOM + 表头（空文件才写表头，追加不重复）。
/// 文件名非法字符（/ \ : * ? " &lt; &gt; | 空格）→ _（C19-② sanitizeModel 保持）。
/// </summary>
public sealed class UsageFileLogger : IUsageFileLogger
{
    private const string CsvHeader =
        "local_time,provider,model,prompt_tokens,cache_hit,cache_miss,completion_tokens,reasoning_tokens,total_tokens,cost_cny,cost_usd";

    private readonly object _gate = new();
    private readonly string _dir;
    private int _offsetMin;

    public UsageFileLogger(string dataDir, int offsetMin)
    {
        _dir = Path.Combine(dataDir, "usage_logs");
        Directory.CreateDirectory(_dir);
        _offsetMin = offsetMin;
    }

    public void Configure(int offsetMin)
    {
        lock (_gate)
        {
            _offsetMin = offsetMin;
        }
    }

    public void Log(UsageEvent evt, double costCny, double costUsd)
    {
        lock (_gate)
        {
            try
            {
                var local = TimeMath.FormatLocalDateTime(evt.CompletedAtUnix, _offsetMin);
                var localDate = local[..10]; // yyyy-MM-dd
                var baseName = Path.Combine(_dir,
                    $"{localDate}_{Sanitize(evt.Provider)}_{Sanitize(evt.Model)}");
                var u = evt.Usage;
                var logLine =
                    $"[{local}] provider={evt.Provider} model={evt.Model} prompt={u.PromptTokens} cache_hit={u.CacheHitTokens} cache_miss={u.CacheMissTokens} completion={u.CompletionTokens} reasoning={u.ReasoningTokens} total={u.TotalTokens} cost_cny={costCny.ToString("F7", CultureInfo.InvariantCulture)} cost_usd={costUsd.ToString("F7", CultureInfo.InvariantCulture)}\n";
                var csvLine =
                    $"{local},{evt.Provider},{evt.Model},{u.PromptTokens},{u.CacheHitTokens},{u.CacheMissTokens},{u.CompletionTokens},{u.ReasoningTokens},{u.TotalTokens},{costCny.ToString("F7", CultureInfo.InvariantCulture)},{costUsd.ToString("F7", CultureInfo.InvariantCulture)}\n";
                Append(baseName + ".log", logLine, isCsv: false);
                Append(baseName + ".csv", csvLine, isCsv: true);
            }
            catch (Exception ex)
            {
                Logger.Warn("UsageLog", $"写入使用痕迹日志失败: {ex.Message}");
            }
        }
    }

    private void Append(string path, string content, bool isCsv)
    {
        // csv 文件首次创建（空文件）时写 BOM+表头（原 append 语义）
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (isCsv && stream.Length == 0)
        {
            stream.Write(Encoding.UTF8.GetBytes("\uFEFF" + CsvHeader + "\n"));
        }
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>文件名非法字符净化（原 sanitizeModel 语义；C19-②：模型名含 '/' 落盘安全）。</summary>
    internal static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
        {
            sb.Append(ch is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ' ' ? '_' : ch);
        }
        return sb.ToString();
    }
}
