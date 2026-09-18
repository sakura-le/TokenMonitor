namespace TokenMonitor.Core.Pricing;

/// <summary>
/// UTC↔Local 时段/规则平移纯函数（02-§4.5，与原 shiftPeriodsToLocal/shiftPriceRulesToLocal
/// 及前端 tzShift.js 同口径）：整体平移 +shift、跨午夜拆分、星期平移、全表同值合并。
/// 供引擎匹配与 Local 口径编辑往返共用（D4-5）。
/// </summary>
public static class PricingShift
{
    private const double Eps = TimeMath.Epsilon;

    /// <summary>倍率时段平移（无星期维度；对齐原 shiftPeriodsToLocal）。</summary>
    public static IReadOnlyList<MultiplierPeriod> ShiftPeriodsToLocal(IReadOnlyList<MultiplierPeriod> periods, int offsetMin)
    {
        var shift = offsetMin / 60.0;
        var segs = new List<(double Start, double End, double Rate)>(periods.Count);
        foreach (var p in periods)
        {
            var s = p.Start + shift;
            var e = p.End + shift;
            while (s >= 24) { s -= 24; e -= 24; }
            while (s < 0) { s += 24; e += 24; }
            if (e > 24 + Eps)
            {
                if (24 - s > Eps) segs.Add((s, 24, p.Rate));
                if (e - 24 > Eps) segs.Add((0, e - 24, p.Rate));
            }
            else if (e > s + Eps)
            {
                segs.Add((s, e, p.Rate));
            }
        }
        segs = segs.OrderBy(x => x.Start).ToList(); // 稳定排序（同 start 保声明序，保证输出确定）
        // 全表合并（tzShift.js mergeAdjacent 变体，D4-1：不只看前一条，避免可合并对被隔开）
        var merged = new List<MultiplierPeriod>(segs.Count);
        foreach (var seg in segs)
        {
            var hit = false;
            for (var i = 0; i < merged.Count; i++)
            {
                if (Math.Abs(merged[i].End - seg.Start) < Eps && merged[i].Rate.Equals(seg.Rate))
                {
                    merged[i] = merged[i] with { End = seg.End };
                    hit = true;
                    break;
                }
            }
            if (!hit) merged.Add(new MultiplierPeriod(seg.Start, seg.End, seg.Rate));
        }
        return merged;
    }

    /// <summary>计价规则平移（含星期平移；对齐原 shiftPriceRulesToLocal）。</summary>
    public static IReadOnlyList<PriceRule> ShiftPriceRulesToLocal(IReadOnlyList<PriceRule> rules, int offsetMin)
    {
        var shift = offsetMin / 60.0;
        var segs = new List<(double Start, double End, PriceRule Rule)>(rules.Count);
        foreach (var r in rules)
        {
            var s = r.Start + shift;
            var e = r.End + shift;
            // dayOffset 用"原始起点+shift"在归一前计算，可为负（floor 向 −∞，02-§4.5 例证）
            var dayOffset = (int)Math.Floor((r.Start + shift) / 24.0);
            while (s >= 24) { s -= 24; e -= 24; }
            while (s < 0) { s += 24; e += 24; }
            if (e > 24 + Eps)
            {
                if (24 - s > Eps)
                    segs.Add((s, 24, r with { Days = ShiftDays(r.Days, dayOffset), Start = s, End = 24 }));
                if (e - 24 > Eps)
                    segs.Add((0, e - 24, r with { Days = ShiftDays(r.Days, dayOffset + 1), Start = 0, End = e - 24 }));
            }
            else if (e > s + Eps)
            {
                segs.Add((s, e, r with { Days = ShiftDays(r.Days, dayOffset), Start = s, End = e }));
            }
        }
        segs = segs.OrderBy(x => x.Start).ToList(); // 稳定排序（同 start 保声明序，保证输出确定）
        var merged = new List<PriceRule>(segs.Count);
        foreach (var seg in segs)
        {
            var hit = false;
            for (var i = 0; i < merged.Count; i++)
            {
                if (Math.Abs(merged[i].End - seg.Start) < Eps && SameRuleValue(merged[i], seg.Rule))
                {
                    merged[i] = merged[i] with { End = seg.End };
                    hit = true;
                    break;
                }
            }
            if (!hit) merged.Add(seg.Rule);
        }
        return merged;
    }

    /// <summary>星期列表整体平移 n 天（1=周一..7=周日循环；空=每天，平移后仍为空）。</summary>
    public static IReadOnlyList<int>? ShiftDays(IReadOnlyList<int>? days, int n)
    {
        if (days is null || days.Count == 0) return days;
        n = ((n % 7) + 7) % 7;
        if (n == 0) return days.ToList();
        var outList = new List<int>(days.Count);
        foreach (var d in days) outList.Add(((d - 1 + n) % 7 + 7) % 7 + 1);
        return outList;
    }

    /// <summary>两条规则除 Start/End 外是否完全一致（单价逐项相等 + Days 排序后逐元素相等；空=每天，空对空相等）。</summary>
    public static bool SameRuleValue(PriceRule a, PriceRule b)
    {
        if (!a.InputPer1M.Equals(b.InputPer1M) || !a.CachePer1M.Equals(b.CachePer1M) ||
            !a.OutputPer1M.Equals(b.OutputPer1M))
            return false;
        var ad = a.Days ?? [];
        var bd = b.Days ?? [];
        if (ad.Count != bd.Count) return false;
        if (ad.Count == 0) return true;
        var sa = ad.OrderBy(x => x).ToArray();
        var sb = bd.OrderBy(x => x).ToArray();
        return sa.SequenceEqual(sb);
    }
}
