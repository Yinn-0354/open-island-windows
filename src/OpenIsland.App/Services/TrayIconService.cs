using System.Drawing;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using OpenIsland.App.ViewModels;
using OpenIsland.App.Views;

namespace OpenIsland.App.Services;

/// <summary>
/// 系统托盘服务
/// </summary>
public class TrayIconService : IDisposable
{
    private readonly PopupWindowService _popupService;
    private readonly SessionManager _sessionManager;
    private TaskbarIcon? _trayIcon;
    private System.Windows.Controls.MenuItem? _countMenuItem;

    public TrayIconService(PopupWindowService popupService, SessionManager sessionManager)
    {
        _popupService = popupService;
        _sessionManager = sessionManager;
        _sessionManager.SessionsChanged += OnSessionsChanged;
    }

    public void Initialize()
    {
        // 必须在UI线程上初始化
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // 创建托盘图标
            _trayIcon = new TaskbarIcon
            {
                Icon = CreateIcon(),
                ToolTipText = "Open Island",
                Visibility = Visibility.Visible
            };

            // 创建上下文菜单
            _trayIcon.ContextMenu = CreateContextMenu();

            // 点击事件 - 使用TrayMouseDoubleClick避免冲突
            _trayIcon.TrayMouseDoubleClick += OnTrayDoubleClick;

            // 更新图标状态
            UpdateIcon();
        });
    }

    private void OnSessionsChanged(object? sender, EventArgs e)
    {
        // 确保在UI线程更新
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            UpdateIcon();
            UpdateMenuText();
        });
    }

    private void UpdateIcon()
    {
        if (_trayIcon == null) return;

        try
        {
            var attentionCount = _sessionManager.GetAttentionCount();
            var runningCount = _sessionManager.GetRunningCount();

            if (attentionCount > 0)
            {
                _trayIcon.Icon = CreateAttentionIcon(attentionCount);
                _trayIcon.ToolTipText = $"Open Island - {attentionCount} 个会话需要关注";
            }
            else if (runningCount > 0)
            {
                _trayIcon.Icon = CreateRunningIcon(runningCount);
                _trayIcon.ToolTipText = $"Open Island - {runningCount} 个会话运行中";
            }
            else
            {
                _trayIcon.Icon = CreateIcon();
                _trayIcon.ToolTipText = "Open Island - 就绪";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateIcon error: {ex.Message}");
        }
    }

    private void UpdateMenuText()
    {
        if (_countMenuItem == null) return;

        try
        {
            var attentionCount = _sessionManager.GetAttentionCount();
            var runningCount = _sessionManager.GetRunningCount();
            _countMenuItem.Header = $"  运行中: {runningCount}, 需关注: {attentionCount}";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"UpdateMenuText error: {ex.Message}");
        }
    }

    private void OnTrayDoubleClick(object sender, RoutedEventArgs e)
    {
        // 双击打开控制中心
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                _popupService.OpenControlCenter();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"OpenControlCenter error: {ex.Message}");
            }
        });
    }

    private System.Windows.Controls.ContextMenu CreateContextMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        // 状态项
        var statusItem = new System.Windows.Controls.MenuItem
        {
            Header = "状态",
            IsEnabled = false
        };
        menu.Items.Add(statusItem);

        // 计数项（缓存引用以便更新）
        _countMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = "  运行中: 0, 需关注: 0",
            IsEnabled = false
        };
        menu.Items.Add(_countMenuItem);
        menu.Items.Add(new System.Windows.Controls.Separator());

        // 打开控制中心
        var openItem = new System.Windows.Controls.MenuItem { Header = "打开控制中心" };
        openItem.Click += (_, _) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _popupService.OpenControlCenter();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"OpenControlCenter error: {ex.Message}");
                }
            });
        };
        menu.Items.Add(openItem);

        // 设置
        var settingsItem = new System.Windows.Controls.MenuItem { Header = "设置..." };
        settingsItem.Click += (_, _) =>
        {
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    _popupService.OpenSettings();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"OpenSettings error: {ex.Message}");
                }
            });
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // 退出
        var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
        exitItem.Click += (s, e) =>
        {
            if (_trayIcon != null)
            {
                _trayIcon.Dispose();
                _trayIcon = null;
            }
            Environment.Exit(0);
        };
        menu.Items.Add(exitItem);

        // 菜单打开时更新文本
        menu.Opened += (_, _) => UpdateMenuText();

        return menu;
    }

    private Icon CreateIcon()
    {
        return SystemIcons.Application;
    }

    private Icon CreateRunningIcon(int count)
    {
        return SystemIcons.Application;
    }

    private Icon CreateAttentionIcon(int count)
    {
        return SystemIcons.Exclamation;
    }

    public void Dispose()
    {
        try
        {
            _trayIcon?.Dispose();
            _sessionManager.SessionsChanged -= OnSessionsChanged;
        }
        catch { }
    }
}
