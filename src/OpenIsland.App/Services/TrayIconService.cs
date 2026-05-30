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
    private readonly WorkspaceSettings _settings;
    private TaskbarIcon? _trayIcon;
    private System.Windows.Controls.MenuItem? _countMenuItem;
    private Icon? _faceIcon; // face.ico 加载一次缓存，三种状态共用

    public TrayIconService(PopupWindowService popupService, SessionManager sessionManager, WorkspaceSettings settings)
    {
        _popupService = popupService;
        _sessionManager = sessionManager;
        _settings = settings;
        _sessionManager.SessionsChanged += OnSessionsChanged;
        // 语言切换后重建菜单与图标提示（文案随之变中/英）
        Loc.Instance.LanguageChanged += () => Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_trayIcon != null) _trayIcon.ContextMenu = CreateContextMenu();
            UpdateIcon();
        });
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
                _trayIcon.ToolTipText = Loc.Format("Tip_Attention", attentionCount);
            }
            else if (runningCount > 0)
            {
                _trayIcon.Icon = CreateRunningIcon(runningCount);
                _trayIcon.ToolTipText = Loc.Format("Tip_Running", runningCount);
            }
            else
            {
                _trayIcon.Icon = CreateIcon();
                _trayIcon.ToolTipText = Loc.Get("Tip_Ready");
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
            _countMenuItem.Header = Loc.Format("Tray_Counts", runningCount, attentionCount);
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
            Header = Loc.Get("Tray_Status"),
            IsEnabled = false
        };
        menu.Items.Add(statusItem);

        // 计数项（缓存引用以便更新）
        _countMenuItem = new System.Windows.Controls.MenuItem
        {
            Header = Loc.Format("Tray_Counts", 0, 0),
            IsEnabled = false
        };
        menu.Items.Add(_countMenuItem);
        menu.Items.Add(new System.Windows.Controls.Separator());

        // 打开控制中心
        var openItem = new System.Windows.Controls.MenuItem { Header = Loc.Get("Tray_Open") };
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
        var settingsItem = new System.Windows.Controls.MenuItem { Header = Loc.Get("Tray_Settings") };
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

        // 语言子菜单：跟随系统 / 中文 / English（当前项打勾）
        var langItem = new System.Windows.Controls.MenuItem { Header = Loc.Get("Tray_Language") };
        langItem.Items.Add(BuildLangChoice("Lang_Auto", "auto"));
        langItem.Items.Add(BuildLangChoice("Lang_Zh", "zh"));
        langItem.Items.Add(BuildLangChoice("Lang_En", "en"));
        menu.Items.Add(langItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        // 退出
        var exitItem = new System.Windows.Controls.MenuItem { Header = Loc.Get("Tray_Exit") };
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

    /// <summary>语言子项：当前生效设置打勾；点击 = 持久化 + 立即切换界面语言。</summary>
    private System.Windows.Controls.MenuItem BuildLangChoice(string labelKey, string value)
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = Loc.Get(labelKey),
            IsCheckable = true,
            IsChecked = _settings.Language == value
        };
        item.Click += (_, _) =>
        {
            _settings.SetLanguage(value);   // 持久化（"auto"/"zh"/"en"）
            Loc.Instance.Apply(value);      // 实时切换：触发所有绑定与监听者刷新
        };
        return item;
    }

    /// <summary>
    /// 托盘图标 = face.ico（嵌入的 Resource）。加载一次缓存复用 ——
    /// 不要每次 UpdateIcon 都 new（System.Drawing.Icon 持非托管句柄，频繁建会泄漏）。
    /// 失败兜底 SystemIcons.Application，绝不让托盘建不出来。
    /// </summary>
    private Icon FaceIcon()
    {
        if (_faceIcon != null) return _faceIcon;
        try
        {
            var uri = new Uri("pack://application:,,,/OpenIsland;component/Assets/face.ico", UriKind.Absolute);
            var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
            if (stream != null)
            {
                using (stream)
                    _faceIcon = new Icon(stream); // 多尺寸 ico，Windows 自动按 DPI 选合适大小
            }
        }
        catch { /* 落到兜底 */ }
        return _faceIcon ??= SystemIcons.Application;
    }

    private Icon CreateIcon() => FaceIcon();

    private Icon CreateRunningIcon(int count) => FaceIcon();

    private Icon CreateAttentionIcon(int count) => FaceIcon();

    public void Dispose()
    {
        try
        {
            _trayIcon?.Dispose();
            _sessionManager.SessionsChanged -= OnSessionsChanged;
            if (_faceIcon != null && _faceIcon != SystemIcons.Application)
                _faceIcon.Dispose();
        }
        catch { }
    }
}
