using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using OpenIsland.App.Views;

namespace OpenIsland.App.Services;

/// <summary>
/// 弹窗窗口服务
/// </summary>
public class PopupWindowService
{
    private readonly IServiceProvider _serviceProvider;
    private PopupWindow? _popupWindow;
    private MainWindow? _mainWindow;

    public PopupWindowService(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>
    /// 切换弹窗显示状态
    /// </summary>
    public void TogglePopup()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_popupWindow?.IsVisible == true)
                {
                    ClosePopup();
                }
                else
                {
                    ShowPopup();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"TogglePopup error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 显示弹窗
    /// </summary>
    public void ShowPopup()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ClosePopup(); // 确保只有一个弹窗

                _popupWindow = _serviceProvider.GetRequiredService<PopupWindow>();
                _popupWindow.Closed += (_, _) => _popupWindow = null;

                // 定位到托盘图标上方
                PositionPopup();

                _popupWindow.Show();
                _popupWindow.Activate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ShowPopup error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 关闭弹窗
    /// </summary>
    public void ClosePopup()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_popupWindow != null)
                {
                    _popupWindow.Close();
                    _popupWindow = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"ClosePopup error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// 打开控制中心
    /// </summary>
    public void OpenControlCenter(string? selectSessionId = null)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                ClosePopup();

                // 如果窗口不存在或已关闭，创建新实例
                if (_mainWindow == null)
                {
                    _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
                    _mainWindow.Closed += (_, _) => _mainWindow = null;
                }

                if (_mainWindow.WindowState == WindowState.Minimized)
                {
                    _mainWindow.WindowState = WindowState.Normal;
                }

                _mainWindow.Show();
                _mainWindow.Activate();
                _mainWindow.Focus();

                if (selectSessionId != null)
                {
                    _mainWindow.SelectSession(selectSessionId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenControlCenter error: {ex.Message}");
                _mainWindow = null;
            }
        });
    }

    /// <summary>
    /// 打开设置
    /// </summary>
    public void OpenSettings()
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                // SettingsWindow 走 DI 拿（带 WorkspaceSettings 注入），用 ShowDialog 打开 ——
                // 这样 SaveButton 里的 DialogResult 才合法；以前用 Show() 导致点保存崩 app。
                var sp = ((App)Application.Current).ServiceProvider;
                var settingsWindow = sp?.GetService(typeof(Views.SettingsWindow)) as Views.SettingsWindow;
                if (settingsWindow == null) return;
                settingsWindow.Owner = Application.Current.Windows
                    .OfType<Views.MainWindow>().FirstOrDefault();
                settingsWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"打开设置窗口失败: {ex.Message}", "Open Island", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
    }

    private void PositionPopup()
    {
        if (_popupWindow == null) return;

        try
        {
            // 获取屏幕工作区
            var screen = SystemParameters.WorkArea;

            // 默认显示在屏幕右下角（系统托盘附近）
            _popupWindow.Left = screen.Width - _popupWindow.Width - 10;
            _popupWindow.Top = screen.Height - _popupWindow.Height - 10;

            // 确保在屏幕内
            if (_popupWindow.Left < 0) _popupWindow.Left = 10;
            if (_popupWindow.Top < 0) _popupWindow.Top = 10;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"PositionPopup error: {ex.Message}");
        }
    }
}
