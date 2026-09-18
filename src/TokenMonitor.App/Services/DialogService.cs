using TokenMonitor.App.Dialogs;
using System.Windows;
using TokenMonitor.App.ViewModels;

namespace TokenMonitor.App.Services;

/// <summary>
/// 对话框编排（C-13 ×7 + 导入向导）：创建 → 遮罩 → ShowDialog(owner=主面板) → 关遮罩。
/// </summary>
public sealed class DialogService
{
    private readonly AppServices _svc;

    public DialogService(AppServices svc) => _svc = svc;

    private void Show(Window dlg)
    {
        var owner = _svc.PanelWindow;
        OverlayWindow? overlay = null;
        try
        {
            if (owner is { IsVisible: true })
            {
                overlay = new OverlayWindow(owner);
                overlay.Show();
            }
            if (owner is not null && owner.IsVisible)
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
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

    public void ShowMultiplierConfig(string? modelKey = null)
    {
        var dlg = new MultiplierConfigDialog(_svc) { ChosenModel = InitialModel(modelKey) ?? "" };
        Show(dlg);
    }

    public void ShowPricingConfig(string? modelKey = null)
    {
        var dlg = new PricingConfigDialog(_svc) { ChosenModel = InitialModel(modelKey) ?? "" };
        Show(dlg);
    }

    public void ShowManualCalibrate(string? modelKey = null)
    {
        var dlg = new ManualCalibrateDialog(_svc) { ChosenModel = modelKey ?? "" };
        Show(dlg);
    }

    public void ShowOpLogs(string? modelKey = null)
    {
        var dlg = new OpLogDialog(_svc) { ChosenModel = modelKey };
        Show(dlg);
    }

    public void ShowExport() => Show(new ExportDialog(_svc));

    /// <summary>时间范围（数据范围 → 自定义日期…；结果写回卡片并触发范围查询 [C16]）。</summary>
    public void ShowTimeRange(CardViewModel? card)
    {
        var dlg = new TimeRangeDialog(_svc)
        {
            Start = card?.CustomStart ?? DateTime.Today.AddDays(-6),
            End = card?.CustomEnd ?? DateTime.Today,
        };
        Show(dlg);
        if (dlg.DialogResult == true && card is not null)
            card.SetRange(CardRange.Custom, dlg.Start, dlg.End);
    }

    public void ShowCardVisibility() => Show(new CardVisibilityDialog(_svc));

    public void ShowImportLegacy() => Show(new ImportDialog(_svc));
}
