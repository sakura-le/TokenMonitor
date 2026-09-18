using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using TokenMonitor.App.Services;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// 对话框 3：手动补录（01-§3-D8）。
/// 模型选择 / 笔数 N / 命中·未命中·输出 token（total 自动合计只读）/
/// 提示自动备份与漏抓清理。确认 → ICalibrationService.ApplyManualCalibrate（后台线程）。
/// </summary>
public partial class ManualCalibrateDialog : ShellDialog
{
    private readonly AppServices _svc;
    public ObservableCollection<string> ModelKeys { get; } = new();

    private string _chosenModel = "";
    private string _countText = "1";
    private string _hitText = "0";
    private string _missText = "0";
    private string _outputText = "0";
    private string? _errorText;
    private bool _busy;

    public string ChosenModel { get => _chosenModel; set => Set(ref _chosenModel, value, nameof(ChosenModel)); }
    public string CountText { get => _countText; set { if (Set(ref _countText, value, nameof(CountText))) Raise(nameof(TotalText)); } }
    public string HitText { get => _hitText; set { if (Set(ref _hitText, value, nameof(HitText))) Raise(nameof(TotalText)); } }
    public string MissText { get => _missText; set { if (Set(ref _missText, value, nameof(MissText))) Raise(nameof(TotalText)); } }
    public string OutputText { get => _outputText; set { if (Set(ref _outputText, value, nameof(OutputText))) Raise(nameof(TotalText)); } }
    public string TotalText
    {
        get
        {
            var hit = ParseLong(HitText) ?? 0;
            var miss = ParseLong(MissText) ?? 0;
            var output = ParseLong(OutputText) ?? 0;
            return (hit + miss + output).ToString("N0");
        }
    }
    public string? ErrorText { get => _errorText; set => Set(ref _errorText, value, nameof(ErrorText)); }

    public ManualCalibrateDialog(AppServices svc)
    {
        _svc = svc;
        // 模型候选：历史出现过的模型（provider/model）
        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var (p, m) in _svc.Engine.Queries.GetAllModels()) keys.Add(p + "/" + m); } catch { /* 库不可用时给空 */ }
        if (_svc.Main is not null)
            foreach (var c in _svc.Main.Cards) keys.Add(c.Key);
        foreach (var k in keys) ModelKeys.Add(k);
        OnConfirm = Save;
    }

    private static long? ParseLong(string s) =>
        long.TryParse(s?.Trim().Replace(",", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : null;

    private bool Save()
    {
        if (_busy) return false;
        if (string.IsNullOrWhiteSpace(ChosenModel))
        {
            ErrorText = "请选择模型";
            return false;
        }
        var count = ParseLong(CountText) ?? 0;
        var hit = ParseLong(HitText) ?? 0;
        var miss = ParseLong(MissText) ?? 0;
        var output = ParseLong(OutputText) ?? 0;
        if (hit + miss + output <= 0)
        {
            ErrorText = "命中/未命中/输出之和必须大于 0";
            return false;
        }

        var i = ChosenModel.IndexOf('/');
        var provider = i < 0 ? "" : ChosenModel[..i];
        var model = i < 0 ? ChosenModel : ChosenModel[(i + 1)..];

        // 后台执行（备份+事务+重算可能耗时），完成经 Dispatcher 关闭 [§8.3]
        _busy = true;
        ConfirmEnabled = false;
        ErrorText = "正在补录（自动备份 → 估算行 → 清理漏抓 → 重算）…";
        var capturedModel = ChosenModel;
        Task.Run(() =>
        {
            try
            {
                var backup = _svc.Engine.Calibration.ApplyManualCalibrate(provider, model,
                    (int)Math.Max(1, count), hit, miss, output);
                Core.SysUtil.Logger.Info("App", $"manual calibrate: {capturedModel} n={count} backup={backup}");
                Dispatcher.BeginInvoke(() =>
                {
                    _svc.Tray?.ShowBalloon("手动补录", $"已补录 {capturedModel}，备份: {System.IO.Path.GetFileName(backup)}");
                    _svc.Tray?.SetRollbackEnabled(SafeState());
                    DialogResult = true;
                    Close();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ErrorText = "补录失败：" + ex.Message;
                    _busy = false;
                    ConfirmEnabled = true;
                    _svc.Tray?.SetRollbackEnabled(SafeState());
                });
            }
        });
        return false;   // 保持打开，后台完成后自行关闭
    }

    private bool SafeState()
    {
        try { return _svc.Engine.Calibration.State.HasCalibrated; }
        catch { return true; }
    }

    private bool _confirmEnabled = true;
    public bool ConfirmEnabled { get => _confirmEnabled; set => Set(ref _confirmEnabled, value, nameof(ConfirmEnabled)); }
}
