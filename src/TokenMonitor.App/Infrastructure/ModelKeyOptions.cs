namespace TokenMonitor.App.Infrastructure;

/// <summary>
/// 配置对话框的模型键候选与遗留键兼容（计价/倍率配置共用）。
/// <para>
/// 规范键 = 卡片与 pricing.json 的完整键 "provider/model"。历史遗留的不带 provider 前缀的键
/// （旧版对话框剥掉前缀后写入，如 "deepseek-flash" 之于 "DeepSeek/deepseek-flash"）只作只读别名：
/// </para>
/// <list type="bullet">
/// <item>存在同名规范键时不再进候选列表——它按序排在规范键之前（'-' &lt; '/'），会被误选，
/// 保存后写进一个没有卡片的键，表现为"保存了但不生效"；</item>
/// <item>读取时回退到别名，让历史遗留在裸键上的配置仍能被看到，并在下次保存时改写到规范键。</item>
/// </list>
/// </summary>
internal static class ModelKeyOptions
{
    /// <summary>模型名（第一个 '/' 之后；无 '/' 视为整个键）。</summary>
    public static string ModelName(string key)
    {
        var i = key.IndexOf('/');
        return i < 0 ? key : key[(i + 1)..];
    }

    /// <summary>候选键（升序、忽略大小写）：全部键去掉被同名规范键遮蔽的遗留裸键。</summary>
    public static IReadOnlyList<string> Build(IEnumerable<string> configuredKeys, IEnumerable<string> cardKeys)
    {
        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in configuredKeys) if (!string.IsNullOrWhiteSpace(k)) all.Add(k);
        foreach (var k in cardKeys) if (!string.IsNullOrWhiteSpace(k)) all.Add(k);

        var canonicalNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in all)
            if (k.Contains('/')) canonicalNames.Add(ModelName(k));

        var result = new List<string>(all.Count);
        foreach (var k in all)
        {
            if (!k.Contains('/') && canonicalNames.Contains(k)) continue; // 被同名规范键遮蔽的遗留裸键
            result.Add(k);
        }
        return result;
    }

    /// <summary>按规范键取值；缺失时回退到同模型名的遗留裸键。命中规范键优先。</summary>
    public static T? Resolve<T>(string? modelKey, IReadOnlyDictionary<string, T> map) where T : class
    {
        if (string.IsNullOrWhiteSpace(modelKey)) return null;
        if (map.TryGetValue(modelKey, out var hit)) return hit;
        var name = ModelName(modelKey);
        if (name == modelKey) return null; // 本身已是裸键，无需回退
        foreach (var (k, v) in map)
            if (k.IndexOf('/') < 0 && string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    /// <summary>
    /// 保存后的可见性校验：该键没有任何卡片消费（也没有同模型名的卡片）→ 返回提示文案，否则 null。
    /// 用于把"保存成功但界面上看不到任何变化"变成一句明确警告。
    /// </summary>
    public static string? NoCardWarning(string modelKey, IEnumerable<string> cardKeys)
    {
        var name = ModelName(modelKey);
        foreach (var k in cardKeys)
        {
            if (string.Equals(k, modelKey, StringComparison.OrdinalIgnoreCase)) return null;
            if (string.Equals(ModelName(k), name, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return $"已保存到「{modelKey}」，但当前没有任何卡片对应这个模型键，界面上不会看到金额变化。\n" +
               $"请确认左侧「模型」选的是卡片上显示的那个模型（形如 provider/model）。";
    }
}
