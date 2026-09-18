using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// 对话框统一壳（03-ui-spec §1.4）：Header(标签+标题+模型 tag+✕) / 版本或提示条 /
/// Body(Content) / Footer(说明+取消+主按钮)；圆角 {Tg.Radius.Dialog}、遮罩色 {Tg.Mask}。
/// 模板见 Controls.Shared「Tg.DialogShell」；Content 为各对话框表单。
/// </summary>
public class DialogShell : Window
{
    public static readonly DependencyProperty HeaderTagProperty = DependencyProperty.Register(
        nameof(HeaderTag), typeof(string), typeof(DialogShell), new PropertyMetadata("CONFIG"));

    public static readonly DependencyProperty HeaderTitleProperty = DependencyProperty.Register(
        nameof(HeaderTitle), typeof(string), typeof(DialogShell), new PropertyMetadata("对话框"));

    public static readonly DependencyProperty ModelTagProperty = DependencyProperty.Register(
        nameof(ModelTag), typeof(string), typeof(DialogShell), new PropertyMetadata(""));

    public static readonly DependencyProperty VersionTextProperty = DependencyProperty.Register(
        nameof(VersionText), typeof(string), typeof(DialogShell), new PropertyMetadata(""));

    public static readonly DependencyProperty FooterHintProperty = DependencyProperty.Register(
        nameof(FooterHint), typeof(string), typeof(DialogShell), new PropertyMetadata(""));

    public static readonly DependencyProperty ConfirmTextProperty = DependencyProperty.Register(
        nameof(ConfirmText), typeof(string), typeof(DialogShell), new PropertyMetadata("保存并生效"));

    public static readonly DependencyProperty CancelTextProperty = DependencyProperty.Register(
        nameof(CancelText), typeof(string), typeof(DialogShell), new PropertyMetadata("取消"));

    public static readonly DependencyProperty ShowCancelProperty = DependencyProperty.Register(
        nameof(ShowCancel), typeof(bool), typeof(DialogShell), new PropertyMetadata(true));

    public static readonly DependencyProperty ConfirmCommandProperty = DependencyProperty.Register(
        nameof(ConfirmCommand), typeof(ICommand), typeof(DialogShell), new PropertyMetadata(null));

    public string HeaderTag { get => (string)GetValue(HeaderTagProperty); set => SetValue(HeaderTagProperty, value); }
    public string HeaderTitle { get => (string)GetValue(HeaderTitleProperty); set => SetValue(HeaderTitleProperty, value); }
    public string ModelTag { get => (string)GetValue(ModelTagProperty); set => SetValue(ModelTagProperty, value); }
    public string VersionText { get => (string)GetValue(VersionTextProperty); set => SetValue(VersionTextProperty, value); }
    public string FooterHint { get => (string)GetValue(FooterHintProperty); set => SetValue(FooterHintProperty, value); }
    public string ConfirmText { get => (string)GetValue(ConfirmTextProperty); set => SetValue(ConfirmTextProperty, value); }
    public string CancelText { get => (string)GetValue(CancelTextProperty); set => SetValue(CancelTextProperty, value); }
    public bool ShowCancel { get => (bool)GetValue(ShowCancelProperty); set => SetValue(ShowCancelProperty, value); }
    public ICommand? ConfirmCommand { get => (ICommand?)GetValue(ConfirmCommandProperty); set => SetValue(ConfirmCommandProperty, value); }

    /// <summary>模板内 Header ✕ / Footer 取消、主按钮使用的路由命令。</summary>
    public static readonly RoutedCommand ShellCloseCommand = new("DialogShell.Close", typeof(DialogShell));
    public static readonly RoutedCommand ShellCancelCommand = new("DialogShell.Cancel", typeof(DialogShell));
    public static readonly RoutedCommand ShellConfirmCommand = new("DialogShell.Confirm", typeof(DialogShell));

    protected DialogShell()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 560;
        MaxWidth = 720;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            GlassFrameThickness = new Thickness(0),
            CaptionHeight = 40,
            CornerRadius = new CornerRadius(0),
            ResizeBorderThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        });
        SetResourceReference(StyleProperty, "Tg.DialogShell");

        CommandBindings.Add(new CommandBinding(ShellCloseCommand, (_, _) => Close()));
        CommandBindings.Add(new CommandBinding(ShellCancelCommand, (_, _) => Close()));
        CommandBindings.Add(new CommandBinding(ShellConfirmCommand, (_, _) => DoConfirm()));
        // Esc = 取消
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    /// <summary>确认按钮默认动作（未绑 Command 的对话框调用；返回 true=关闭）。</summary>
    public Func<bool>? OnConfirm { get; set; }

    protected internal void DoConfirm()
    {
        if (ConfirmCommand?.CanExecute(null) == true)
        {
            ConfirmCommand.Execute(null);
            return;
        }
        if (OnConfirm?.Invoke() != false)
            Close();
    }
}
