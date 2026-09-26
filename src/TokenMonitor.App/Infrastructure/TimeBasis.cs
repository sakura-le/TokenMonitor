using TokenMonitor.Core.Pricing;

namespace TokenMonitor.App.Infrastructure;

/// <summary>
/// 计价/倍率配置对话框的"时段编辑基准"换算（02-§4.5：时段/规则恒按 UTC 存储，Local 口径下由
/// 对话框负责双向换算——原前端 <c>tzShift.js</c> 的职责）。
/// <para>
/// 基准取 <c>settings.effective_date_mode</c>：</para>
/// <list type="bullet">
/// <item><b>local</b>：打开即把存量 UTC 时段平移到本地时钟显示（+offset），保存时把用户填的本地时段
/// 平移回 UTC（−offset）落库。缺这一步时，用户按本地时间填的"00:00–09:00"会被引擎当成 UTC 时段匹配，
/// 于是本地晚间（厂商的优惠时段）被算成峰时高价——即"显示金额比厂商后台高一截"的来源。</item>
/// <item><b>utc</b>：原样（用户直接按 UTC 填写）。</item>
/// </list>
/// </summary>
internal static class TimeBasis
{
    public static bool IsLocal(string? mode) => string.Equals(mode, "local", StringComparison.Ordinal);

    /// <summary>存量（UTC 存储）→ 编辑区显示。</summary>
    public static IReadOnlyList<PriceRule> ToDisplay(IReadOnlyList<PriceRule> stored, string? mode, int offsetMin)
        => IsLocal(mode) ? PricingShift.ShiftPriceRulesToLocal(stored, offsetMin) : stored;

    /// <summary>编辑区输入 → 落库（UTC 存储）。</summary>
    public static IReadOnlyList<PriceRule> ToStorage(IReadOnlyList<PriceRule> display, string? mode, int offsetMin)
        => IsLocal(mode) ? PricingShift.ShiftPriceRulesToLocal(display, -offsetMin) : display;

    /// <summary>同上（倍率时段，无星期维度）。</summary>
    public static IReadOnlyList<MultiplierPeriod> ToDisplay(IReadOnlyList<MultiplierPeriod> stored, string? mode, int offsetMin)
        => IsLocal(mode) ? PricingShift.ShiftPeriodsToLocal(stored, offsetMin) : stored;

    /// <summary>同上（倍率时段，无星期维度）。</summary>
    public static IReadOnlyList<MultiplierPeriod> ToStorage(IReadOnlyList<MultiplierPeriod> display, string? mode, int offsetMin)
        => IsLocal(mode) ? PricingShift.ShiftPeriodsToLocal(display, -offsetMin) : display;

    /// <summary>页脚提示：明确告知当前时段按哪个日历填写、保存后如何存储。</summary>
    public static string Hint(string? mode, int offsetMin) => IsLocal(mode)
        ? $"时段按 LOCAL（{Fmt.OffsetLabel(offsetMin)}）填写，保存自动换算为 UTC 存储；跨午夜时段自动平移（含星期）· 保存前自动备份 pricing.json"
        : "时段按 UTC 填写（生效日期基准 = UTC）· 跨午夜时段自动平移（含星期）· 保存前自动备份 pricing.json";
}
