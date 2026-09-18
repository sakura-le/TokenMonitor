using MiniExcelLibs;
using MiniExcelLibs.Attributes;
using MiniExcelLibs.OpenXml;

using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Storage;

/// <summary>XLSX 导出契约（01-§2.3.8）。同步 IO，调用方必须放后台线程。</summary>
public interface IExportService
{
    /// <summary>RangeScope 决定 StartDate/EndDate 的日历解释（[C9] 规则同 IStatsQueryService.GetRange）。
    /// Sheet 固定顺序：UTC总表 / UTC明细表 / LOCAL总表 / LOCAL明细表 [+ HOUR用量 / HOUR消费]。</summary>
    ExportResult ExportXlsx(ExportRequest request);
}

/// <summary>
/// IExportService 默认实现（MiniExcel，02-§7 照原 ExportToXLSX/write*Sheet 逐列对齐）：
/// UTC 侧取 usage_daily；LOCAL 侧从 usage_log 现算；HOUR 表恒为本地"今日"（仅 IncludeHourly）。
/// 文件：data/export/token_usage_{modelTag|all}_{start}_{end}_{yyyyMMdd_HHmmss}.xlsx。
/// 列宽经 MiniExcel DynamicColumns（A-B=12、C-I=14、J-K=12，原 SetColWidth 保持）。
/// </summary>
public sealed class ExportService : IExportService
{
    private const string FullRangeStart = "2000-01-01";
    private const string FullRangeEnd = "2099-12-31";

    private readonly IStore _store;
    private readonly RateFunc? _rateFn;
    private readonly CostFunc? _costFn;
    private readonly Func<int> _offsetProvider;
    private readonly string _exportDir;
    private readonly TimeProvider _clock;

    public ExportService(IStore store, RateFunc? rateFn, CostFunc? costFn,
                         Func<int> offsetProvider, string exportDir, TimeProvider? clock = null)
    {
        _store = store;
        _rateFn = rateFn;
        _costFn = costFn;
        _offsetProvider = offsetProvider;
        _exportDir = exportDir;
        _clock = clock ?? TimeProvider.System;
    }

    public ExportResult ExportXlsx(ExportRequest request)
    {
        try
        {
            Directory.CreateDirectory(_exportDir);
            var offset = _offsetProvider();
            var keyFilter = request.ModelKey;

            // —— 数据准备（D7-2：ModelDailyAggregate 行已含成本，直接写行值，去中间映射）——
            var utcAll = Filter(_store.QueryDailyRange(FullRangeStart, FullRangeEnd), keyFilter);
            var utcDaily = Filter(_store.QueryDailyRange(request.StartDate, request.EndDate), keyFilter);
            var localAll = Filter(_store.AggregateLocal(0, long.MaxValue, offset, _rateFn, _costFn), keyFilter);

            // [C9]/[C19-①] 日期窗口的日历解释由 RangeScope 显式决定
            var (startTs, endEx) = request.RangeScope == BucketScope.Local
                ? (TimeMath.LocalDayStart(request.StartDate, offset), TimeMath.LocalDayStart(request.EndDate, offset) + 86400)
                : (TimeMath.UtcDayStart(request.StartDate), TimeMath.UtcDayStart(request.EndDate) + 86400);
            var localDaily = Filter(_store.AggregateLocal(startTs, endEx, offset, _rateFn, _costFn), keyFilter);

            // HOUR 表恒为本地今日（其维度即本地小时，无 UTC 语义）
            var now = _clock.GetUtcNow().ToUnixTimeSeconds();
            var localToday = TimeMath.LocalDate(now, offset);
            var hourly = request.IncludeHourly
                ? _store.AggregateHourly(TimeMath.LocalDayStart(localToday, offset),
                                         TimeMath.LocalDayStart(localToday, offset) + 86400, offset, _rateFn, _costFn)
                : [];

            // —— 文件名（modelTag=模型键中 '/'→'-'，原语义）——
            var modelTag = string.IsNullOrEmpty(keyFilter) ? "all" : keyFilter.Replace('/', '-').Replace('\\', '-');
            var stamp = _clock.GetLocalNow().ToString("yyyyMMdd_HHmmss");
            var path = Path.Combine(_exportDir, $"token_usage_{modelTag}_{request.StartDate}_{request.EndDate}_{stamp}.xlsx");

            // —— Sheet 组装（固定顺序；总表行序按 (provider, model) 升序——D7-1 确定化）——
            var sheets = new Dictionary<string, object>
            {
                ["UTC总表"] = TotalRows(utcAll),
                ["UTC明细表"] = DailyRows(utcDaily),
                ["LOCAL总表"] = TotalRows(localAll),
                ["LOCAL明细表"] = DailyRows(localDaily),
            };
            if (request.IncludeHourly)
            {
                sheets["HOUR用量"] = HourlyUsageRows(hourly);
                sheets["HOUR消费"] = HourlyCostRows(hourly);
            }
            var config = new OpenXmlConfiguration { DynamicColumns = BuildColumnWidths() };
            MiniExcel.SaveAs(path, sheets, excelType: ExcelType.XLSX, configuration: config, overwriteFile: true);

            return new ExportResult(true, null, path, utcAll.Count + utcDaily.Count,
                localAll.Count + localDaily.Count, hourly.Count);
        }
        catch (Exception ex)
        {
            Logger.Error("Storage", $"XLSX 导出失败: {ex.Message}", ex);
            return new ExportResult(false, ex.Message, string.Empty, 0, 0, 0);
        }
    }

