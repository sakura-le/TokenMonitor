using System.Text.Json;

namespace TokenMonitor.Core.Parser;

/// <summary>
/// IUsageParser 默认实现：归一化规则 = 原 parser.go normalizeUsage + 顶层 reasoning_tokens 别名（D2-1）
/// + total 铁律（01-§0.1，[C3]）+ hit&gt;prompt 防御（D2-2）+ 字段级类型容错（D2-4）。
/// 恒等式收口：CacheMiss = Prompt - CacheHit（显式 miss 字段与恒等式冲突时一律推翻重推导）。
/// 死代码 TeeReaderSplit/ScanSSEData 不移植（C18）。
/// </summary>
public sealed class UsageParser : IUsageParser
{
    public ParseResult ParseJsonResponse(string responseBody, string provider)
    {
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return new ParseResult(null, $"响应顶层不是 JSON 对象（{doc.RootElement.ValueKind}）");
            if (!doc.RootElement.TryGetProperty("usage", out var usageEl) || usageEl.ValueKind != JsonValueKind.Object)
                return new ParseResult(null, null);
            var model = doc.RootElement.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                ? modelEl.GetString() ?? string.Empty
                : string.Empty;
            return new ParseResult(Normalize(usageEl), null);
        }
        catch (JsonException ex)
        {
            return new ParseResult(null, $"解析响应 JSON 失败: {ex.Message}");
        }
        finally
        {
            doc?.Dispose();
        }
    }

    public ParseResult ParseSseDataLine(string dataLine, string provider)
    {
        // 行快路径：不含 "usage" 直接返回（原 strings.Contains 快路径保持）
        if (!dataLine.Contains("\"usage\"", StringComparison.Ordinal)) return new ParseResult(null, null);
        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(dataLine);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("usage", out var usageEl) ||
                usageEl.ValueKind != JsonValueKind.Object)
                return new ParseResult(null, null);
            return new ParseResult(Normalize(usageEl), null);
        }
        catch (JsonException)
        {
            // 畸形 JSON 行 → null（原 ParseSSEUsageLine 语义），不中断流解析
            return new ParseResult(null, null);
        }
        finally
        {
            doc?.Dispose();
        }
    }

    public bool IsJsonRequestBody(string? contentType, ReadOnlySpan<byte> bodyPrefix)
    {
        // Content-Type 含 application/json 即可捕获；缺失时按首个非空白字符 '{' 兼容（D1-2）
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrEmpty(contentType))
        {
            foreach (var b in bodyPrefix)
            {
                if (char.IsWhiteSpace((char)b)) continue;
                return b == (byte)'{';
            }
        }
        return false;
    }

    /// <summary>归一化（02-§2.1 别名优先级表，推导顺序固定）。</summary>
    private static UnifiedUsage Normalize(JsonElement u)
    {
        long prompt = GetInt(u, "prompt_tokens").Value;
        // DashScope 别名兜底：仅当 prompt_tokens 为 0 且 input_tokens 存在（存在性判定，显式 0 算存在）
        var input = GetInt(u, "input_tokens");
        if (prompt == 0 && input.Present) prompt = input.Value;

        long completion = GetInt(u, "completion_tokens").Value;
        var output = GetInt(u, "output_tokens");
        if (completion == 0 && output.Present) completion = output.Value;

        // 缓存命中三级取值：DeepSeek → OpenAI prompt_tokens_details → DashScope input_tokens_details
        long hit = 0;
        var hitEl = GetInt(u, "prompt_cache_hit_tokens");
        if (hitEl.Present) hit = hitEl.Value;
        else if (TryGetInt(Prop(u, "prompt_tokens_details"), "cached_tokens", out var v1)) hit = v1;
        else if (TryGetInt(Prop(u, "input_tokens_details"), "cached_tokens", out var v2)) hit = v2;

        // 缓存未命中：显式字段或推导
        long miss;
        var missEl = GetInt(u, "prompt_cache_miss_tokens");
        if (missEl.Present) miss = missEl.Value;
        else miss = prompt - hit;

        // 思维链：completion_tokens_details.reasoning_tokens → 顶层 reasoning_tokens（D2-1）→ 0
        long reasoning = 0;
        if (TryGetInt(Prop(u, "completion_tokens_details"), "reasoning_tokens", out var r1)) reasoning = r1;
        else
        {
            var r2 = GetInt(u, "reasoning_tokens");
            if (r2.Present) reasoning = r2.Value;
        }

        // 防线 1：hit 越界防御（hit > prompt 视为坏数据，D2-2；原版会产生负 miss）
        if (hit > prompt) hit = prompt;
        // 防线 2：恒等式强制（显式 miss 与恒等式冲突时一律推翻重推导）
        if (prompt != hit + miss) miss = Math.Clamp(prompt - hit, 0, prompt);

        // total 铁律（[C3]）：厂商 total>0 取厂商值，否则推导 prompt+completion（唯一决策点）
        var rawTotal = GetInt(u, "total_tokens").Value;
        var total = rawTotal > 0 ? rawTotal : prompt + completion;

        return new UnifiedUsage(prompt, hit, miss, completion, reasoning, total);
    }

    private static JsonElement Prop(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var el) ? el : default;

    private static (bool Present, long Value) GetInt(JsonElement parent, string name)
    {
        if (TryGetInt(parent, name, out var v)) return (true, v);
        return (false, 0);
    }

    /// <summary>字段级类型容错（D2-4）：Number→取整（截断）；String→Invariant TryParse；其它类型视为缺失。</summary>
    private static bool TryGetInt(JsonElement el, string name, out long value)
    {
        value = 0;
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out var v)) return false;
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                value = (long)v.GetDouble();
                return true;
            case JsonValueKind.String:
                return long.TryParse(v.GetString(), System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out value);
            default:
                return false;
        }
    }
}
