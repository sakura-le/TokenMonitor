using TokenMonitor.App.Dialogs;
using System.Windows;
using System.Windows.Threading;
using TokenMonitor.App.ViewModels;

namespace TokenMonitor.App.Services;

/// <summary>
/// 对话框编排（C-13 ×7 + 导入向导）。
/// 可靠性规则（修复"托盘点击无反应/模态卡死"）：
/// ① 对话框构造纳入 try（构造失败 → 可见报错而非被全局钩子静默吞掉）；
/// ② 打开前关闭托盘/球菜单并排空 Background 队列（规避 Popup→ShowDialog 激活死锁）；
/// ③ 模态期间对话框 Topmost（保证压过置顶面板/遮罩/悬浮球，杜绝"模态在顶层元素后面假卡死"）；
/// ④ 面板不可见时 Owner 兜底为屏幕居中。
/// </summary>
public sealed class DialogService
{
    private readonly AppServices _svc;

    public DialogService(AppServices svc) => _svc = svc;

    private void Show(Func<Window> create)
    {
        Window dlg;
        try
        {
            dlg = create();
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Error("App", "对话框构造失败", ex);
            MessageBox.Show("窗口初始化失败：" + ex.Message, "Token Monitor",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ShowCore(dlg);
    }

    /// <summary>模态宿主（创建之后、ShowDialog 的全部可靠性处理）。</summary>
    private void ShowCore(Window dlg)
    {
        OverlayWindow? overlay = null;
        try
        {
            // 菜单关闭屏障：ContextPopup 关闭/鼠标捕获释放的排队工作先走完，再进入模态循环
            var menu = _svc.Tray?.Menu;
            if (menu is { IsOpen: true }) menu.IsOpen = false;
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

            var owner = _svc.PanelWindow;
            if (owner is { IsVisible: true })
            {
                overlay = new OverlayWindow(owner);
                overlay.Show();
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
            // 面板/球/遮罩均可为 Topmost；模态对话框必须压过它们，否则"看不见的模态"卡死整程序
            dlg.Topmost = true;
            dlg.ShowDialog();
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Error("App", "dialog failed", ex);
            MessageBox.Show("操作失败：" + ex.Message, "Token Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            overlay?.Close();
        }
    }

    private string? InitialModel(string? modelKey)
    {
        if (string.IsNullOrEmpty(modelKey)) return null;
        var i = modelKey.IndexOf('/');
        return i < 0 ? modelKey : modelKey[(i + 1)..];   // pricing.json 键 = 不带 provider 前缀
    }

    public void ShowMultiplierConfig(string? modelKey = null) =>
        Show(() => new MultiplierConfigDialog(_svc) { ChosenModel = InitialModel(modelKey) ?? "" });

    public void ShowPricingConfig(string? modelKey = null) =>
        Show(() => new PricingConfigDialog(_svc) { ChosenModel = InitialModel(modelKey) ?? "" });

    public void ShowManualCalibrate(string? modelKey = null) =>
        Show(() => new ManualCalibrateDialog(_svc) { ChosenModel = modelKey ?? "" });

    public void ShowOpLogs(string? modelKey = null) =>
        Show(() => new OpLogDialog(_svc) { ChosenModel = modelKey });

    public void ShowExport() => Show(() => new ExportDialog(_svc));

    /// <summary>时间范围（数据范围 → 自定义日期…；结果写回卡片并触发范围查询 [C16]）。</summary>
    public void ShowTimeRange(CardViewModel? card)
    {
        var dlg = new TimeRangeDialog(_svc)
        {
            Start = card?.CustomStart ?? DateTime.Today.AddDays(-6),
            End = card?.CustomEnd ?? DateTime.Today,
        };
        ShowCore(dlg);
        if (dlg.DialogResult == true && card is not null)
            card.SetRange(CardRange.Custom, dlg.Start, dlg.End);
    }

    public void ShowCardVisibility() => Show(() => new CardVisibilityDialog(_svc));

    public void ShowImportLegacy() => Show(() => new ImportDialog(_svc));
}
