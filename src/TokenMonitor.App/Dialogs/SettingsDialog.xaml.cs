using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.Windows;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.Services;
using TokenMonitor.Core.Config;

namespace TokenMonitor.App.Dialogs;

/// <summary>供应商行 VM：名称 / 上游地址 / API Key / 模型前缀（逗号分隔）。
/// <see cref="Source"/> 保留原 ProviderConfig，保存时按行回填，MaxTokens/StripParams 等
/// 窗口未覆盖的高级项不丢失（它们仍走配置文件，见页脚提示）。</summary>
public partial class ProviderRowVm : System.ComponentModel.INotifyPropertyChanged
{
    public ProviderConfig Source { get; }

    private string _name, _baseUrl, _apiKey, _prefixes;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, string n) { if (!Equals(f, v)) { f = v; PropertyChanged?.Invoke(this, new(n)); } }

    public ProviderRowVm(ProviderConfig source)
    {
        Source = source;
        _name = source.Name;
        _baseUrl = source.BaseUrl;
        _apiKey = source.ApiKey;
        _prefixes = string.Join(",", source.ModelPrefix);
    }

    /// <summary>空白新增行（占位默认值，用户改掉即可）。</summary>
    public ProviderRowVm()
    {
        Source = new ProviderConfig("", "", "", [], null, null);
        _name = "";
        _baseUrl = "https://";
        _apiKey = "";
        _prefixes = "";
    }

    public string Name { get => _name; set => Set(ref _name, value, nameof(Name)); }
    public string BaseUrl { get => _baseUrl; set => Set(ref _baseUrl, value, nameof(BaseUrl)); }
    public string ApiKey { get => _apiKey; set => Set(ref _apiKey, value, nameof(ApiKey)); }

    /// <summary>模型前缀，逗号分隔（请求 model 命中前缀即路由到本供应商）。</summary>
    public string Prefixes { get => _prefixes; set => Set(ref _prefixes, value, nameof(Prefixes)); }
}

/// <summary>
/// 对话框 8：设置（把散在托盘菜单与 config.json 里的可配置项收进一个窗口，小白友好）。
/// 代理：监听地址 + 供应商（名称/上游/API Key/模型前缀，增删行）→ SaveProxy + ReloadProxyConfig
/// （地址变更由引擎自动重启监听）；通用：生效日期基准 / 统计时区 / 悬浮球透明度与置顶 / 开机自启。
/// 一律「保存并生效」时统一应用；MaxTokens/StripParams/默认供应商等高级项仍走配置文件。
/// </summary>
public partial class SettingsDialog : ShellDialog
{
    private readonly AppServices _svc;
    public ObservableCollection<ProviderRowVm> Providers { get; } = new();
    public ObservableCollection<TzOption> Timezones { get; } = new();

    private string _listenAddr = "";
    private bool _isLocalMode = true;
    private double _ballOpacityPct = 80;
    private bool _ballTopmost = true;
    private bool _autoStart;
    private TzOption? _selectedTz;
    private string? _statusText;

    public RelayCommand AddProviderCommand { get; }
    public RelayCommand<ProviderRowVm> RemoveProviderCommand { get; }
    public RelayCommand OpenConfigFileCommand { get; }
    public RelayCommand OpenExportDirCommand { get; }

    public SettingsDialog(AppServices svc)
    {
        // 命令先于 InitializeComponent 赋值（见 CardVisibilityDialog 同注）
        _svc = svc;
        AddProviderCommand = new RelayCommand(() => Providers.Add(new ProviderRowVm()));
        RemoveProviderCommand = new RelayCommand<ProviderRowVm>(r => { if (r is not null) Providers.Remove(r); });
        OpenConfigFileCommand = new RelayCommand(OpenConfigFile);
        OpenExportDirCommand = new RelayCommand(OpenExportDir);

        // 现值载入必须先于 InitializeComponent：绑定在 XAML 应用时求值，
        // 之后再用字段赋值不会触发 PropertyChanged，界面会停留在字段初始值。
        var proxy = svc.Engine.Config.Proxy;
        _listenAddr = proxy.ListenAddr;
        _defaultProvider = proxy.DefaultProvider;
        foreach (var p in proxy.Providers) Providers.Add(new ProviderRowVm(p));

        var s = svc.Engine.Config.Settings;
        _isLocalMode = s.EffectiveDateMode != "utc";
        _ballOpacityPct = Math.Round(s.BallOpacity / 255.0 * 100);
        _ballTopmost = s.BallTopmost;
        _autoStart = AutoStart.IsEnabled();
        for (var m = -720; m <= 840; m += 30) Timezones.Add(new TzOption(Fmt.OffsetLabel(m), m));
        _selectedTz = Timezones.FirstOrDefault(t => t.Minutes == s.OffsetMin) ?? Timezones[^1];
        _statusText = (svc.Engine.Proxy.IsListening ? "监听中 " : "未监听 ") + svc.Engine.Proxy.ListenAddr;

        InitializeComponent();
        OnConfirm = Save;
    }

    private string? _defaultProvider;

    public string ListenAddr { get => _listenAddr; set => Set(ref _listenAddr, value); }
    public bool IsLocalMode { get => _isLocalMode; set => Set(ref _isLocalMode, value); }
    public TzOption? SelectedTz { get => _selectedTz; set => Set(ref _selectedTz, value); }
    public double BallOpacityPct { get => _ballOpacityPct; set => Set(ref _ballOpacityPct, value); }
    public bool BallTopmost { get => _ballTopmost; set => Set(ref _ballTopmost, value); }
    public bool AutoStartOn { get => _autoStart; set => Set(ref _autoStart, value); }
    public string? StatusText { get => _statusText; set => Set(ref _statusText, value); }

