using TokenMonitor.App.Dialogs;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
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
        Core.SysUtil.Logger.Info("Dialog", "请求打开对话框");
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

    /// <summary>模态宿主：遮罩（非置顶）→ 关闭菜单/释放捕获 → ShowDialog（Owner 归属，不用 Topmost）。</summary>
    private void ShowCore(Window dlg, Action<Window>? onClosed = null)
    {
        try
        {
            var owner = _svc.PanelWindow;
            CloseOpenMenus();
            try { Mouse.Capture(null); } catch { }

            // 最终配置：Topmost=true 让对话框进入置顶带（压过置顶面板/悬浮球，点击才能落进来），
            // 但不设 Owner（Owner+Topmost+遮罩的组合实测会令 ShowDialog 永久挂起）、不建遮罩。
            dlg.WindowStartupLocation = owner is { IsVisible: true }
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen;
            dlg.Topmost = true;
            Core.SysUtil.Logger.Info("Dialog", "即将 ShowDialog (" + dlg.GetType().Name + ")");
            dlg.ShowDialog();
            Core.SysUtil.Logger.Info("Dialog", "ShowDialog 返回: " + dlg.GetType().Name);
            onClosed?.Invoke(dlg);
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Error("App", "dialog failed", ex);
            MessageBox.Show("操作失败：" + ex.Message, "Token Monitor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>关闭当前所有打开的弹出菜单（托盘/悬浮球/卡片设置）。</summary>
    private static void CloseOpenMenus()
    {
        try
        {
            if (Application.Current is null) return;
            foreach (Window w in Application.Current.Windows)
            {
                if (w.ContextMenu is { IsOpen: true } cm) cm.IsOpen = false;
            }
            // 菜单项自身所在的 ContextMenu（不在 Window 列表里）：从焦点元素回溯
            if (Keyboard.FocusedElement is DependencyObject fe)
            {
                var cur = fe;
                while (cur is not null)
                {
                    if (cur is System.Windows.Controls.ContextMenu cm2 && cm2.IsOpen) { cm2.IsOpen = false; break; }
                    cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                        ? VisualTreeHelper.GetParent(cur)
                        : LogicalTreeHelper.GetParent(cur);
                }
            }
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Warn("Dialog", "CloseOpenMenus: " + ex.Message);
        }
    }

    /// <summary>
    /// 配置对话框的预选模型键：pricing.json / 卡片都用完整键 "provider/model"（见 PricingDocument 注释），
    /// 此处必须原样透传——此前剥掉 provider 前缀会让保存写到 "deepseek-flash" 这类不存在的键上，
    /// 卡片按 "DeepSeek/deepseek-flash" 取值 → 表现为"保存了但不生效"。
    /// </summary>
    private static string? InitialModel(string? modelKey) => string.IsNullOrEmpty(modelKey) ? null : modelKey;

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
        ShowCore(dlg, w =>
        {
            if (w is Dialogs.ShellDialog { Confirmed: true } && card is not null)
                card.SetRange(CardRange.Custom, dlg.Start, dlg.End);
        });
    }

    public void ShowCardVisibility() => Show(() => new CardVisibilityDialog(_svc));

    public void ShowImportLegacy() => Show(() => new ImportDialog(_svc));

    /// <summary>设置窗口（代理/供应商/生效基准/时区/悬浮球/自启；替代直接编辑 config.json）。</summary>
    public void ShowSettings() => Show(() => new SettingsDialog(_svc));
}
