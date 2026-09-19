using System.Windows;
using System.Windows.Interop;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// 模态遮罩层（非置顶版）：铺在主面板矩形上方、对话框下方，仅作视觉分区。
/// 关键点：Topmost=false —— 置顶遮罩会与对话框争夺 z 序并吞掉对话框内点击（历史事故）；
/// 非置顶遮罩永远低于对话框（对话框 Owner=面板 且激活态），既保留视觉又零输入风险。
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
        IsHitTestVisible = false;
        ResizeMode = ResizeMode.NoResize;
        SetResourceReference(BackgroundProperty, "Tg.Mask");
        Topmost = false;
        Left = covered.Left;
        Top = covered.Top;
        Width = covered.ActualWidth;
        Height = covered.ActualHeight;
    }
}