    /// <summary>时区下拉项。</summary>
    public sealed record TzOption(string Label, int Minutes)
    {
        public override string ToString() => Label;
    }

    private void OpenConfigFile()
    {
        try
        {
            var path = System.IO.Path.Combine(_svc.Engine.Config.DataDir, "config.json");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { Core.SysUtil.Logger.Warn("App", "open config failed: " + ex.Message); }
    }

    private void OpenExportDir()
    {
        try
        {
            var dir = System.IO.Path.Combine(_svc.Engine.Config.DataDir, "export");
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex) { Core.SysUtil.Logger.Warn("App", "open export dir failed: " + ex.Message); }
    }

    /// <summary>保存：代理 → 通用 → 自启，全部校验通过才落盘；任一失败保持原状并提示。</summary>
    private bool Save()
    {
        // —— 1) 代理 ——
        var listen = ListenAddr.Trim();
        if (!TryValidateListen(listen, out var listenErr)) return Fail(listenErr);
        if (Providers.Count == 0) return Fail("至少需要一家供应商");

        var list = new List<ProviderConfig>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        var usedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in Providers)
        {
            var name = r.Name.Trim();
            if (name.Length == 0) return Fail("供应商名称不能为空");
            if (!usedNames.Add(name)) return Fail($"供应商名称重复：{name}");
            var baseUrl = r.BaseUrl.Trim();
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return Fail($"「{name}」上游地址必须是 http:// 或 https:// 开头的完整地址");
            var prefixes = new List<string>();
            foreach (var raw in r.Prefixes.Split(',', '，', ';', '；', ' '))
            {
                var p = raw.Trim();
                if (p.Length == 0) continue;
                if (!usedPrefixes.Add(p)) return Fail($"模型前缀跨供应商重复：{p}");
                prefixes.Add(p);
            }
            if (prefixes.Count == 0)
                return Fail($"「{name}」模型前缀至少填一个（请求里 model 的前缀，如 deepseek-，多个用逗号分隔）");
            list.Add(r.Source with { Name = name, BaseUrl = baseUrl, ApiKey = r.ApiKey.Trim(), ModelPrefix = prefixes });
        }

        try
        {
            // 密钥明文只在内存态流转；SaveProxy 落盘前统一 DPAPI 加密
            _svc.Engine.Config.SaveProxy(new ProxyConfig(listen, _defaultProvider, list));
            _svc.Engine.ReloadProxyConfig();   // 地址变更 → 引擎自动 Stop/Start 监听

            // —— 2) 通用 ——
            var cur = _svc.Engine.Config.Settings;
            var opacity = Math.Clamp((int)Math.Round(BallOpacityPct / 100.0 * 255), 0, 255);
            if (opacity != cur.BallOpacity || BallTopmost != cur.BallTopmost)
            {
                _svc.Engine.Config.SaveSettings(cur with { BallOpacity = opacity, BallTopmost = BallTopmost });
                if (AppServices.Instance.BallWindow is { } ball)
                {
                    ball.Opacity = opacity <= 0 ? 1.0 : Math.Clamp(opacity / 255.0, 0.2, 1.0);
                    ball.Topmost = BallTopmost;
                }
            }
            var mode = IsLocalMode ? "local" : "utc";
            if (mode != _svc.Engine.Config.Settings.EffectiveDateMode)
                _svc.Engine.SetEffectiveDateMode(mode);   // 内部含 daily 重算与事件广播
            if (SelectedTz is { } tz && tz.Minutes != _svc.Engine.Config.Settings.OffsetMin)
                _svc.Engine.SetTimezone(tz.Minutes);

            // —— 3) 自启 ——
            if (AutoStartOn != AutoStart.IsEnabled()) AutoStart.Apply(AutoStartOn);

            _svc.Tray?.RefreshChecks();
            _svc.Tray?.ShowBalloon("设置", "已保存并生效");
            Core.SysUtil.Logger.Info("App", $"settings saved: listen={listen} providers={list.Count} " +
                                            $"eff={(IsLocalMode ? "local" : "utc")} tz={SelectedTz?.Minutes ?? 0} autostart={AutoStartOn}");
            return true;
        }
        catch (Exception ex)
        {
            return Fail("保存失败：" + ex.Message);
        }
    }

    /// <summary>与 Core 的 ValidateListenAddr 同规则的友好前置校验（host 只许回环）。</summary>
    private static bool TryValidateListen(string addr, out string error)
    {
        error = "";
        string host, portPart;
        if (addr.StartsWith('['))
        {
            // [::1]:8280 形式（IPv6）
            var close = addr.IndexOf(']');
            if (close < 0 || close + 2 > addr.Length || addr[close + 1] != ':')
            { error = "监听地址格式应为 host:port，IPv6 写作 [::1]:8280"; return false; }
            host = addr[1..close];
            portPart = addr[(close + 2)..];
        }
        else
        {
            var idx = addr.LastIndexOf(':');
            if (idx <= 0 || idx == addr.Length - 1)
            { error = "监听地址格式应为 host:port，例如 127.0.0.1:8280"; return false; }
            host = addr[..idx];
            portPart = addr[(idx + 1)..];
        }
        if (!int.TryParse(portPart, out var port) || port is < 1 or > 65535)
        { error = "端口必须是 1–65535 的数字"; return false; }
        if (host is not ("127.0.0.1" or "localhost" or "::1"))
        { error = "只允许监听本机回环地址（127.0.0.1 / localhost / ::1），这是安全红线"; return false; }
        return true;
    }

    private bool Fail(string msg)
    {
        MessageBox.Show(this, msg, "设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
