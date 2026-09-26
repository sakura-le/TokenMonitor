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
    Action OpenPanel, Action OpenSettings, Action OpenConfigFile, Action MultiplierConfig, Action PricingConfig,
    Action<int> TimezoneSelected,
    Action ToggleEffectiveMode,
    Action ExportToday, Action ExportMonth, Action ExportRecent7Days, Action OpenExportDir,
    Action ManualCalibrate, Action RollbackLastCalibration, Action ReloadConfig,
    Action ToggleAutoStart, Action<int> BallOpacitySelected,
    Action ToggleBallTopmost, Action ToggleBallShape, Action ImportLegacy, Action Exit);

/// <summary>
/// C-11 TrayMenu —— Hardcodet NotifyIcon 托盘全项菜单（01-§3-E1 + 03-ui-spec §1.5）。
/// 状态行 / 打开面板 / 打开配置文件 / 倍率 / 计价 / 统计时区(53 项单选) / 生效日期基准 /
/// 导出(今日·本月·近7日·今日小时明细·打开导出目录) / 手动补录 / 校准回滚 / 重载配置 /
/// 开机自启 / 悬浮球(切换形态 + 透明度 40-100 + 置顶) / 导入旧数据 / 皮肤(4 项单选) / 退出。
/// 可靠性规则（修复"菜单共享实例导致焦点异常"）：托盘与悬浮球各持一份独立 ContextMenu 实例，
/// 同一构建函数、同一回调；勾选/状态刷新同步应用到两份。
/// </summary>
public sealed class TrayController : IDisposable
{
    /// <summary>单份菜单实例 + 其可刷新控件引用。</summary>
    private sealed class MenuRefs
    {
        public ContextMenu Menu = null!;
        public MenuItem? Status, EffUtc, EffLocal, AutoStart, BallTopmost, Rollback;
        public readonly List<MenuItem> TzItems = new();
        public readonly List<(MenuItem Item, int Percent)> BallOpacity = new();
        public readonly List<(MenuItem Item, string Key)> Skins = new();
    }

    private readonly AppServices _svc;
    private TaskbarIcon? _icon;
    private TrayMenuCallbacks? _cb;
    private MenuRefs _tray = null!;
    private MenuRefs _ball = null!;

    /// <summary>托盘用菜单（DialogService 的菜单关闭屏障读取它）。</summary>
    public ContextMenu? Menu => _tray?.Menu;

    /// <summary>悬浮球右键用菜单（独立实例，避免跨 PlacementTarget 共享）。</summary>
    public ContextMenu? BallMenu => _ball?.Menu;

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
        _tray = BuildMenu(callbacks);
        _icon.ContextMenu = _tray.Menu;
        _ball = BuildMenu(callbacks);
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

