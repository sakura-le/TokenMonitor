using System.Windows;
using TokenMonitor.App.Infrastructure;

namespace TokenMonitor.App.Services;

/// <summary>
/// 皮肤服务（01-附录 C / 03-ui-spec §2.1、§3.2）。
/// 运行时替换 Application.Resources.MergedDictionaries 中的皮肤字典：
/// 先加后删，不重启、不重建控件树；皮肤字典只含
/// Brush/Color/CornerRadius/Thickness/Effect/FontFamily/纹理 DrawingBrush，
/// 切换只使 DynamicResource 引用换值，无闪烁。
/// </summary>
public sealed class ThemeService
{
    public static ThemeService Instance { get; } = new();

    public const string DefaultSkin = "GraphiteTerminal";
    public static readonly string[] SkinKeys = { "EditorialInk", "GraphiteTerminal", "SwissGrid", "SkyHud" };

    private string _current = DefaultSkin;

    /// <summary>当前皮肤键（EditorialInk / GraphiteTerminal / SwissGrid / SkyHud）。</summary>
    public string Current => _current;

    /// <summary>皮肤切换完成（App 内部广播，不占用 Core 事件总线）。</summary>
    public event EventHandler<string>? SkinChanged;

    /// <summary>切换后持久化回调（由 UiStateService 注入，写 ui_state.json: active_skin）。</summary>
    public Action<string>? PersistCallback { get; set; }

    /// <summary>03-ui-spec §3.2 参考实现。</summary>
    public void ApplySkin(string key)
    {
        if (!SkinKeys.Contains(key) || key == _current && SkinFlags.Instance.SkinKey == key)
        {
            if (key == _current) return;
        }
        var app = Application.Current;
        if (app is null) return;

        var md = app.Resources.MergedDictionaries;
        var old = md.FirstOrDefault(d => d.Source?.OriginalString.Contains("Skins/Skin.") == true);
        var skin = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/Skins/Skin.{key}.xaml") };
        md.Add(skin);                    // 先加后删，避免瞬间取不到资源
        if (old is not null) md.Remove(old);

        _current = key;
        SkinFlags.Instance.SetSkin(key); // 同步结构性差异标记（角标/雷达/光标等）
        PersistCallback?.Invoke(key);
        SkinChanged?.Invoke(this, key);
    }

    /// <summary>启动时应用持久化皮肤（不经 PersistCallback，避免回写）。</summary>
    public void ApplySkinOnStartup(string key)
    {
        if (!SkinKeys.Contains(key)) key = DefaultSkin;
        var md = Application.Current.Resources.MergedDictionaries;
        var old = md.FirstOrDefault(d => d.Source?.OriginalString.Contains("Skins/Skin.") == true);
        var skin = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/Skins/Skin.{key}.xaml") };
        md.Add(skin);
        if (old is not null) md.Remove(old);
        _current = key;
        SkinFlags.Instance.SetSkin(key);
    }

    /// <summary>皮肤显示名（托盘菜单/§6）。</summary>
    public static string DisplayName(string key) => key switch
    {
        "EditorialInk" => "纸墨印刷",
        "GraphiteTerminal" => "石墨终端",
        "SwissGrid" => "瑞士网格",
        "SkyHud" => "苍穹空域",
        _ => key,
    };

    /// <summary>皮肤缩略描述（§6）。</summary>
    public static string Description(string key) => key switch
    {
        "EditorialInk" => "浅色 · 米纸账本：衬线标题 + 印章红 + 细规线",
        "GraphiteTerminal" => "深色 · 纯平仪表：等宽字形 + 磷绿 + 刻度标尺",
        "SwissGrid" => "浅色 · 数据海报：粗黑大数字 + 红蓝双色 + 柔角白卡",
        "SkyHud" => "天蓝 · 座舱 HUD：天空蓝主色 + 雷达扫描 + 尾焰橙点睛",
        _ => "",
    };

    /// <summary>透明度下限：全皮肤统一 0.2（对齐原版 20-255 全范围可调）。</summary>
    public double MinOpacity => 0.2;
}
