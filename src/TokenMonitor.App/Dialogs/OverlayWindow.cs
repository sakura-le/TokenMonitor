using System.Windows;
using System.Windows.Interop;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// 模态遮罩层（03-ui-spec §1.4）：单色半透明纯色，铺在主面板上方、对话框下方。
/// 覆盖区域 = 主面板矩形；透明度随主面板（S1 40% 墨 / S2 88% 底 / S3 55% 墨 / S4 50% 海军，经 Tg.Mask）。
/// </summary>
public sealed class OverlayWindow : Window
{
    public OverlayWindow(Window covered)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Tg.Mask");
        Topmost = true;
        Left = covered.Left;
        Top = covered.Top;
        Width = covered.ActualWidth;
        Height = covered.ActualHeight;
    }
}
