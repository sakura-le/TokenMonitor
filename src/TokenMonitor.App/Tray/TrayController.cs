using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Drawing;
using Hardcodet.Wpf.TaskbarNotification;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.Services;

namespace TokenMonitor.App.Tray;

/// <summary>托盘菜单回调集（01-§2.3.10 契约；动作实现由 ViewModel/App 注入）。</summary>
public sealed record TrayMenuCallbacks(
    Action OpenPanel, Action OpenConfigFile, Action MultiplierConfig, Action PricingConfig,
    Action<int> TimezoneSelected,
    Action ToggleEffectiveMode,
    Action ExportToday, Action ExportMonth, Action ExportRecent7Days, Action OpenExportDir,
    Action ManualCalibrate, Action RollbackLastCalibration, Action ReloadConfig,
    Action ToggleAutoStart, Action<int> BallOpacitySelected,
    Action ToggleBallTopmost, Action ImportLegacy, Action Exit);

/// <summary>
/// C-11 TrayMenu —— Hardcodet NotifyIcon 托盘全项菜单（01-§3-E1 + 03-ui-spec §1.5）。
/// 状态行 / 打开面板 / 打开配置文件 / 倍率 / 计价 / 统计时区(53 项单选) / 生效日期基准 /
/// 导出(今日·本月·近7日·今日小时明细·打开导出目录) / 手动补录 / 校准回滚 / 重载配置 /
/// 开机自启 / 悬浮球(透明度 40-100 + 置顶) / 导入旧数据 / 皮肤(4 项单选) / 退出。
/// Menu 实例同时供悬浮球右键共享。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly AppServices _svc;
    private TaskbarIcon? _icon;
    private TrayMenuCallbacks? _cb;
    private readonly List<MenuItem> _tzItems = new();
    private MenuItem? _miUtcMode, _miLocalMode, _miAutoStart, _miBallTopmost;
    private readonly List<(MenuItem Item, int Percent)> _ballOpacityItems = new();
    private readonly List<(MenuItem Item, string Key)> _skinItems = new();
    private MenuItem? _miRollback, _miStatus;

    public ContextMenu? Menu => _icon?.ContextMenu;

    public TrayController(AppServices svc) => _svc = svc;

    public void Initialize(TrayMenuCallbacks callbacks)
    {
        _cb = callbacks;
        _icon = new TaskbarIcon
        {
            ToolTipText = "Token Monitor — Token 用量监控",
            Icon = TryGetIcon(),
            Visibility = Visibility.Visible,
        };
        _icon.TrayMouseDoubleClick += (_, _) => _cb.OpenPanel();
        _icon.ContextMenu = BuildMenu();
    }

    private static Icon? TryGetIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app.ico");
            var sInfo = System.Windows.Application.GetResourceStream(uri);
            return sInfo is not null ? new Icon(sInfo.Stream) : SystemIcons.Application;
        }
        catch { return SystemIcons.Application; }
    }

    private ContextMenu BuildMenu()
    {
        var cb = _cb!;
        var menu = new ContextMenu();

        // 状态行（禁用态）
        _miStatus = new MenuItem { Header = "● 代理运行中 · --", IsEnabled = false, FontWeight = FontWeights.Bold };
        menu.Items.Add(_miStatus);
        menu.Items.Add(new Separator { Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuSeparator") });

        menu.Items.Add(Item("打开面板", cb.OpenPanel));
        menu.Items.Add(Item("打开配置文件", cb.OpenConfigFile));
        menu.Items.Add(Sep());

        menu.Items.Add(Item("倍率配置…", cb.MultiplierConfig));
        menu.Items.Add(Item("计价配置…", cb.PricingConfig));

        // 统计时区子菜单（-720..840 每 30 分钟 = 53 项）
        var tz = new MenuItem { Header = "统计时区", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        foreach (var off in _svc.Engine.Config is null ? TimezoneDefaults() : SysOffsets())
        {
            var captured = off;
            var mi = Item(Fmt.OffsetLabel(off) + (off == 480 ? " · 北京" : ""), () => cb.TimezoneSelected(captured));
            mi.IsCheckable = true;
            _tzItems.Add(mi);
            tz.Items.Add(mi);
        }
        menu.Items.Add(tz);

        // 生效日期基准
        var eff = new MenuItem { Header = "生效日期基准", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        _miUtcMode = Item("UTC（按协调世界时解释）", cb.ToggleEffectiveMode);
        _miLocalMode = Item("LOCAL（按本地时区解释）", cb.ToggleEffectiveMode);
        eff.Items.Add(_miUtcMode);
        eff.Items.Add(_miLocalMode);
        menu.Items.Add(eff);
        menu.Items.Add(Sep());

        // 导出子菜单
        var export = new MenuItem { Header = "导出", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        export.Items.Add(Item("今日", cb.ExportToday));
        export.Items.Add(Item("本月", cb.ExportMonth));
        export.Items.Add(Item("近 7 日", cb.ExportRecent7Days));
        export.Items.Add(Item("今日小时明细", () => cb.ExportToday()));
        export.Items.Add(Sep());
        export.Items.Add(Item("打开导出目录", cb.OpenExportDir));
        menu.Items.Add(export);

        menu.Items.Add(Item("手动补录…", cb.ManualCalibrate));
        _miRollback = Item("校准回滚", cb.RollbackLastCalibration);
        menu.Items.Add(_miRollback);
        menu.Items.Add(Item("重载配置", cb.ReloadConfig));
        menu.Items.Add(Sep());

        _miAutoStart = Item("开机自启", cb.ToggleAutoStart);
        _miAutoStart.IsCheckable = true;
        menu.Items.Add(_miAutoStart);

        // 悬浮球子菜单
        var ball = new MenuItem { Header = "悬浮球", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        var opSub = new MenuItem { Header = "透明度", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        foreach (var pct in new[] { 40, 60, 80, 100 })
        {
            var captured = pct;
            var mi = Item(pct + "%", () => cb.BallOpacitySelected(captured));
            mi.IsCheckable = true;
            _ballOpacityItems.Add((mi, pct));
            opSub.Items.Add(mi);
        }
        ball.Items.Add(opSub);
        _miBallTopmost = Item("窗口置顶", cb.ToggleBallTopmost);
        _miBallTopmost.IsCheckable = true;
        ball.Items.Add(_miBallTopmost);
        menu.Items.Add(ball);

        menu.Items.Add(Item("导入旧数据…", cb.ImportLegacy));
        menu.Items.Add(Sep());

        // 皮肤子菜单（§6）
        var skin = new MenuItem { Header = "皮肤", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        foreach (var key in ThemeService.SkinKeys)
        {
            var captured = key;
            var mi = Item($"{ThemeService.DisplayName(key)}　{ThemeService.Description(key)}", () => _svc.Theme.ApplySkin(captured));
            mi.IsCheckable = true;
            _skinItems.Add((mi, key));
            skin.Items.Add(mi);
        }
        menu.Items.Add(skin);
        menu.Items.Add(Sep());
        menu.Items.Add(DangerItem("退出", cb.Exit));
        return menu;
    }

    private IReadOnlyList<int> SysOffsets()
    {
        try { return _svc.Engine is null ? TimezoneDefaults() : AllOffsets(); }
        catch { return TimezoneDefaults(); }
    }

    private IReadOnlyList<int> AllOffsets()
    {
        // ISysUtil.TimezoneOffsets() 在 Engine 内部；契约允许 App 直接列（-720..840 步长 30）
        var list = new List<int>();
        for (var m = -720; m <= 840; m += 30) list.Add(m);
        return list;
    }

    private static IReadOnlyList<int> TimezoneDefaults()
    {
        var list = new List<int>();
        for (var m = -720; m <= 840; m += 30) list.Add(m);
        return list;
    }

    private static MenuItem Item(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.SetResourceReference(FrameworkElement.StyleProperty, "Tg.MenuItem");
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static MenuItem DangerItem(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.SetResourceReference(FrameworkElement.StyleProperty, "Tg.MenuItemDanger");
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static Separator Sep() =>
        new() { Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuSeparator") };

    /// <summary>更新状态行文案（ProxyStateChanged）。</summary>
    public void SetStatus(bool isListening, string listenAddr, string? error)
    {
        if (_miStatus is null) return;
        var state = isListening ? "代理运行中" : "代理已停止" + (error is null ? "" : " · " + error);
        _miStatus.Header = (isListening ? "● " : "○ ") + state + " · " + listenAddr;
    }

    /// <summary>刷新勾选态（ConfigChanged 后与启动时）。</summary>
    public void RefreshChecks()
    {
        try
        {
            var s = _svc.Engine.Config.Settings;
            var offset = s.OffsetMin;
            foreach (var mi in _tzItems)
            {
                var label = ((string)mi.Header).Split(" ·")[0];
                mi.IsChecked = label == Fmt.OffsetLabel(offset);
            }
            var localMode = s.EffectiveDateMode == "local";
            if (_miUtcMode is not null) _miUtcMode.IsChecked = !localMode;
            if (_miLocalMode is not null) _miLocalMode.IsChecked = localMode;

            // 开机自启
            if (_miAutoStart is not null) _miAutoStart.IsChecked = IsAutoStart();

            // 球透明度（settings.ball_opacity 0-255 → 最近档）
            var pct = s.BallOpacity <= 0 ? 100 : (int)Math.Round(s.BallOpacity / 255.0 * 100);
            var nearest = _ballOpacityItems.OrderBy(x => Math.Abs(x.Percent - pct)).First().Percent;
            foreach (var (item, p) in _ballOpacityItems) item.IsChecked = p == nearest;
            if (_miBallTopmost is not null) _miBallTopmost.IsChecked = s.BallTopmost;

            // 皮肤
            foreach (var (item, key) in _skinItems) item.IsChecked = key == _svc.Theme.Current;

            // 校准回滚可用态
            try { SetRollbackEnabled(_svc.Engine.Calibration.State.HasCalibrated); }
            catch { /* state 读取失败保持现状 */ }
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Warn("Tray", "RefreshChecks failed: " + ex.Message);
        }
    }

    private static bool IsAutoStart()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("TokenMonitor") is not null;
    }

    /// <summary>回滚项可用态（CalibrateCompleted/启动时）。</summary>
    public void SetRollbackEnabled(bool enabled)
    {
        if (_miRollback is not null) _miRollback.IsEnabled = enabled;
    }

    public void ShowBalloon(string title, string message)
        => _icon?.ShowBalloonTip(title, message, BalloonIcon.Info);

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
