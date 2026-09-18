using System.Windows;
using TokenMonitor.App.ViewModels;
using TokenMonitor.Core;

namespace TokenMonitor.App.Services;

/// <summary>
/// App 层服务装配点（组合根；App 只经此访问 Engine 与各服务）。
/// </summary>
public sealed class AppServices
{
    public static AppServices Instance { get; private set; } = null!;

    public ITokenMonitorEngine Engine { get; }
    public ThemeService Theme { get; } = ThemeService.Instance;
    public UiStateService UiState { get; }
    public DialogService Dialogs { get; }
    public SurgeDetector Surge { get; }

    public MainViewModel? Main { get; private set; }
    public BallViewModel? Ball { get; private set; }

    public MainWindow? PanelWindow { get; set; }
    public FloatingBall? BallWindow { get; set; }
    public Tray.TrayController? Tray { get; set; }

    public AppServices(ITokenMonitorEngine engine)
    {
        Engine = engine;
        Instance = this;
        UiState = new UiStateService(engine.Config);
        Dialogs = new DialogService(this);
        Surge = new SurgeDetector();
    }

    public void CreateViewModels()
    {
        Main = new MainViewModel(this);
        Ball = new BallViewModel(this);
    }

    /// <summary>显示面板（托盘/球双击/二次实例唤起）。</summary>
    public void ShowPanel()
    {
        var win = PanelWindow;
        if (win is null) return;
        win.RestoreFromBall();
    }

    /// <summary>收起到球。</summary>
    public void CollapseToBall()
    {
        PanelWindow?.CollapseToBall();
        BallWindow?.ExpandFromPanel();
        SaveUiNow();
    }

    /// <summary>立即落盘 UI 状态（退出/收起时）。</summary>
    public void SaveUiNow() => UiState.Flush(Main, BallWindow, PanelWindow);
}
