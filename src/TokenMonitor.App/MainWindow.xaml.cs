using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TokenMonitor.App.Controls;
using TokenMonitor.App.Infrastructure;
using TokenMonitor.App.Services;
using TokenMonitor.App.ViewModels;

namespace TokenMonitor.App;

/// <summary>
/// 主面板窗口壳：chrome/拖拽类不可避免的少量后台代码。
/// 窗口矩形/透明度记忆、漏抓横幅显隐动画、卡片入场错峰动画、F5/F2 快捷键、
/// 3 列自适应（宽 >900）、选中卡联动侧栏。
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppServices _svc;
    private MainViewModel? _vm;

    public MainWindow()
    {
        InitializeComponent();
        _svc = AppServices.Instance;
        SourceInitialized += (_, _) => _svc.UiState.RestorePanel(this);
        Loaded += OnLoaded;
        Closing += (_, _) => _svc.UiState.SavePanel(this, _vm);
        LocationChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
        SizeChanged += (_, _) => { _saveTimer.Stop(); _saveTimer.Start(); };
        // 3 列自适应（§1.2：窗口宽 >900 时 3 列）
        PreviewKeyDown += OnPreviewKeyDown;
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _svc.UiState.SavePanel(this, _vm); };
    }

    private readonly DispatcherTimer _saveTimer = new();

    private MainViewModel Vm => _vm ??= (MainViewModel)DataContext;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DataContext = _vm = AppServices.Instance.Main;
        if (_vm is null) return;

        // 卡片视图（过滤隐藏卡）
        var view = new System.Windows.Data.ListCollectionView(_vm.Cards) { Filter = o => o is CardViewModel c && !c.IsHidden };
        CardsHost.ItemsSource = view;
        _vm.VisibleChanged += () => Dispatcher.Invoke(view.Refresh);

        // 卡片入场错峰（首次 + DayRollover 由 RenderCards 触发）
        _vm.Cards.CollectionChanged += (_, _) => Dispatcher.BeginInvoke(PlayCardEntrance);
        _vm.PropertyChanged += (_, e2) =>
        {
            if (e2.PropertyName is nameof(MainViewModel.PanelOpacity) or nameof(MainViewModel.IsTopmost)) { }
        };

        // 侧栏折线数据同步（Points 是 IReadOnlyList，代码侧同步）
        _vm.Side.PropertyChanged += (_, e2) =>
        {
            if (e2.PropertyName is nameof(SideDetailViewModel.ChartVersion) or nameof(SideDetailViewModel.Chart))
            {
                Trend.Points = _vm.Side.ChartValues;
                Trend.Labels = _vm.Side.ChartLabels;
                Trend.PlayEntrance();
            }
        };
        Trend.Points = _vm.Side.ChartValues;
        Trend.Labels = _vm.Side.ChartLabels;

        // 皮肤切换 → 钳制透明度下限
        ThemeService.Instance.SkinChanged += (_, _) =>
        {
            Dispatcher.Invoke(() =>
            {
                if (Opacity < ThemeService.Instance.MinOpacity) Opacity = ThemeService.Instance.MinOpacity;
            });
        };

        PlayCardEntrance();
    }

    /// <summary>显示/隐藏（收起到球 ↔ 恢复）。</summary>
    public void CollapseToBall()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease() };
        var move = new DoubleAnimation(0, 8, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease() };
        var tf = new TranslateTransform();
        RootBorder.RenderTransform = tf;
        fade.Completed += (_, _) => { Hide(); tf.BeginAnimation(TranslateTransform.YProperty, null); };
        tf.BeginAnimation(TranslateTransform.YProperty, move);
        BeginAnimation(OpacityProperty, fade);
    }

    public void RestoreFromBall()
    {
        if (IsVisible)
        {
            Activate();
            Topmost = _vm?.IsTopmost ?? true;
            return;
        }
        Show();
        var fade = new DoubleAnimation(0, _vm?.PanelOpacity ?? 1.0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease() };
        var tf = new TranslateTransform { Y = 8 };
        RootBorder.RenderTransform = tf;
        var move = new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = Ease() };
        move.Completed += (_, _) => RootBorder.RenderTransform = Transform.Identity;
        tf.BeginAnimation(TranslateTransform.YProperty, move);
        BeginAnimation(OpacityProperty, fade);
        Activate();
    }

    private static IEasingFunction Ease() => new CircleEase { EasingMode = EasingMode.EaseOut };

    /// <summary>卡片入场：淡入 + X(-10→0)，BeginTime 阶梯 50ms×n（§5；S3 主要表达，其余淡入）。</summary>
    public void PlayCardEntrance()
    {
        var idx = 0;
        foreach (var item in CardsHost.Items)
        {
            var container = CardsHost.ItemContainerGenerator.ContainerFromItem(item) as ContentPresenter;
            var card = FindVisualChild<ModelCard>(container);
            if (card is null) continue;
            card.Opacity = 0;
            var tf = new TranslateTransform { X = -10 };
            card.RenderTransform = tf;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = TimeSpan.FromMilliseconds(50 * idx),
                EasingFunction = Ease()
            };
            var move = new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(320))
            {
                BeginTime = TimeSpan.FromMilliseconds(50 * idx),
                EasingFunction = Ease()
            };
            move.Completed += (_, _) => card.RenderTransform = Transform.Identity;
            card.BeginAnimation(OpacityProperty, fade);
            tf.BeginAnimation(TranslateTransform.XProperty, move);
            idx++;
            if (idx > 11) break;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) return typed;
            var found = FindVisualChild<T>(child);
            if (found is not null) return found;
        }
        return null;
    }

    // ================= 交互 =================

    private void Card_MouseRightButton(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CardViewModel card })
            Vm!.SelectCardCommand.Execute(card);   // 右键即选中（菜单动作针对该卡）
    }

    private void Card_MouseLeftButton(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CardViewModel card })
        {
            Vm!.SelectCardCommand.Execute(card);
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // F5 刷新选中卡；F2 面板↔球 联动（§1.5 / F 系列）
        if (e.Key == Key.F5)
        {
            if (_vm?.SelectedCard is not null) _vm.RefreshCard(_vm.SelectedCard);
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
        {
            _svc.CollapseToBall();
            e.Handled = true;
        }
    }

    private void SideHead_MouseRightButton(object sender, MouseButtonEventArgs e)
    {
        // 侧栏标题右键 = 标题栏菜单（显示隐藏卡片/操作日志）
        ContextMenu?.OpenContextMenuIfPossible();
        e.Handled = true;
    }
}

internal static class ContextMenuExtensions
{
    public static void OpenContextMenuIfPossible(this FrameworkElement fe)
    {
        // 侧栏头右键：显示隐藏卡片 / 操作日志
        var menu = new ContextMenu();
        var vm = AppServices.Instance.Main;
        if (vm is null) return;
        var m1 = new MenuItem { Header = "显示隐藏卡片…", Command = vm.OpenCardVisibilityCommand };
        var m2 = new MenuItem { Header = "操作日志（全部）", Command = vm.OpenOpLogsCommand, CommandParameter = null };
        m1.SetResourceReference(FrameworkElement.StyleProperty, "Tg.MenuItem");
        m2.SetResourceReference(FrameworkElement.StyleProperty, "Tg.MenuItem");
        menu.Items.Add(m1);
        menu.Items.Add(m2);
        menu.PlacementTarget = fe;
        menu.IsOpen = true;
    }
}
