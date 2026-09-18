using System.Text.Json.Nodes;

namespace TokenMonitor.Core.Pricing;

/// <summary>倍率时段（小时，0.5 步进；匹配区间 [Start, End)，02-§4.4）。</summary>
public sealed record MultiplierPeriod(double Start, double End, double Rate);

/// <summary>计价规则（单价 per 1M token；Days=1..7 周一..周日，空/缺省=每天）。</summary>
public sealed record PriceRule(double Start, double End, IReadOnlyList<int>? Days,
                               double InputPer1M, double CachePer1M, double OutputPer1M);

/// <summary>倍率版本（EffectiveFrom=null/"" = 一直生效，排序为最早）。</summary>
public sealed record MultiplierVersion(string? EffectiveFrom, IReadOnlyList<MultiplierPeriod> Periods);

/// <summary>计价版本（Currency 仅接受 CNY/USD）。</summary>
public sealed record PriceVersion(string? EffectiveFrom, string Currency, IReadOnlyList<PriceRule> Rules);

/// <summary>单模型倍率版本链（history 末尾=最近保存）。</summary>
public sealed record MultiplierConfig(IReadOnlyList<MultiplierVersion> History);

/// <summary>单模型计价版本链。</summary>
public sealed record PriceConfig(IReadOnlyList<PriceVersion> History);

/// <summary>
/// pricing.json 文档。键=完整模型键 provider/model（原版 App.SaveMultiplierVersion 传 selectedKey=
/// "provider/model"，01-§3.2"不含 provider 前缀"与原码不符，以原码与接口契约为准——交付报告登记）。
/// </summary>
public sealed record PricingDocument(
    IReadOnlyDictionary<string, MultiplierConfig> Multipliers,
    IReadOnlyDictionary<string, PriceConfig> Pricing)
{
    public static PricingDocument Empty { get; } =
        new(new Dictionary<string, MultiplierConfig>(), new Dictionary<string, PriceConfig>());

    /// <summary>
    /// 加载归一化（[C13]，原 Load 迁移逻辑）：
    /// ① 旧平铺 {periods}/{currency,rules} → history 化；② per-1K(≠0 且 per-1M 缺失) ×1000 迁入并清零旧字段；
    /// ③ 同一 effective_from 重复版本 → 保留链尾值、占据首次出现位置（原 dedupe 语义，非"留最后一条"）；
    /// ④ currency 非 CNY → 归 USD。
    /// </summary>
    public PricingDocument Normalize() => PricingDocumentIO.Normalize(this);
}

/// <summary>倍率报价（Matched=false → Rate=1.0）。</summary>
public readonly record struct MultiplierQuote(double Rate, bool Matched);

/// <summary>成本报价（Priced=false → "不计价/Coding Plan 套餐"）。</summary>
public readonly record struct CostQuote(double CostCNY, double CostUSD, bool Priced);

/// <summary>查询口径枚举（供 Stats/Storage/Export 共用）。</summary>
public enum BucketScope { Utc, Local }
