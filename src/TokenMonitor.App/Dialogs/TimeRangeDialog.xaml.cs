using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TokenMonitor.App.Services;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// 对话框 6：时间范围（01-§3-D8）：预设（本日/本周/近7日/本月/上月/本季度/本年）+ 自定义。
/// DialogResult=true 后由 DialogService 把 Start/End 写回卡片并触发范围查询 [C16]。
/// </summary>
public partial class TimeRangeDialog : ShellDialog
{
    private readonly AppServices _svc;

    private string _startText = "";
    private string _endText = "";
    public string StartText { get => _startText; set => Set(ref _startText, value, nameof(StartText)); }
    public string EndText { get => _endText; set => Set(ref _endText, value, nameof(EndText)); }

    public DateTime Start { get; set; } = DateTime.Today.AddDays(-6);
    public DateTime End { get; set; } = DateTime.Today;

    public TimeRangeDialog(AppServices svc)
    {
        _svc = svc;
        OnConfirm = ConfirmRange;
    }

    private void Preset_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string idx } || !IsLoaded) return;
        var today = DateTime.Today;
        (Start, End) = int.Parse(idx) switch
        {
            0 => (today, today),
            1 => (today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today),
            2 => (today.AddDays(-6), today),
            3 => (new DateTime(today.Year, today.Month, 1), today),
            4 => (new DateTime(today.Year, today.Month, 1).AddMonths(-1), new DateTime(today.Year, today.Month, 1).AddDays(-1)),
            5 => (new DateTime(today.Year, (today.Month - 1) / 3 * 3 + 1, 1), today),
            6 => (new DateTime(today.Year, 1, 1), today),
            _ => (today.AddDays(-6), today),
        };
    }

    private bool ConfirmRange()
    {
        if (!string.IsNullOrWhiteSpace(StartText) && !string.IsNullOrWhiteSpace(EndText))
        {
            if (!DateOnly.TryParse(StartText, CultureInfo.InvariantCulture, out var s) ||
                !DateOnly.TryParse(EndText, CultureInfo.InvariantCulture, out var e2))
            {
                MessageBox.Show("日期格式应为 YYYY-MM-DD", "时间范围", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            if (e2 < s)
            {
                MessageBox.Show("结束日期不能早于起始日期", "时间范围", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            Start = s.ToDateTime(TimeOnly.MinValue);
            End = e2.ToDateTime(TimeOnly.MinValue);
        }
        return true;
    }
}
