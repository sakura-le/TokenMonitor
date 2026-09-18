namespace TokenMonitor.Core;

/// <summary>
/// 模型键值对象（铁律 3 / C19-②）：modelKey = provider + "/" + model，
/// 切分只按第一个 '/'（模型名可含 '/'），禁止 string.Split('/')[0..1] 直接取段。
/// </summary>
public readonly record struct ModelKey(string Provider, string Model)
{
    /// <summary>只按第一个 '/' 切分；无 '/' 时 Provider 为空串、Model 为原键。</summary>
    public static ModelKey Parse(string key)
    {
        var idx = key.IndexOf('/');
        return idx < 0 ? new ModelKey(string.Empty, key) : new ModelKey(key[..idx], key[(idx + 1)..]);
    }

    public override string ToString() => Provider + "/" + Model;

    public static implicit operator string(ModelKey k) => k.ToString();
}
