using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TokenMonitor.App.Services;

namespace TokenMonitor.App.Dialogs;

/// <summary>卡片条目（显示隐藏卡片对话框）。</summary>
public sealed class CardItemVm : INotifyPropertyChanged
{
    private bool _visible;
    private bool _armed;
    public string Key { get; init; } = "";
    public bool Visible { get => _visible; set { _visible = value; PropertyChanged?.Invoke(this, new(nameof(Visible))); } }

    /// <summary>删除二次确认的行内状态：首次点击进入待确认，再次点击才真正删除。</summary>
    public bool Armed
    {
        get => _armed;
        set
        {
            _armed = value;
            PropertyChanged?.Invoke(this, new(nameof(Armed)));
            PropertyChanged?.Invoke(this, new(nameof(DeleteLabel)));
        }
    }

    public string DeleteLabel => _armed ? "确认删除" : "删除";
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 对话框 7：显示隐藏卡片（01-§3-D8）：勾选 + 拖拽/▲▼ 排序 + 删除选中 + 二次确认。
/// 完成 → card_order / hidden_cards 持久化；删除 → Engine.DeleteModelData（后台）。
/// </summary>
public partial class CardVisibilityDialog : ShellDialog
{
    private readonly AppServices _svc;
    public ObservableCollection<CardItemVm> Items { get; } = new();

    public RelayCommand<CardItemVm> MoveUpCommand { get; }
    public RelayCommand<CardItemVm> MoveDownCommand { get; }
    public RelayCommand<CardItemVm> DeleteCommand { get; }

    public CardVisibilityDialog(AppServices svc)
    {
        // 命令必须先于 InitializeComponent 赋值：{ get; } 属性无变更通知，
        // XAML 在 InitializeComponent 时一次性求值，晚赋值会让命令绑定永久为 null（按钮点击无反应）
        _svc = svc;
        MoveUpCommand = new RelayCommand<CardItemVm>(MoveUp);
        MoveDownCommand = new RelayCommand<CardItemVm>(MoveDown);
        DeleteCommand = new RelayCommand<CardItemVm>(Delete);
        InitializeComponent();

        var main = _svc.Main;
        if (main is not null)
            foreach (var c in main.Cards)
                Items.Add(new CardItemVm { Key = c.Key, Visible = !c.IsHidden });
        try
        {
            foreach (var (p, m) in _svc.Engine.Queries.GetAllModels())
            {
                var key = p + "/" + m;
                if (Items.All(i => i.Key != key))
                    Items.Add(new CardItemVm { Key = key, Visible = main?.Cards.Any(c => c.Key == key) == true });
            }
        }
        catch { /* 查询失败时仅显示当前卡片 */ }
        OnConfirm = Done;
    }

    private void MoveUp(CardItemVm? item)
    {
        var i = item is null ? -1 : Items.IndexOf(item);
        if (i > 0) Items.Move(i, i - 1);
    }

    private void MoveDown(CardItemVm? item)
    {
        var i = item is null ? -1 : Items.IndexOf(item);
        if (i >= 0 && i < Items.Count - 1) Items.Move(i, i + 1);
    }

    private void Delete(CardItemVm? item)
    {
        if (item is null) return;
        // 行内二次确认：不用 MessageBox——归 Topmost 对话框所有的 MessageBox 会藏在
        // 对话框后面（表现为"删除按钮无效"）；首次点击进入待确认态，再点确认才删除。
        if (!item.Armed)
        {
            foreach (var other in Items) other.Armed = false;
            item.Armed = true;
            return;
        }
        Items.Remove(item);
        var modelKey = item.Key;
        Task.Run(() =>
        {
            try { _svc.Engine.DeleteModelData(modelKey); }
            catch (Exception ex) { Core.SysUtil.Logger.Error("App", "delete model failed", ex); }
        });
    }

    private bool Done()
    {
        var main = _svc.Main;
        if (main is not null)
        {
            main.ApplyCardOrder(Items.Select(i => i.Key));
            foreach (var item in Items)
            {
                var card = main.Cards.FirstOrDefault(c => c.Key == item.Key);
                if (card is not null) main.SetHidden(card, !item.Visible);
            }
        }
        return true;
    }

    // —— 拖拽排序 ——
    private Point _dragStart;

    /// <summary>记录拖拽起点。此前 _dragStart 从未赋值（恒为 0,0），
    /// 导致任何带左键的鼠标移动都越过阈值直接触发 DoDragDrop，
    /// 把点击劫持成拖拽 → 复选框/▲▼/删除全部点不动。</summary>
    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => _dragStart = e.GetPosition(null);

    private void List_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStart.X) < 4 && Math.Abs(pos.Y - _dragStart.Y) < 4) return;
        if (e.OriginalSource is DependencyObject src &&
            ItemsControl.ContainerFromElement(List, src) is ListBoxItem { DataContext: CardItemVm item })
        {
            DragDrop.DoDragDrop(List, item, DragDropEffects.Move);
        }
    }

    private void List_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CardItemVm)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void List_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CardItemVm)) is not CardItemVm payload) return;
        if (e.OriginalSource is not DependencyObject src ||
            ItemsControl.ContainerFromElement(List, src) is not ListBoxItem { DataContext: CardItemVm target }) return;
        var from = Items.IndexOf(payload);
        var to = Items.IndexOf(target);
        if (from < 0 || to < 0 || from == to) return;
        Items.Move(from, to);
    }
}
