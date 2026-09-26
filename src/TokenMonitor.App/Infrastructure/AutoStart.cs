using Microsoft.Win32;
using TokenMonitor.App.Services;

namespace TokenMonitor.App.Infrastructure;

/// <summary>开机自启（HKCU\…\Run，App.xaml.cs 与设置窗口共用；失败不抛，返回是否成功）。</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TokenMonitor";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is not null;
        }
        catch { return false; }
    }

    public static bool Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else key.DeleteValue(ValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            Core.SysUtil.Logger.Warn("App", "autostart toggle failed: " + ex.Message);
            AppServices.Instance.Tray?.ShowBalloon("开机自启",
                "设置失败：" + ex.Message + "（注册表写入被拒绝，请以管理员运行一次后重试）");
            return false;
        }
    }
}
