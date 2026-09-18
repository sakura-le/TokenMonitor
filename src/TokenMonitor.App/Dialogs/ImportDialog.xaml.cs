using System.Globalization;
using System.Windows;
using Microsoft.Win32;
using TokenMonitor.App.Services;
using TokenMonitor.Core.Storage;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// G1 导入旧数据向导（轻量版）：目录 → Preview → 用户确认 → Import（后台线程）。
/// </summary>
public partial class ImportDialog : ShellDialog
{
    private readonly AppServices _svc;

    private string _dirText = "";
    private bool _migrateKeys;
    private bool _replaceAll;
    private string? _previewText;
    private bool _busy;
    private ImportPreview? _lastPreview;

    public string DirText { get => _dirText; set { Set(ref _dirText, value, nameof(DirText)); _lastPreview = null; } }
    public bool MigrateKeys { get => _migrateKeys; set => Set(ref _migrateKeys, value, nameof(MigrateKeys)); }
    public bool ReplaceAll { get => _replaceAll; set => Set(ref _replaceAll, value, nameof(ReplaceAll)); }
    public string? PreviewText { get => _previewText; set => Set(ref _previewText, value, nameof(PreviewText)); }

    public ImportDialog(AppServices svc)
    {
        _svc = svc;
        var exe = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        DirText = System.IO.Path.Combine(exe, "legacy_data");
        OnConfirm = Run;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择旧项目 token_monitor.db",
            Filter = "SQLite 数据库|*.db|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
            DirText = System.IO.Path.GetDirectoryName(dlg.FileName) ?? DirText;
    }

    private void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        PreviewText = "正在扫描…";
        var opts = MakeOptions();
        Task.Run(() =>
        {
            try
            {
                var p = _svc.Engine.Import.Preview(opts);
                Dispatcher.BeginInvoke(() =>
                {
                    _lastPreview = p;
                    PreviewText =
                        $"旧库：{(p.LegacyDbFound ? "已找到" : "未找到")} · 用量行≈{p.EstimatedUsageRows:N0} · 漏抓≈{p.EstimatedMissedRows:N0} · 操作日志≈{p.EstimatedOpRows:N0}\n" +
                        $"config: {(p.ConfigFound ? "有" : "无")} · pricing: {(p.PricingFound ? "有" : "无")} · settings: {(p.SettingsFound ? "有" : "无")} · 倍率状态: {(p.MultiplierStatesFound ? "有" : "无")}\n" +
                        (p.AlreadyImported ? "⚠ 该旧库此前已导入过（替换模式可重新导入）\n" : "") +
                        string.Join("\n", p.Warnings.Take(4));
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => PreviewText = "扫描失败：" + ex.Message);
            }
        });
    }

    private ImportOptions MakeOptions() =>
        new(DirText.Trim(), MigrateKeys, ReplaceAll ? ImportMode.ReplaceAll : ImportMode.MergeIfNew);

    private bool Run()
    {
        if (_busy) return false;
        if (_lastPreview is null)
        {
            MessageBox.Show(this, "请先「扫描预览」确认后导入。", "导入旧数据", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (!System.IO.Directory.Exists(DirText.Trim()))
        {
            MessageBox.Show(this, "目录不存在。", "导入旧数据", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        var r = MessageBox.Show(this,
            $"即将从 {DirText} 导入（{(ReplaceAll ? "替换模式：清空现有数据" : "合并模式")}）。\n导入前将自动备份新库。继续？",
            "导入旧数据", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r != MessageBoxResult.OK) return false;

        _busy = true;
        var opts = MakeOptions();
        PreviewText = "正在导入…";
        Task.Run(() =>
        {
            try
            {
                var res = _svc.Engine.Import.Import(opts);
                Dispatcher.BeginInvoke(() =>
                {
                    if (res.Success)
                    {
                        Core.SysUtil.Logger.Info("App", $"import legacy ok: usage={res.ImportedUsageRows} backup={res.BackupPath}");
                        _svc.Tray?.ShowBalloon("导入旧数据", $"导入完成：用量 {res.ImportedUsageRows} 行");
                        MessageBox.Show(this, $"导入完成：\n用量 {res.ImportedUsageRows} 行 · 漏抓 {res.ImportedMissedRows} 行 · 操作日志 {res.ImportedOpRows} 行\n备份：{res.BackupPath}",
                            "导入旧数据", MessageBoxButton.OK, MessageBoxImage.Information);
                        DialogResult = true;
                        Close();
                    }
                    else
                    {
                        _busy = false;
                        PreviewText = "导入失败：" + res.Error;
                    }
                });
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    _busy = false;
                    PreviewText = "导入失败：" + ex.Message;
                });
            }
        });
        return false;
    }
}
