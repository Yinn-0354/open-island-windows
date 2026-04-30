using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OpenIsland.App.ViewModels;

namespace OpenIsland.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        Closing += OnWindowClosing;
        Loaded += (_, _) => _viewModel.RefreshNow();
    }

    /// <summary>
    /// Windows 11 / 10 (1809+) 通过 DWM API 让系统标题栏跟随暗色主题，
    /// 让 "Open Island - Control Center" 那条横条变成深灰，跟下面 #1C1C1E
    /// 内容一体化，不再有亮白条切割视觉。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        int useDark = 1;
        // Windows 10 1809-1903: attribute 19; Windows 10 2004+/Win11: attribute 20.
        // 两个都试一遍兼容性最广。
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref useDark, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        // 窗口真正关闭，不做取消操作
        // 确保 Closed 事件会被触发
    }

    /// <summary>
    /// 选中指定会话
    /// </summary>
    public void SelectSession(string sessionId)
    {
        var session = _viewModel.Sessions.FirstOrDefault(s => s.Id == sessionId);
        if (session != null)
        {
            _viewModel.SelectedSession = session;
        }
    }
}