    private static IReadOnlyList<ModelDailyAggregate> Filter(IReadOnlyList<ModelDailyAggregate> rows, string? keyFilter)
    {
        if (string.IsNullOrEmpty(keyFilter)) return rows;
        return rows.Where(r => r.Provider + "/" + r.Model == keyFilter).ToList();
    }

    /// <summary>列宽表（原 SetColWidth：A-B=12、C-I=14、J-K=12；按表头名匹配全 Sheet）。</summary>
    private static DynamicExcelColumn[] BuildColumnWidths()
    {
        var widths = new (string Header, double Width)[]
        {
            ("厂商", 12), ("模型", 12), ("日期", 12),
            ("输入Token", 14), ("缓存命中", 14), ("缓存未命中", 14), ("输出Token", 14),
            ("推理Token", 14), ("总Token", 14), ("请求次数", 14),
            ("输入Token(缓存命中)", 14), ("输入Token(缓存未命中)", 14),
            ("消费(元)", 12), ("消费(美元)", 12),
            ("开始时间", 12), ("结束时间", 12),
        };
        return widths.Select(w => new DynamicExcelColumn(w.Header) { Width = w.Width }).ToArray();
    }

    private static List<Dictionary<string, object>> TotalRows(IReadOnlyList<ModelDailyAggregate> rows) =>
        rows.GroupBy(r => (r.Provider, r.Model))
            .OrderBy(g => g.Key.Provider, StringComparer.Ordinal).ThenBy(g => g.Key.Model, StringComparer.Ordinal)
            .Select(g => new Dictionary<string, object>
            {
                ["厂商"] = g.Key.Provider,
                ["模型"] = g.Key.Model,
                ["输入Token"] = g.Sum(x => x.PromptTokens),
                ["缓存命中"] = g.Sum(x => x.CacheHitTokens),
                ["缓存未命中"] = g.Sum(x => x.CacheMissTokens),
                ["输出Token"] = g.Sum(x => x.CompletionTokens),
                ["推理Token"] = g.Sum(x => x.ReasoningTokens),
                ["总Token"] = g.Sum(x => x.TotalTokens),
                ["请求次数"] = g.Sum(x => x.RequestCount),
                ["消费(元)"] = g.Sum(x => x.CostCNY),
                ["消费(美元)"] = g.Sum(x => x.CostUSD),
            })
            .ToList();

    private static List<Dictionary<string, object>> DailyRows(IReadOnlyList<ModelDailyAggregate> rows) =>
        rows.OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Provider, StringComparer.Ordinal)
            .ThenBy(x => x.Model, StringComparer.Ordinal)
            .Select(r => new Dictionary<string, object>
            {
                ["日期"] = r.Date,
                ["厂商"] = r.Provider,
                ["模型"] = r.Model,
                ["输入Token"] = r.PromptTokens,
                ["缓存命中"] = r.CacheHitTokens,
                ["缓存未命中"] = r.CacheMissTokens,
                ["输出Token"] = r.CompletionTokens,
                ["推理Token"] = r.ReasoningTokens,
                ["总Token"] = r.TotalTokens,
                ["请求次数"] = r.RequestCount,
                ["消费(元)"] = r.CostCNY,
                ["消费(美元)"] = r.CostUSD,
            })
            .ToList();

    /// <summary>HOUR 用量（原 writeHourlyUsageSheet；开始/结束 = "&lt;date&gt; HH:00" 与 +1h，格式 yyyy-MM-dd HH:mm）。</summary>
    private static List<Dictionary<string, object>> HourlyUsageRows(IReadOnlyList<HourlyAggregate> rows) =>
        rows.OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Hour)
            .ThenBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.Model, StringComparer.Ordinal)
            .Select(r => new Dictionary<string, object>
            {
                ["开始时间"] = HourRange(r.Date, r.Hour).Start,
                ["结束时间"] = HourRange(r.Date, r.Hour).End,
                ["厂商"] = r.Provider,
                ["模型"] = r.Model,
                ["请求次数"] = r.RequestCount,
                ["输入Token(缓存命中)"] = r.CacheHitTokens,
                ["输入Token(缓存未命中)"] = r.CacheMissTokens,
                ["输出Token"] = r.CompletionTokens,
                ["推理Token"] = r.ReasoningTokens,
                ["总Token"] = r.TotalTokens,
            })
            .ToList();

    /// <summary>HOUR 消费（原 writeHourlyCostSheet）。</summary>
    private static List<Dictionary<string, object>> HourlyCostRows(IReadOnlyList<HourlyAggregate> rows) =>
        rows.OrderBy(x => x.Date, StringComparer.Ordinal).ThenBy(x => x.Hour)
            .ThenBy(x => x.Provider, StringComparer.Ordinal).ThenBy(x => x.Model, StringComparer.Ordinal)
            .Select(r => new Dictionary<string, object>
            {
                ["开始时间"] = HourRange(r.Date, r.Hour).Start,
                ["结束时间"] = HourRange(r.Date, r.Hour).End,
                ["厂商"] = r.Provider,
                ["模型"] = r.Model,
                ["消费(元)"] = r.CostCNY,
                ["消费(美元)"] = r.CostUSD,
            })
            .ToList();

    /// <summary>本地小时段起止字符串（原 hourRange 语义）。</summary>
    internal (string Start, string End) TestHourRange(string date, int hour) => HourRange(date, hour);

    private static (string Start, string End) HourRange(string date, int hour)
    {
        var day = System.DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var start = new System.DateTime(day.Year, day.Month, day.Day, hour, 0, 0);
        return (start.ToString("yyyy-MM-dd HH:mm"), start.AddHours(1).ToString("yyyy-MM-dd HH:mm"));
    }
}
