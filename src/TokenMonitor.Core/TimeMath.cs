namespace TokenMonitor.Core;

/// <summary>
/// 时间/取整的统一纯函数集（02-§4.4 一致性硬约束的落点）：
/// 实时路径（Accumulator.AddUsage）与重算路径（RecalcDerived/AggregateLocal）必须共用同一组函数，
/// 否则 RecalcDerived 幂等性会被破坏。ε=1e-9 仅用于时段平移/合并的浮点边界（02-§0.1）。
/// </summary>
public static class TimeMath
{
    /// <summary>浮点边界比较容差（沿用原 shiftPeriodsToLocal 判定精度）。</summary>
    public const double Epsilon = 1e-9;

    /// <summary>[C11] 统一倍率取整：四舍五入（AwayFromZero）转 long。</summary>
    public static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>UTC 口径分数小时 = H + M/60.0（[C4]；秒不参与——时段边界均为 0.5 步进，02-§0.1）。</summary>
    public static double UtcFractionalHour(long ts)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        return t.Hour + t.Minute / 60.0;
    }

    /// <summary>Local(UTC+offset) 口径分数小时（[C4]，支持 30/45 分钟时区）。</summary>
    public static double LocalFractionalHour(long ts, int offsetMin)
    {
        var t = ToOffset(ts, offsetMin);
        return t.Hour + t.Minute / 60.0;
    }

    /// <summary>UTC 日历日期字符串 yyyy-MM-dd。</summary>
    public static string UtcDate(long ts) => DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToString("yyyy-MM-dd");

    /// <summary>UTC+offset 日历日期字符串 yyyy-MM-dd（offset 可为负，支持半时区）。</summary>
    public static string LocalDate(long ts, int offsetMin) => ToOffset(ts, offsetMin).ToString("yyyy-MM-dd");

    /// <summary>ts 换算到 UTC+offset 的 DateTimeOffset。</summary>
    public static DateTimeOffset ToOffset(long ts, int offsetMin) =>
        DateTimeOffset.FromUnixTimeSeconds(ts).ToOffset(TimeSpan.FromMinutes(offsetMin));

    /// <summary>ISO 星期编号（1=周一 .. 7=周日；周日 0→7）。</summary>
    public static int IsoWeekday(DayOfWeek day) => day == DayOfWeek.Sunday ? 7 : (int)day;

    /// <summary>UTC 日历的 ISO 星期（计价星期匹配，UTC 口径）。</summary>
    public static int IsoWeekdayUtc(long ts) => IsoWeekday(DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.DayOfWeek);

    /// <summary>UTC+offset 日历的 ISO 星期（计价星期匹配，Local 口径）。</summary>
    public static int IsoWeekdayLocal(long ts, int offsetMin) => IsoWeekday(ToOffset(ts, offsetMin).DayOfWeek);

    /// <summary>本地(offset)日历 date（yyyy-MM-dd）00:00 的 Unix 秒——即"本地午夜"= UTC 00:00 平移 -offset（02-§5.6 LocalDayStart）。</summary>
    public static long LocalDayStart(string date, int offsetMin) =>
        new DateTimeOffset(DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.Zero)
            .AddMinutes(-offsetMin)
            .ToUnixTimeSeconds();

    /// <summary>UTC 日历 date（yyyy-MM-dd）00:00 的 Unix 秒。</summary>
    public static long UtcDayStart(string date) =>
        new DateTimeOffset(DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                TimeSpan.Zero)
            .ToUnixTimeSeconds();

    /// <summary>Unix 秒 → UTC+offset 的本地日期时间格式化（文件日志用：yyyy-MM-dd HH:mm:ss）。</summary>
    public static string FormatLocalDateTime(long ts, int offsetMin) =>
        ToOffset(ts, offsetMin).ToString("yyyy-MM-dd HH:mm:ss");
}

/// <summary>
/// 异步回包守卫（[C16] 的 Core 侧落点）：范围查询/导出等异步回包落地前校验目标卡片键，
/// 防止用户切卡后旧请求结果写入新卡片选中态。App.ViewModel 在 await 回包处调用。
/// </summary>
public static class AsyncResultGuard
{
    /// <summary>请求时记录的 modelKey 与回包落地时的当前 modelKey 一致才允许写入；两者均为 null 视为同卡（全局结果）。</summary>
    public static bool IsValid(string? requestedModelKey, string? currentModelKey) =>
        string.Equals(requestedModelKey, currentModelKey, StringComparison.Ordinal);
}
