using System.Windows;
using TokenMonitor.App.ViewModels;
using TokenMonitor.Core;
using TokenMonitor.Core.Config;

namespace TokenMonitor.App.Services;

/// <summary>
/// UI 状态持久化编排（01-§1.2 App.Services；文件 ui_state.json 经 IConfigService 契约）。
/// 汇聚：窗口矩形/透明度/收起态、卡片顺序/隐藏/每卡口径/倍率开关、球位置、active_skin。
/// </summary>
public sealed class UiStateService
{
    private readonly IConfigService _config;
    private string _skin = ThemeService.DefaultSkin;

    public UiStateService(IConfigService config)
    {
        _config = config;
        _skin = config.Ui?.ActiveSkin is { Length: > 0 } s ? s : ThemeService.DefaultSkin;
        ThemeService.Instance.PersistCallback = key =>
        {
            _skin = key;
            Save(null, null, null);
        };
    }

    public string Skin => _skin;

    /// <summary>保存（传 null 的部分沿用现有值）。Core 侧 300ms 防抖合并。</summary>
    public void Save(MainViewModel? main, FloatingBall? ball, MainWindow? panel)
    {
        try
        {
            var ui = _config.Ui;
            var order = main?.Cards.Select(c => c.Key).ToList()
                        ?? ui?.CardOrder?.ToList() ?? new List<string>();
            var hidden = main?.Cards.Where(c => c.IsHidden).Select(c => c.Key).ToHashSet()
                         ?? ui?.HiddenCards?.ToHashSet() ?? new HashSet<string>();
            var scope = main?.Cards.ToDictionary(c => c.Key, c => c.IsUtc ? "utc" : "local")
                        ?? ui?.CardScope?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, string>();
            var mulView = main?.Cards.ToDictionary(c => c.Key, c => c.MultiplierView)
                          ?? ui?.CardMultiplierView?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, bool>();

            PanelWindowState? panelState;
            if (panel is not null)
            {
                panelState = new PanelWindowState(
                    panel.Left, panel.Top, panel.ActualWidth, panel.ActualHeight,
                    panel.Opacity, !panel.IsVisible);
            }
            else panelState = ui?.Panel;

            var ballState = ball is not null
                ? new BallState(ball.Left, ball.Top)
                : ui?.Ball;

            _config.SaveUiState(new UiState(
                panelState, order, hidden, scope, mulView, ballState, _skin));
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Warn("App", "SaveUiState failed: " + ex.Message);
        }
    }

    /// <summary>窗口位置变更（拖拽/缩放结束）时保存。</summary>
    public void SavePanel(MainWindow panel, MainViewModel? main) => Save(main, null, panel);

    /// <summary>球位置变更时保存。</summary>
    public void SaveBall(FloatingBall ball, MainViewModel? main) => Save(main, ball, null);

    /// <summary>强制落盘（防抖队列清空由 Core 退出序列处理；此处再保存一次确保最新）。</summary>
    public void Flush(MainViewModel? main, FloatingBall? ball, MainWindow? panel) => Save(main, ball, panel);

    /// <summary>还原面板窗口矩形（越界回调到主屏工作区，03-ui-spec §3.2-7）。</summary>
    public void RestorePanel(MainWindow win)
    {
        var p = _config.Ui?.Panel;
        var wa = SystemParameters.WorkArea;
        if (p is not null)
        {
            if (p.Width is > 200 && p.Height is > 200)
            {
                win.Width = Math.Clamp(p.Width.Value, 720, Math.Max(760, wa.Width));
                win.Height = Math.Clamp(p.Height.Value, 520, Math.Max(560, wa.Height));
            }
            win.Left = p.Left ?? (wa.Width - win.Width) / 2;
            win.Top = p.Top ?? (wa.Height - win.Height) / 2;
            var op = Math.Clamp(p.Opacity <= 0 ? 1.0 : p.Opacity, 0.3, 1.0);
            win.Opacity = Math.Max(op, ThemeService.Instance.MinOpacity);
        }
        else
        {
            win.Left = (wa.Width - win.Width) / 2;
            win.Top = (wa.Height - win.Height) / 2;
        }
        // 越界回调：确保至少 100px 可见
        win.Left = Math.Clamp(win.Left, -win.Width + 120, Math.Max(wa.Width - 120, 0));
        win.Top = Math.Clamp(win.Top, 0, Math.Max(wa.Height - 60, 0));
    }

    /// <summary>面板是否以收起（仅球）状态启动。</summary>
    public bool StartCollapsed => _config.Ui?.Panel?.Collapsed ?? false;

    /// <summary>还原球位置。</summary>
    public void RestoreBall(FloatingBall ball)
    {
        var b = _config.Ui?.Ball;
        var wa = SystemParameters.WorkArea;
        ball.Left = Math.Clamp(b?.Left ?? wa.Right - 96, 0, Math.Max(wa.Width - 80, 0));
        ball.Top = Math.Clamp(b?.Top ?? wa.Bottom - 160, 0, Math.Max(wa.Height - 80, 0));
    }
}
