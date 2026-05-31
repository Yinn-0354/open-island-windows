using System.Windows;
using OpenIsland.App.Views;

namespace OpenIsland.App.Services;

/// <summary>
/// 区域截图服务：唤起全屏覆盖层让用户拖拽框选并复制到剪贴板。
/// 由灵动岛上的截图按钮（ScreenshotCommand）和全局快捷键（HotkeyService）共同调用。
/// </summary>
public class ScreenshotService
{
    private ScreenshotOverlayWindow? _active;

    /// <summary>开始一次区域截图（已在截图中则忽略，避免叠多层覆盖）。</summary>
    public void Capture()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        disp.Invoke(() =>
        {
            if (_active != null) return;
            try
            {
                var overlay = new ScreenshotOverlayWindow();
                overlay.Closed += (_, _) => _active = null;
                _active = overlay;
                overlay.Show();
                overlay.Activate();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Screenshot overlay failed: {ex.Message}");
                _active = null;
            }
        });
    }
}
