using System.Text.Json;
using System.Text.Json.Nodes;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.Pricing;

/// <summary>
/// pricing.json 读写与迁移（01-§3.2）。读取兼容旧平铺格式；写回为规范 history 格式
/// （UTF-8 无 BOM、snake_case、2 空格缩进、days 仅在非空时写出、effective_from 可为 null）。
/// </summary>
public static class PricingDocumentIO
{
    /// <summary>从文件加载；文件不存在/损坏 → 空文档（损坏时保留现场并告警，与 §3 通用规则一致）。
    /// 迁移判定 = 规范序列化结果与原文件内容不同（平铺→history / per-1K 迁移 / 去重 / 格式规范化均触发回写）。</summary>
    public static (PricingDocument Doc, bool Migrated) Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return (PricingDocument.Empty, false);
            var raw = File.ReadAllText(path);
            var node = JsonNode.Parse(raw);
            var doc = FromNode(node).Normalize();
            var canonical = Serialize(doc);
            return (doc, !canonical.Equals(raw.Trim(), StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            Logger.Warn("Pricing", $"加载 pricing.json 失败，按空配置继续: {ex.Message}");
            return (PricingDocument.Empty, false);
        }
    }

    /// <summary>原子写回（临时文件+替换）。</summary>
    public static void Save(string path, PricingDocument doc)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, Serialize(doc), new System.Text.UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }

    public static string Serialize(PricingDocument doc)
    {
        var root = new JsonObject();
        var mult = new JsonObject();
        foreach (var (key, mc) in doc.Multipliers.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var history = new JsonArray();
            foreach (var v in mc.History)
            {
                var vo = new JsonObject { ["effective_from"] = string.IsNullOrEmpty(v.EffectiveFrom) ? null : v.EffectiveFrom };
                var periods = new JsonArray();
                foreach (var p in v.Periods)
                    periods.Add(new JsonObject
                    {
                        ["start"] = p.Start,
                        ["end"] = p.End,
                        ["rate"] = p.Rate,
                    });
                vo["periods"] = periods;
                history.Add(vo);
            }
            mult[key] = new JsonObject { ["history"] = history };
        }
        root["multipliers"] = mult;

        var pric = new JsonObject();
        foreach (var (key, pc) in doc.Pricing.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var history = new JsonArray();
            foreach (var v in pc.History)
            {
                var vo = new JsonObject
                {
                    ["effective_from"] = string.IsNullOrEmpty(v.EffectiveFrom) ? null : v.EffectiveFrom,
                    ["currency"] = v.Currency,
                };
                var rules = new JsonArray();
                foreach (var r in v.Rules)
                {
                    var ro = new JsonObject
                    {
                        ["start"] = r.Start,
                        ["end"] = r.End,
                    };
                    if (r.Days is { Count: > 0 })
                    {
                        var days = new JsonArray();
                        foreach (var d in r.Days) days.Add(d);
                        ro["days"] = days;
                    }
                    ro["input_per_1m"] = r.InputPer1M;
                    ro["cache_per_1m"] = r.CachePer1M;
                    ro["output_per_1m"] = r.OutputPer1M;
                    rules.Add(ro);
                }
                vo["rules"] = rules;
                history.Add(vo);
            }
            pric[key] = new JsonObject { ["history"] = history };
        }
        root["pricing"] = pric;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>解析（含旧格式迁移）→ Normalize。文件级损坏由调用方捕获。</summary>
    public static PricingDocument FromNode(JsonNode? root)
    {
        var mult = new Dictionary<string, MultiplierConfig>(StringComparer.Ordinal);
        var pric = new Dictionary<string, PriceConfig>(StringComparer.Ordinal);
        if (root is not JsonObject obj)
            return new PricingDocument(mult, pric);

        if (obj["multipliers"] is JsonObject mo)
        {
            foreach (var (key, node) in mo)
            {
                if (node is not JsonObject cfg) continue;
                List<MultiplierVersion> history;
                if (cfg["history"] is JsonArray ha)
                    history = ha.OfType<JsonObject>().Select(ParseMultiplierVersion).Where(v => v is not null).Select(v => v!).ToList();
                else if (cfg["periods"] is JsonArray pa)
                    history = [new MultiplierVersion(null, ParsePeriods(pa))]; // 旧平铺 {periods} → history 化
                else
                    continue;
                mult[key] = new MultiplierConfig(history);
            }
        }

        if (obj["pricing"] is JsonObject po)
        {
            foreach (var (key, node) in po)
            {
                if (node is not JsonObject cfg) continue;
                List<PriceVersion> history;
                if (cfg["history"] is JsonArray ha)
                    history = ha.OfType<JsonObject>().Select(ParsePriceVersion).Where(v => v is not null).Select(v => v!).ToList();
                else if (cfg["currency"] is not null || cfg["rules"] is JsonArray)
                    history = [ParsePriceVersion(cfg)!]; // 旧平铺 {currency,rules} → history 化
                else
                    continue;
                pric[key] = new PriceConfig(history);
            }
        }
        return new PricingDocument(mult, pric);
    }

    /// <summary>Normalize（01-§3.2）：同 effective_from 去重（首位置、后值）。平铺→history 与 per-1K 迁移在 FromNode 解析时完成。</summary>
    public static PricingDocument Normalize(PricingDocument doc)
    {
        var mult = new Dictionary<string, MultiplierConfig>(StringComparer.Ordinal);
        foreach (var (key, mc) in doc.Multipliers)
            mult[key] = new MultiplierConfig(DedupeMultiplier(mc.History));

        var pric = new Dictionary<string, PriceConfig>(StringComparer.Ordinal);
        foreach (var (key, pc) in doc.Pricing)
            pric[key] = new PriceConfig(DedupePricing(pc.History));
        return new PricingDocument(mult, pric);
    }

    /// <summary>按 EffectiveFrom 去重：同一生效日期保留最后一次保存的值、占据首次出现的位置（原 dedupeMultiplierVersions 语义）。</summary>
    internal static List<MultiplierVersion> DedupeMultiplier(IReadOnlyList<MultiplierVersion> history)
    {
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        var outList = new List<MultiplierVersion>(history.Count);
        foreach (var v in history)
        {
            var eff = EffKey(v.EffectiveFrom);
            if (idx.TryGetValue(eff, out var i)) outList[i] = v;
            else
            {
                idx[eff] = outList.Count;
                outList.Add(v);
            }
        }
        return outList;
    }

    /// <summary>同上（计价版本，原 dedupePricingVersions 语义）。</summary>
    internal static List<PriceVersion> DedupePricing(IReadOnlyList<PriceVersion> history)
    {
        var idx = new Dictionary<string, int>(StringComparer.Ordinal);
        var outList = new List<PriceVersion>(history.Count);
        foreach (var v in history)
        {
            var eff = EffKey(v.EffectiveFrom);
            if (idx.TryGetValue(eff, out var i)) outList[i] = v;
            else
            {
                idx[eff] = outList.Count;
                outList.Add(v);
            }
        }
        return outList;
    }

    private static string EffKey(string? eff) => string.IsNullOrEmpty(eff) ? "0000-00-00" : eff;

    private static MultiplierVersion? ParseMultiplierVersion(JsonObject vo)
    {
        var eff = vo["effective_from"]?.GetValueKind() == JsonValueKind.String
            ? vo["effective_from"]!.GetValue<string>() : null;
        var periods = vo["periods"] as JsonArray;
        if (periods is null) return null;
        return new MultiplierVersion(eff, ParsePeriods(periods));
    }

    private static IReadOnlyList<MultiplierPeriod> ParsePeriods(JsonArray arr) =>
        arr.OfType<JsonObject>()
           .Select(p => new MultiplierPeriod(
               p["start"]?.GetValue<double>() ?? 0,
               p["end"]?.GetValue<double>() ?? 0,
               p["rate"]?.GetValue<double>() ?? 1.0))
           .ToList();

    private static PriceVersion? ParsePriceVersion(JsonObject vo)
    {
        var eff = vo["effective_from"]?.GetValueKind() == JsonValueKind.String
            ? vo["effective_from"]!.GetValue<string>() : null;
        var currency = vo["currency"]?.GetValueKind() == JsonValueKind.String
            ? vo["currency"]!.GetValue<string>() ?? "USD" : "USD";
        // currency 仅接受 CNY/USD，其它值读取时归 USD + Warn（01-§3.2）
        if (!currency.Equals("CNY", StringComparison.OrdinalIgnoreCase) &&
            !currency.Equals("USD", StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn("Pricing", $"未知货币 {currency}，按 USD 处理");
            currency = "USD";
        }
        currency = currency.ToUpperInvariant();

        var rules = new List<PriceRule>();
        if (vo["rules"] is JsonArray ra)
        {
            foreach (var n in ra.OfType<JsonObject>())
            {
                var start = n["start"]?.GetValue<double>() ?? 0;
                var end = n["end"]?.GetValue<double>() ?? 0;
                List<int>? days = null;
                if (n["days"] is JsonArray da)
                    days = da.OfType<JsonNode>().Select(d => (int)d.GetValue<double>()).ToList();
                var input1m = n["input_per_1m"]?.GetValue<double>() ?? 0;
                var cache1m = n["cache_per_1m"]?.GetValue<double>() ?? 0;
                var output1m = n["output_per_1m"]?.GetValue<double>() ?? 0;
                // per-1K 旧字段迁移：≠0 且对应 per_1m 缺失(==0) → ×1000（原 Load 语义，迁移后旧字段不落新模型）
                if (n["input_per_1k"] is { } ik && ik.GetValue<double>() != 0 && input1m == 0) input1m = ik.GetValue<double>() * 1000;
                if (n["cache_per_1k"] is { } ck && ck.GetValue<double>() != 0 && cache1m == 0) cache1m = ck.GetValue<double>() * 1000;
                if (n["output_per_1k"] is { } ok && ok.GetValue<double>() != 0 && output1m == 0) output1m = ok.GetValue<double>() * 1000;
                rules.Add(new PriceRule(start, end, days, input1m, cache1m, output1m));
            }
        }
        return new PriceVersion(eff, currency, rules);
    }
}
