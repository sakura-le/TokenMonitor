using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace TokenMonitor.App.Dialogs;

/// <summary>
/// 对话框壳 + 手写 INPC 混入基类（Window 已占用基类，不能继承 ObservableObject）。
/// </summary>
public abstract class ShellDialog : DialogShell, INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected ShellDialog()
    {
        // 表单绑定 {Binding X} 直接解析到对话框自身
        DataContext = this;
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}
