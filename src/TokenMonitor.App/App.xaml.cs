using System.Threading;
using System.Windows;
using System.Windows.Threading;
using TokenMonitor.App.Services;

namespace TokenMonitor.App;

/// <summary>
/// App 引导（01-§7.1/§7.2 UI 职责）：
/// DPI 兜底 → 全局异常钩子 → 单实例（二次实例置位显示信号后退出）→
/// 引擎 StartAsync → 皮肤应用（active_skin）→ 视图模型/窗口/托盘装配 →
/// 按 ui_state.panel.collapsed 决定显示面板或仅悬浮球 → showPanelSignal 守听。
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showPanelSignal;
    private Thread? _signalWatcher;
    private Core.ITokenMonitorEngine? _engine;

    protected override void OnStartup(StartupEventArgs e)
    {
        // DPI 兜底（app.manifest 已声明 PerMonitorV2；此处为旧系统兜底）
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { /* 旧平台忽略 */ }

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 全局异常兜底（§7.3：UI 线程级不退出，用户可托盘退出）
        DispatcherUnhandledException += (_, args) =>
        {
            Core.SysUtil.Logger.Error("App", "UI 未捕获异常", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Core.SysUtil.Logger.Error("App", "致命异常", args.ExceptionObject as Exception);

        // 1. 单实例：Mutex "Local\TokenMonitor.SingleInstance"；已有实例 → 置位显示信号后退出
        _singleInstanceMutex = new Mutex(true, @"Local\TokenMonitor.SingleInstance", out var isNew);
        if (!isNew)
        {
            try
            {
                using var sig = EventWaitHandle.OpenExisting(@"Local\TokenMonitor.ShowPanel");
                sig.Set();
            }
            catch { /* 信号量异常时直接退出 */ }
            Shutdown(0);
            return;
        }

        _engine = new Core.MonitorEngine();
        var services = new AppServices(_engine);

        _showPanelSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\TokenMonitor.ShowPanel");

        StartEngineAndUi(services);
    }

    private async void StartEngineAndUi(AppServices services)
    {
        try
        {
            // 皮肤：在创建任何窗口前应用持久化 active_skin（避免默认肤闪现）
            services.Theme.ApplySkinOnStartup(services.UiState.Skin);

            // 视图模型（订阅事件总线）
            services.CreateViewModels();
            services.Main!.Initialize();

            // 窗口与托盘
            var panel = new MainWindow();
            var ball = new FloatingBall();
            services.PanelWindow = panel;
            services.BallWindow = ball;

            var tray = new Tray.TrayController(services);
            services.Tray = tray;
            tray.Initialize(new Tray.TrayMenuCallbacks(
                OpenPanel: () => Dispatcher.Invoke(services.ShowPanel),
                OpenConfigFile: () => Dispatcher.Invoke(() => services.Main!.OpenConfigFileCommand.Execute(null)),
                MultiplierConfig: () => Dispatcher.Invoke(() => services.Dialogs.ShowMultiplierConfig()),
                PricingConfig: () => Dispatcher.Invoke(() => services.Dialogs.ShowPricingConfig()),
                TimezoneSelected: offset => Dispatcher.Invoke(() =>
                {
                    try { _engine!.SetTimezone(offset); }
                    catch (Exception ex) { Core.SysUtil.Logger.Warn("App", "tz switch failed: " + ex.Message); }
                    AppServices.Instance.Tray?.RefreshChecks();
                }),
                ToggleEffectiveMode: () => Dispatcher.Invoke(() =>
                {
                    var mode = _engine!.Config.Settings.EffectiveDateMode == "utc" ? "local" : "utc";
                    _engine.SetEffectiveDateMode(mode);
                    AppServices.Instance.Tray?.RefreshChecks();
                }),
                ExportToday: () => Dispatcher.Invoke(() => RunExport("today")),
                ExportMonth: () => Dispatcher.Invoke(() => RunExport("month")),
                ExportRecent7Days: () => Dispatcher.Invoke(() => RunExport("7d")),
                OpenExportDir: () => Dispatcher.Invoke(OpenExportDir),
                ManualCalibrate: () => Dispatcher.Invoke(() => services.Dialogs.ShowManualCalibrate()),
                RollbackLastCalibration: () => Dispatcher.Invoke(RollbackCalibration),
                ReloadConfig: () => Dispatcher.Invoke(() =>
                {
                    try { _engine!.ReloadProxyConfig(); }
                    catch (Exception ex) { Core.SysUtil.Logger.Warn("App", "reload failed: " + ex.Message); }
                }),
                ToggleAutoStart: () => Dispatcher.Invoke(ToggleAutoStart),
                BallOpacitySelected: pct => Dispatcher.Invoke(() => SetBallOpacity(pct)),
                ToggleBallTopmost: () => Dispatcher.Invoke(ToggleBallTopmost),
                ToggleBallShape: () => Dispatcher.Invoke(() => AppServices.Instance.BallWindow?.ToggleShape()),
                ImportLegacy: () => Dispatcher.Invoke(() => services.Dialogs.ShowImportLegacy()),
                Exit: () => Dispatcher.Invoke(ExitApp)));
            tray.RefreshChecks();

            // 引擎启动（§7.1 的 8-11 步骤）
            await _engine!.StartAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token);

            // 12. UI 显示：按 ui_state.panel.collapsed 决定显示面板或仅悬浮球
            Dispatcher.Invoke(() =>
            {
                if (services.UiState.StartCollapsed)
                {
                    panel.Opacity = services.Main.PanelOpacity;
                    ball.Show();
                }
                else
                {
                    panel.Show();
                    panel.Opacity = services.Main.PanelOpacity;
                    panel.Topmost = services.Main.IsTopmost;
                    panel.Dispatcher.BeginInvoke(panel.PlayCardEntrance, DispatcherPriority.ApplicationIdle);
                }
                tray.SetStatus(_engine.Proxy.IsListening, _engine.Proxy.ListenAddr, null);
            });

            // 二次实例唤起守听
            _signalWatcher = new Thread(() =>
            {
                while (_showPanelSignal!.WaitOne())
                {
                    if (_stopped) return;
                    Dispatcher.BeginInvoke(services.ShowPanel);
                }
            })
            { IsBackground = true, Name = "ShowPanelSignal" };
            _signalWatcher.Start();
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Error("App", "启动失败", ex);
            MessageBox.Show("Token Monitor 启动失败：" + ex.Message, "Token Monitor",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    // ================= 托盘动作 =================

    private bool _stopped;

    private void RunExport(string kind)
    {
        var today = DateTime.Today;
        (DateTime s, DateTime e) = kind switch
        {
            "today" => (today, today),
            "month" => (new DateTime(today.Year, today.Month, 1), today),
            _ => (today.AddDays(-6), today),
        };
        var includeHourly = kind == "today";
        var req = new Core.Storage.ExportRequest(s.ToString("yyyy-MM-dd"), e.ToString("yyyy-MM-dd"),
            Core.Pricing.BucketScope.Local, null, includeHourly);
        var services = AppServices.Instance;
        Task.Run(() =>
        {
            try
            {
                var res = _engine!.Exporter.ExportXlsx(req);
                Dispatcher.BeginInvoke(() =>
                {
                    services.Tray?.ShowBalloon(res.Success ? "导出完成" : "导出失败",
                        res.Success ? res.FilePath : res.Error ?? "未知错误");
                });
            }
            catch (Exception ex)
            {
                Core.SysUtil.Logger.Warn("App", "tray export failed: " + ex.Message);
                Dispatcher.BeginInvoke(() =>
                    AppServices.Instance.Tray?.ShowBalloon("导出失败", ex.Message));
            }
        });
    }

    private void OpenExportDir()
    {
        var dir = System.IO.Path.Combine(_engine!.Config.DataDir, "export");
        System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
    }

    private void RollbackCalibration()
    {
        var r = MessageBox.Show("确认回滚到最近一次校准备份？", "校准回滚", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        try
        {
            _engine!.Calibration.RollbackLastCalibration();
            AppServices.Instance.Tray?.ShowBalloon("校准回滚", "已回滚到最近备份");
        }
        catch (Exception ex)
        {
            MessageBox.Show("回滚失败：" + ex.Message, "校准回滚", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        AppServices.Instance.Tray?.SetRollbackEnabled(SafeRollbackState());
    }

    private bool SafeRollbackState()
    {
        try { return _engine!.Calibration.State.HasCalibrated; }
        catch { return false; }
    }

    private void ToggleAutoStart()
    {
        try
        {
            var enable = !IsAutoStartOn();
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enable)
                key.SetValue("TokenMonitor", $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue("TokenMonitor", false);
            AppServices.Instance.Tray?.RefreshChecks();
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Warn("App", "autostart toggle failed: " + ex.Message);
            AppServices.Instance.Tray?.ShowBalloon("开机自启",
                "设置失败：" + ex.Message + "（注册表写入被拒绝，请以管理员运行一次后重试）");
        }
    }

    private static bool IsAutoStartOn()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("TokenMonitor") is not null;
    }

    private void SetBallOpacity(int pct)
    {
        // settings.ball_opacity 0-255（≤0 视为 255，§3.3）
        var b = Math.Clamp((int)Math.Round(pct / 100.0 * 255), 0, 255);
        var s = _engine!.Config.Settings with { BallOpacity = b };
        _engine.Config.SaveSettings(s);
        if (AppServices.Instance.BallWindow is { } ball)
            ball.Opacity = b <= 0 ? 1.0 : Math.Clamp(b / 255.0, 0.2, 1.0);
        AppServices.Instance.Tray?.RefreshChecks();
    }

    private void ToggleBallTopmost()
    {
        var s = _engine!.Config.Settings with { BallTopmost = !_engine.Config.Settings.BallTopmost };
        _engine.Config.SaveSettings(s);
        if (AppServices.Instance.BallWindow is { } ball) ball.Topmost = s.BallTopmost;
        AppServices.Instance.Tray?.RefreshChecks();
    }

    private void ExitApp()
    {
        _stopped = true;
        Shutdown(0);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try
        {
            // §7.2：保存 ui_state（强制落盘）→ 引擎 StopAsync（停代理/Flush/Checkpoint）→ 托盘 Dispose
            AppServices.Instance?.SaveUiNow();
            if (_engine is not null) await _engine.StopAsync();
            AppServices.Instance?.Tray?.Dispose();
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Error("App", "退出清理异常", ex);
        }
        base.OnExit(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}
