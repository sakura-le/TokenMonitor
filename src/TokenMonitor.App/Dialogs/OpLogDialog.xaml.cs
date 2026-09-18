using System.Collections.ObjectModel;
using System.Windows.Controls;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.Services;

namespace TokenMonitor.App.Dialogs;

/// <summary>操作日志行 VM。</summary>
public sealed record OpLogRowVm(string TimeText, string Action, string Model, string Detail);

/// <summary>
/// 对话框 4：操作日志（01-§3-D8）：范围切换（按模型/全部）+ 列表（时间/动作/模型/详情）。
/// </summary>
public partial class OpLogDialog : ShellDialog
{
    private readonly AppServices _svc;
    public ObservableCollection<OpLogRowVm> Rows { get; } = new();

    private string? _chosenModel;
    public string? ChosenModel { get => _chosenModel; set { _chosenModel = value; Load(); } }
    public string ScopeText => _chosenModel ?? "全部";

    public OpLogDialog(AppServices svc)
    {
        _svc = svc;
        Loaded += (_, _) => Load();
        OnConfirm = () => true;
    }

    private void Scope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ScopeBox.SelectedItem is ComboBoxItem { Tag: "selected" })
            ChosenModel = _svc.Main?.SelectedCard?.Key;
        else
            ChosenModel = null;
    }

    private void Load()
    {
        Rows.Clear();
        try
        {
            var modelParam = string.IsNullOrEmpty(ChosenModel) ? null : ChosenModel;
            foreach (var r in _svc.Engine.Queries.GetOpLogs(modelParam))
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(r.Ts).LocalDateTime;
                Rows.Add(new OpLogRowVm(Fmt.Clock(time) + " " + Fmt.Day(time), r.Action, r.Model, r.Detail));
            }
            Raise(nameof(ScopeText));
        }
        catch (Exception ex)
        {
            Rows.Add(new OpLogRowVm("", "error", "", "读取失败：" + ex.Message));
        }
    }
}
