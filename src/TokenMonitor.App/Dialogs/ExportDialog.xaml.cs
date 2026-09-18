using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TokenMonitor.App.Services;
using TokenMonitor.Core.Pricing;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// 对话框 5：导出（01-§3-D8）：范围预设 + 自定义起止 + 模型范围 + 小时明细勾选。
/// 后台线程执行 IExportService.ExportXlsx；完成显示路径 + 打开导出目录。
/// </summary>
public partial class ExportDialog : ShellDialog
{
    private readonly AppServices _svc;

    private string _startText = DateTime.Today.ToString("yyyy-MM-dd");
    private string _endText = DateTime.Today.ToString("yyyy-MM-dd");
    private string? _statusText;
    private string? _errorText;
    private bool _includeHourly = true;
    private bool _busy;
    private string? _lastFile;

    public string StartText { get => _startText; set => Set(ref _startText, value, nameof(StartText)); }
    public string EndText { get => _endText; set => Set(ref _endText, value, nameof(EndText)); }
    public string? StatusText { get => _statusText; set => Set(ref _statusText, value, nameof(StatusText)); }
    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value, nameof(ErrorText)); }
    public bool IncludeHourly { get => _includeHourly; set => Set(ref _includeHourly, value, nameof(IncludeHourly)); }

    public ExportDialog(AppServices svc)
    {
        _svc = svc;
        PresetBox.Items.Add("今日");
        PresetBox.Items.Add("本月");
        PresetBox.Items.Add("近 7 日");
        PresetBox.Items.Add("本年");
        PresetBox.Items.Add("自定义");
        PresetBox.SelectedIndex = 0;

        ModelBox.Items.Add("全部模型");
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var (p, m) in _svc.Engine.Queries.GetAllModels()) names.Add(p + "/" + m); } catch { }
        if (_svc.Main is not null)
            foreach (var c in _svc.Main.Cards) names.Add(c.Key);
        foreach (var n in names) ModelBox.Items.Add(n);
        ModelBox.SelectedIndex = 0;

        OnConfirm = Run;
    }

    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedIndex < 0) return;
        var today = DateTime.Today;
        (DateTime, DateTime) range = PresetBox.SelectedIndex switch
        {
            0 => (today, today),
            1 => (new DateTime(today.Year, today.Month, 1), today),
            2 => (today.AddDays(-6), today),
            3 => (new DateTime(today.Year, 1, 1), today),
            _ => (today.AddDays(-6), today),
        };
        if (PresetBox.SelectedIndex != 4)
        {
            StartText = range.Item1.ToString("yyyy-MM-dd");
            EndText = range.Item2.ToString("yyyy-MM-dd");
        }
    }

    private bool Run()
    {
        if (_busy) return false;
        ErrorText = null;
        if (!DateOnly.TryParse(StartText, CultureInfo.InvariantCulture, out var start) ||
            !DateOnly.TryParse(EndText, CultureInfo.InvariantCulture, out var end))
        {
            ErrorText = "日期格式应为 YYYY-MM-DD";
            return false;
        }
        if (end < start)
        {
            ErrorText = "结束日期不能早于起始日期";
            return false;
        }
        var scope = ScopeBox.SelectedIndex == 1 ? BucketScope.Utc : BucketScope.Local;
        var modelKey = ModelBox.SelectedIndex > 0 ? ModelBox.SelectedItem as string : null;
        var req = new ExportRequest(start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"), scope, modelKey, IncludeHourly);

        _busy = true;
        StatusText = "正在导出（后台线程）…";
        Task.Run(() =>
        {
            try
            {
                var res = _svc.Engine.Exporter.ExportXlsx(req);
                Dispatcher.BeginInvoke(() =>
                {
                    _busy = false;
                    if (res.Success)
                    {
                        _lastFile = res.FilePath;
                        StatusText = $"导出完成：{res.FilePath}（UTC {res.UtcRows} 行 · LOCAL {res.LocalRows} 行{(IncludeHourly ? $" · HOUR {res.HourlyRows} 行" : "")}）";
                        var r = MessageBox.Show(this, $"导出完成：\n{res.FilePath}\n\n打开导出目录？",
                            "导出数据", MessageBoxButton.YesNo, MessageBoxImage.Information);
                        if (r == MessageBoxResult.Yes)
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                                System.IO.Path.GetDirectoryName(res.FilePath)!) { UseShellExecute = true });
                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        ErrorText = "导出失败：" + res.Error;
                        StatusText = null;
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _busy = false;
                    ErrorText = "导出失败：" + ex.Message;
                    StatusText = null;
                });
            }
        });
        return false;   // 后台完成后自行关闭
    }
}
