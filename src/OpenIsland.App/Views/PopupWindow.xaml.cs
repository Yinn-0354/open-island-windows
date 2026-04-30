using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OpenIsland.App.ViewModels;

namespace OpenIsland.App.Views;

public partial class PopupWindow : Window
{
    private readonly PopupViewModel _viewModel;

    public PopupWindow(PopupViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        // 失去焦点时关闭窗口
        Close();
    }

    private void SessionItem_Click(object sender, MouseButtonEventArgs e)
    {
        // 处理会话项点击
    }

    private void ApproveButton_Click(object sender, RoutedEventArgs e)
    {
        // 阻止事件冒泡，避免触发SessionItem_Click
        e.Handled = true;
    }
}
