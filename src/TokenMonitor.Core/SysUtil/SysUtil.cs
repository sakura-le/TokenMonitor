using Microsoft.Win32;

using TokenMonitor.Core.SysUtil;

namespace TokenMonitor.Core.SysUtil;

/// <summary>Windows 系统能力门面（01-§2.3.8）。静态能力 + 可替换实现双形态，便于测试替身。</summary>
public interface ISysUtil : IDisposable
{
    string DataDir { get; }

    /// <summary>单实例：Mutex "Local\TokenMonitor.SingleInstance"。已有实例 → false（并已置位显示信号）。</summary>
    bool TryAcquireSingleInstance(out EventWaitHandle showPanelSignal);

    /// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run，值名 "TokenMonitor"（02-§7 生命周期 E2）。</summary>
    void SetAutoStart(bool enable, string exePath);

    bool IsAutoStartEnabled();

    /// <summary>UTC-12:00..UTC+14:00 步长 30 分钟（53 项）。</summary>
    IReadOnlyList<int> TimezoneOffsets();

    /// <summary>"UTC±0" / "UTC+8" / "UTC+5:30"。</summary>
    string OffsetLabel(int offsetMin);

    void OpenInExplorer(string path);

    /// <summary>默认编辑器打开文本文件（配置文件）。</summary>
    void OpenTextFile(string path);
}

/// <summary>ISysUtil 默认实现（Win32/注册表）。</summary>
public sealed class SysUtil : ISysUtil
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartValueName = "TokenMonitor";
    private const string MutexName = @"Local\TokenMonitor.SingleInstance";
    private const string ShowPanelSignalName = @"Local\TokenMonitor.ShowPanel";

    private Mutex? _mutex;
    private EventWaitHandle? _showPanelSignal;
    private readonly string _mutexName;
    private readonly string _signalName;

    public string DataDir { get; }

    public SysUtil(string? dataDir = null)
    {
        _mutexName = MutexName;
        _signalName = ShowPanelSignalName;
        // exe 旁 data/；失败回退当前目录\data（原 sysutil.DataDir 语义）
        if (dataDir is not null)
        {
            DataDir = dataDir;
            return;
        }
        try
        {
            // AppContext.BaseDirectory：常规与单文件（self-contained single-file）发布下都指向 exe 所在目录；
            // Assembly.Location 在单文件发布返回空串（此前会退回 CurrentDirectory\data，取决于启动方式，不可靠）
            var dir = AppContext.BaseDirectory;
            if (string.IsNullOrWhiteSpace(dir)) dir = Environment.CurrentDirectory;
            DataDir = Path.Combine(dir, "data");
        }
        catch
        {
            DataDir = Path.Combine(Environment.CurrentDirectory, "data");
        }
    }

    /// <summary>测试构造：注入独立 Mutex/信号名（避免与本机在跑的正式实例互斥）。</summary>
    internal SysUtil(string? dataDir, string mutexName, string signalName)
    {
        _mutexName = mutexName;
        _signalName = signalName;
        DataDir = dataDir ?? Path.Combine(Environment.CurrentDirectory, "data");
    }

    public bool TryAcquireSingleInstance(out EventWaitHandle showPanelSignal)
    {
        _showPanelSignal = new EventWaitHandle(false, EventResetMode.AutoReset, _signalName);
        showPanelSignal = _showPanelSignal;
        Mutex? created;
        try
        {
            created = new Mutex(true, _mutexName, out var isNew);
        }
        catch (Exception ex)
        {
            Logger.Error("SysUtil", "创建单实例 Mutex 失败", ex);
            return false;
        }
        if (!created.WaitOne(TimeSpan.Zero))
        {
            // 已有实例 → 置位显示信号后返回 false
            try { _showPanelSignal.Set(); } catch { /* 信号失败不影响结论 */ }
            created.Dispose();
            return false;
        }
        _mutex = created;
        return true;
    }

    public void SetAutoStart(bool enable, string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                Logger.Warn("SysUtil", "打开注册表 Run 键失败");
                return;
            }
            if (enable) key.SetValue(AutoStartValueName, exePath, RegistryValueKind.String);
            else key.DeleteValue(AutoStartValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Logger.Error("SysUtil", "设置开机自启失败", ex);
        }
    }

    public bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(AutoStartValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public IReadOnlyList<int> TimezoneOffsets()
    {
        // -12h..+14h 步长 30 分钟 = 53 项（支持半小时时区，与原 TimezoneOffsets 一致）
        var list = new List<int>(53);
        for (var m = -12 * 60; m <= 14 * 60; m += 30) list.Add(m);
        return list;
    }

    public string OffsetLabel(int offsetMin)
    {
        if (offsetMin == 0) return "UTC±0";
        var sign = "+";
        if (offsetMin < 0) { sign = "-"; offsetMin = -offsetMin; }
        var h = offsetMin / 60;
        var m = offsetMin % 60;
        return m == 0 ? $"UTC{sign}{h}" : $"UTC{sign}{h}:{m:D2}";
    }

    public void OpenInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", path);
        }
        catch (Exception ex)
        {
            Logger.Error("SysUtil", $"打开资源管理器失败: {path}", ex);
        }
    }

    public void OpenTextFile(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error("SysUtil", $"打开文件失败: {path}", ex);
        }
    }

    public void Dispose()
    {
        try { _mutex?.ReleaseMutex(); } catch { /* 非 owning 线程释放会抛，忽略 */ }
        _mutex?.Dispose();
        _mutex = null;
    }
}
