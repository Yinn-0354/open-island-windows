using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using OpenIsland.App.Services;
using OpenIsland.App.ViewModels;
using OpenIsland.App.Views;
using OpenIsland.Core;
using OpenIsland.Core.Bridge;
using OpenIsland.Core.Registry;

namespace OpenIsland.App;

public partial class App : Application
{
    private IServiceProvider? _serviceProvider;
    /// <summary>给 ViewModel 等需要打开 transient Window（如 SettingsWindow）的入口取用。</summary>
    public IServiceProvider? ServiceProvider => _serviceProvider;
    private TrayIconService? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 配置依赖注入
        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        // 初始化托盘服务
        _trayIcon = _serviceProvider.GetRequiredService<TrayIconService>();
        _trayIcon.Initialize();

        // 显示 Dynamic Island 悬浮窗
        var dynamicIsland = _serviceProvider.GetRequiredService<DynamicIslandWindow>();
        dynamicIsland.Show();

        // 提示音由 hook 子进程（open-island-hooks.exe 见到 Stop 事件就 PlayBeep）单独负责，
        // 主进程不再订阅 TaskCompleted 播声 —— 用户明确要求只保留 hook 端那个声音，
        // 撤掉主进程的 SystemSounds.Asterisk 二次响铃。
        // SessionManager.TaskCompleted 仍然触发 DynamicIslandViewModel.OnTaskCompleted（绿灯）。
        var sessionManager = _serviceProvider.GetRequiredService<SessionManager>();
        sessionManager.AttentionRequired += (_, session) =>
            Task.Run(SoundService.PlayTaskComplete);

        // 启动 Claude transcript watcher：先全量扫描补齐已有 session，再挂 FileSystemWatcher
        // 取代 SessionManager 里旧的 5s _scanTimer 与所有 Claude-family hook 通道
        var claudeWatcher = _serviceProvider.GetRequiredService<ClaudeTranscriptWatcher>();
        claudeWatcher.Start();

        // 启动桥接服务器
        try
        {
            var bridgeServer = _serviceProvider.GetRequiredService<BridgeServer>();
            _ = bridgeServer.StartAsync();
            System.Diagnostics.Debug.WriteLine("BridgeServer started successfully");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to start BridgeServer: {ex.Message}");
            MessageBox.Show($"启动通信服务失败: {ex.Message}", "Open Island", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 检查是否自动安装hooks
        var setupService = _serviceProvider.GetRequiredService<SetupService>();
        _ = setupService.CheckAndAutoInstallAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        if (_serviceProvider is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnExit(e);
    }

    private void ConfigureServices(IServiceCollection services)
    {
        // 核心服务
        services.AddSingleton<BridgeServer>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<TerminalJumpService>();
        services.AddSingleton<ProcessMonitorService>();
        services.AddSingleton<ClaudeTranscriptDiscovery>();
        services.AddSingleton<ClaudeTranscriptWatcher>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<SetupService>();
        services.AddSingleton<BeepService>();
        services.AddSingleton<WorkspaceSettings>();
        services.AddSingleton<SystemStatsService>();
        services.AddSingleton<MediaControlService>();

        // UI服务
        services.AddSingleton<TrayIconService>();
        services.AddSingleton<PopupWindowService>();

        // ViewModels
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DynamicIslandViewModel>();
        services.AddTransient<PopupViewModel>();

        // Views
        services.AddTransient<MainWindow>();
        services.AddTransient<PopupWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddSingleton<DynamicIslandWindow>();
    }
}