    private MenuRefs BuildMenu(TrayMenuCallbacks cb)
    {
        var r = new MenuRefs();
        var menu = new ContextMenu();

        // 状态行（禁用态）
        r.Status = new MenuItem { Header = "● 代理运行中 · --", IsEnabled = false, FontWeight = FontWeights.Bold };
        menu.Items.Add(r.Status);
        menu.Items.Add(new Separator { Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuSeparator") });

        menu.Items.Add(Item("打开面板", cb.OpenPanel));
        menu.Items.Add(Item("设置…", cb.OpenSettings));
        menu.Items.Add(Item("打开配置文件", cb.OpenConfigFile));
        menu.Items.Add(Sep());

        menu.Items.Add(Item("倍率配置…", cb.MultiplierConfig));
        menu.Items.Add(Item("计价配置…", cb.PricingConfig));

        // 统计时区子菜单（-720..840 每 30 分钟 = 53 项）
        var tz = new MenuItem { Header = "统计时区", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        foreach (var off in AllOffsets())
        {
            var captured = off;
            var mi = Item(Fmt.OffsetLabel(off) + (off == 480 ? " · 北京" : ""), () => cb.TimezoneSelected(captured));
            mi.IsCheckable = true;
            r.TzItems.Add(mi);
            tz.Items.Add(mi);
        }
        menu.Items.Add(tz);

        // 生效日期基准
        var eff = new MenuItem { Header = "生效日期基准", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        r.EffUtc = Item("UTC（按协调世界时解释）", cb.ToggleEffectiveMode);
        r.EffLocal = Item("LOCAL（按本地时区解释）", cb.ToggleEffectiveMode);
        eff.Items.Add(r.EffUtc);
        eff.Items.Add(r.EffLocal);
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
        r.Rollback = Item("校准回滚", cb.RollbackLastCalibration);
        menu.Items.Add(r.Rollback);
        menu.Items.Add(Item("重载配置", cb.ReloadConfig));
        menu.Items.Add(Sep());

        r.AutoStart = Item("开机自启", cb.ToggleAutoStart);
        r.AutoStart.IsCheckable = true;
        menu.Items.Add(r.AutoStart);

        // 悬浮球子菜单
        var ball = new MenuItem { Header = "悬浮球", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        ball.Items.Add(Item("切换形态（圆球 / 跑马灯栏）", cb.ToggleBallShape));
        var opSub = new MenuItem { Header = "透明度", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        foreach (var pct in new[] { 40, 60, 80, 100 })
        {
            var captured = pct;
            var mi = Item(pct + "%", () => cb.BallOpacitySelected(captured));
            mi.IsCheckable = true;
            r.BallOpacity.Add((mi, pct));
            opSub.Items.Add(mi);
        }
        ball.Items.Add(opSub);
        r.BallTopmost = Item("窗口置顶", cb.ToggleBallTopmost);
        r.BallTopmost.IsCheckable = true;
        ball.Items.Add(r.BallTopmost);
        menu.Items.Add(ball);

        menu.Items.Add(Item("导入旧数据…", cb.ImportLegacy));
        menu.Items.Add(Sep());

        // 皮肤子菜单（§6）：只显示皮肤名称（描述文字过长，用户要求在菜单里省略）
        var skin = new MenuItem { Header = "皮肤", Style = (Style)System.Windows.Application.Current.FindResource("Tg.MenuItem") };
        foreach (var key in ThemeService.SkinKeys)
        {
            var captured = key;
            var mi = Item(ThemeService.DisplayName(key),
                () => { _svc.Theme.ApplySkin(captured); RefreshChecks(); });
            mi.ToolTip = ThemeService.Description(key);
            mi.IsCheckable = true;
            r.Skins.Add((mi, key));
            skin.Items.Add(mi);
        }
        menu.Items.Add(skin);
        menu.Items.Add(Sep());
        menu.Items.Add(DangerItem("退出", cb.Exit));

        r.Menu = menu;
        return r;
    }

    private static IReadOnlyList<int> AllOffsets()
    {
        // ISysUtil.TimezoneOffsets() 在 Engine 内部；契约允许 App 直接列（-720..840 步长 30）
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
        if (_tray is null) return;
        var state = isListening ? "代理运行中" : "代理已停止" + (error is null ? "" : " · " + error);
        var header = (isListening ? "● " : "○ ") + state + " · " + listenAddr;
        foreach (var m in new[] { _tray, _ball })
            if (m.Status is not null) m.Status.Header = header;
    }

    /// <summary>刷新勾选态（ConfigChanged 后与启动时）；同步应用到托盘与球两份菜单。</summary>
    public void RefreshChecks()
    {
        try
        {
            var s = _svc.Engine.Config.Settings;
            var offset = s.OffsetMin;
            var localMode = s.EffectiveDateMode == "local";

            // 开机自启
            var autoOn = IsAutoStart();

            // 球透明度（settings.ball_opacity 0-255 → 最近档）
            var pct = s.BallOpacity <= 0 ? 100 : (int)Math.Round(s.BallOpacity / 255.0 * 100);

            // 校准回滚可用态
            bool? rollback = null;
            try { rollback = _svc.Engine.Calibration.State.HasCalibrated; } catch { /* 读取失败保持现状 */ }

            foreach (var m in new[] { _tray, _ball })
            {
                foreach (var mi in m.TzItems)
                {
                    var label = ((string)mi.Header).Split(" ·")[0];
                    mi.IsChecked = label == Fmt.OffsetLabel(offset);
                }
                if (m.EffUtc is not null) m.EffUtc.IsChecked = !localMode;
                if (m.EffLocal is not null) m.EffLocal.IsChecked = localMode;
                if (m.AutoStart is not null) m.AutoStart.IsChecked = autoOn;
                var nearest = m.BallOpacity.OrderBy(x => Math.Abs(x.Percent - pct)).First().Percent;
                foreach (var (item, p) in m.BallOpacity) item.IsChecked = p == nearest;
                if (m.BallTopmost is not null) m.BallTopmost.IsChecked = s.BallTopmost;
                foreach (var (item, key) in m.Skins) item.IsChecked = key == _svc.Theme.Current;
                if (rollback is not null && m.Rollback is not null) m.Rollback.IsEnabled = rollback.Value;
            }
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
        if (_tray is null) return;
        foreach (var m in new[] { _tray, _ball })
            if (m.Rollback is not null) m.Rollback.IsEnabled = enabled;
    }

    public void ShowBalloon(string title, string message)
        => _icon?.ShowBalloonTip(title, message, BalloonIcon.Info);

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
