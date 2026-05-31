using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace OpenIsland.App.Services;

/// <summary>
/// 全局快捷键服务：用 RegisterHotKey 把"区域截图"绑定为系统级热键（默认 Ctrl+Q，设置中心可改）。
/// 在一个隐藏的 HwndSource 消息窗口上接收 WM_HOTKEY。设置变更时自动重新绑定。
/// </summary>
public class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 0xB001;
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly WorkspaceSettings _settings;
    private readonly ScreenshotService _screenshot;
    private HwndSource? _src;
    private IntPtr _hwnd;
    private bool _registered;

    public HotkeyService(WorkspaceSettings settings, ScreenshotService screenshot)
    {
        _settings = settings;
        _screenshot = screenshot;
    }

    /// <summary>在 UI 线程调用：建隐藏消息窗口、注册当前快捷键、并监听设置变更自动重绑。</summary>
    public void Start()
    {
        var p = new HwndSourceParameters("OpenIslandHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0 // 不可见
        };
        _src = new HwndSource(p);
        _hwnd = _src.Handle;
        _src.AddHook(WndProc);
        Rebind(_settings.ScreenshotHotkey);
        _settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
        => Application.Current?.Dispatcher.Invoke(() => Rebind(_settings.ScreenshotHotkey));

    /// <summary>解绑旧热键、按新字符串注册（解析失败则不注册，等同于禁用）。</summary>
    public void Rebind(string hotkey)
    {
        if (_hwnd == IntPtr.Zero) return;
        if (_registered) { UnregisterHotKey(_hwnd, HOTKEY_ID); _registered = false; }
        if (!TryParse(hotkey, out var mods, out var vk)) return;
        // 注册失败（如组合键已被其它程序占用，GetLastError=1409）则保持未注册：截图按钮仍可用，
        // 用户可在设置中心改一个空闲组合键。
        _registered = RegisterHotKey(_hwnd, HOTKEY_ID, mods | MOD_NOREPEAT, vk);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            handled = true;
            _screenshot.Capture();
        }
        return IntPtr.Zero;
    }

    /// <summary>解析 "Ctrl+Shift+Q" 之类的字符串为 (修饰键掩码, 虚拟键码)。需至少一个非修饰主键。</summary>
    public static bool TryParse(string hotkey, out uint mods, out uint vk)
    {
        mods = 0; vk = 0;
        if (string.IsNullOrWhiteSpace(hotkey)) return false;
        foreach (var raw in hotkey.Split('+'))
        {
            var t = raw.Trim();
            switch (t.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= MOD_CONTROL; break;
                case "alt": mods |= MOD_ALT; break;
                case "shift": mods |= MOD_SHIFT; break;
                case "win": case "windows": mods |= MOD_WIN; break;
                case "": break;
                default: vk = KeyToVk(t); break;
            }
        }
        return vk != 0;
    }

    private static uint KeyToVk(string k)
    {
        if (k.Length == 1)
        {
            char c = char.ToUpperInvariant(k[0]);
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }
        if ((k[0] == 'F' || k[0] == 'f') && int.TryParse(k.Substring(1), out var n) && n >= 1 && n <= 24)
            return (uint)(0x70 + n - 1); // VK_F1 = 0x70
        return k.ToLowerInvariant() switch
        {
            "space" => 0x20,
            "enter" or "return" => 0x0D,
            "tab" => 0x09,
            _ => 0u
        };
    }

    public void Dispose()
    {
        try
        {
            _settings.Changed -= OnSettingsChanged;
            if (_registered && _hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HOTKEY_ID);
            _src?.RemoveHook(WndProc);
            _src?.Dispose();
        }
        catch { }
    }
}
