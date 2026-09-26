using System.Globalization;

namespace TokenMonitor.App.Infrastructure;

/// <summary>
/// 数字/文案格式化（等宽表格数字口径；与原版 UI 文案一致）。
/// 仅 App 层展示用途，不参与任何统计推导。
/// </summary>
public static class Fmt
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>千分位整数（等宽数字）。</summary>
    public static string N0(long v) => v.ToString("N0", Inv);

    /// <summary>大数字缩写：8,641,233 → 8.6M；736,209 → 736K；999 → 999。</summary>
    public static string Compact(long v)
    {
        var a = Math.Abs(v);
        if (a >= 1_000_000_000) return (v / 1_000_000_000.0).ToString("0.##B", Inv);
        if (a >= 1_000_000) return (v / 1_000_000.0).ToString("0.#M", Inv);
        if (a >= 10_000) return (v / 1_000.0).ToString("0.#K", Inv);
        return v.ToString("N0", Inv);
    }

    /// <summary>带符号增量：+1,234 / +1.2K。</summary>
    public static string Delta(long v) => v > 0 ? "+" + Compact(v) : "0";

    /// <summary>带符号千分位增量（卡片实时增量用完整数字）。</summary>
    public static string DeltaFull(long v) => v > 0 ? "+" + N0(v) : "0";

    /// <summary>百分比 1 位小数：67.3。</summary>
    public static string Pct(double v) => (v * 100).ToString("0.#", Inv);

    /// <summary>金额 4 位小数：42.1371。</summary>
    public static string Money(double v) => v.ToString("0.0000", Inv);

    /// <summary>时间 HH:mm:ss。</summary>
    public static string Clock(DateTime t) => t.ToString("HH:mm:ss", Inv);

    /// <summary>日期 MM-dd。</summary>
    public static string Day(DateTime t) => t.ToString("MM-dd", Inv);

    /// <summary>倍率徽章：×1.5。</summary>
    public static string Rate(double r) => "×" + r.ToString("0.#", Inv);

    /// <summary>UTC±HH:mm 标签（托盘/徽章）。</summary>
    public static string OffsetLabel(int offsetMin)
    {
        var sign = offsetMin < 0 ? "−" : "+";
        var a = Math.Abs(offsetMin);
        return $"UTC{sign}{a / 60}:{a % 60:00}";
    }

    /// <summary>将 0.5h 粒度小时数显示为 HH:mm（9.0 → 09:00，9.5 → 09:30，24 → 24:00）。</summary>
    public static string HourLabel(double h)
    {
        var hh = (int)h;
        var mm = (int)Math.Round((h - hh) * 60);
        return $"{hh:00}:{mm:00}";
    }

    /// <summary>时段起点候选项：00:00…23:30，每半小时一档（配置对话框下拉）。</summary>
    public static IReadOnlyList<string> HourStarts { get; } = BuildHourOptions(0, 47);

    /// <summary>时段终点候选项：00:30…24:00，每半小时一档（终点点允许 24:00）。</summary>
    public static IReadOnlyList<string> HourEnds { get; } = BuildHourOptions(1, 48);

    private static List<string> BuildHourOptions(int fromStep, int toStep)
    {
        var list = new List<string>(toStep - fromStep + 1);
        for (var step = fromStep; step <= toStep; step++) list.Add(HourLabel(step / 2.0));
        return list;
    }

    /// <summary>把任意 HH:mm 文本归一到 0.5h 档位标签（"9:00" → "09:00"、"09:15" → "09:30"）。</summary>
    public static string NormalizeHour(string? text, double fallback = 0)
        => HourLabel(ParseHour(text) ?? fallback);

    /// <summary>解析 HH:mm 为 0.5h 粒度小时数；非法返回 null。</summary>
    public static double? ParseHour(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 1 or > 2) return null;
        if (!int.TryParse(parts[0], NumberStyles.Integer, Inv, out var hh)) return null;
        var mm = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.Integer, Inv, out mm)) return null;
        if (hh < 0 || hh > 24 || mm < 0 || mm > 59) return null;
        var v = hh + mm / 60.0;
        if (v > 24.0) return null;
        return Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2.0;
    }
}
