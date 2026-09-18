namespace TokenMonitor.Core.SysUtil;

/// <summary>日志级别（Debug &lt; Info &lt; Warn &lt; Error；默认门面 Info）。</summary>
public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// 静态日志门面（01-§8.2）：data/logs/TokenMonitor.log，UTF-8 无 BOM 追加写，
/// 启动时与每次写入前检查 &gt;5MB → 轮转为 TokenMonitor.old.log（覆盖旧轮转，磁盘上界约 10MB）。
/// 脱敏铁律：永不记录 Authorization 头、api_key（明文或密文）、请求体内容。
/// 未 Init（如纯单元测试环境）时静默丢弃，不抛异常。
/// </summary>
public static class Logger
{
    private const long RotateThresholdBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string _logDir = string.Empty;
    private static string _logPath = string.Empty;
    private static LogLevel _minLevel = LogLevel.Info;
    private static bool _initialized;

    /// <summary>轮转阈值（测试注入用；生产恒 5MB）。</summary>
    internal static long RotateThreshold { get; set; } = RotateThresholdBytes;

    /// <summary>初始化：日志目录 = data/logs；存在 data/logs/.debug 文件时启用 Debug 级别。</summary>
    public static void Init(string logDir)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                _logDir = logDir;
                _logPath = Path.Combine(logDir, "TokenMonitor.log");
                _minLevel = File.Exists(Path.Combine(logDir, ".debug")) ? LogLevel.Debug : LogLevel.Info;
                _initialized = true;
                RotateIfNeededNoLock();
            }
            catch
            {
                // 日志初始化失败不能影响主流程（降级为不落盘）
                _initialized = false;
            }
        }
    }

    public static void Debug(string tag, string message) => Write(LogLevel.Debug, tag, message, null);
    public static void Info(string tag, string message) => Write(LogLevel.Info, tag, message, null);
    public static void Warn(string tag, string message) => Write(LogLevel.Warn, tag, message, null);
    public static void Error(string tag, string message, Exception? ex = null) => Write(LogLevel.Error, tag, message, ex);

    public static void Write(LogLevel level, string tag, string message, Exception? ex)
    {
        if (level < _minLevel) return;
        lock (Gate)
        {
            if (!_initialized) return;
            try
            {
                RotateIfNeededNoLock();
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] [{tag}] {message}\n";
                if (ex is not null) line += "    " + ex.ToString().Replace("\r\n", "\n").Replace("\n", "\n    ") + "\n";
                File.AppendAllText(_logPath, line, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // 写日志失败静默（例如磁盘满），绝不向上传染
            }
        }
    }

    private static void RotateIfNeededNoLock()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var fi = new FileInfo(_logPath);
            if (fi.Length <= RotateThreshold) return;
            var old = Path.Combine(_logDir, "TokenMonitor.old.log");
            File.Delete(old);
            File.Move(_logPath, old);
        }
        catch
        {
            // 轮转失败下次写入再试
        }
    }
}
