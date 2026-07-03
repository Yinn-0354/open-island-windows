using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenIsland.App.Services;

/// <summary>
/// 屏幕区域抓取（GDI BitBlt）→ BGRA byte[]。坐标为物理像素。设了 WDA_EXCLUDEFROMCAPTURE 的窗口
/// （灵动岛自己）被排除，抓到的是其背后的桌面，避免毛玻璃套毛玻璃自我反馈。
/// </summary>
internal static class ScreenGrab
{
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    private const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE 兜底版：某些环境（远程桌面/RDP 会话等）该 API 会静默失败——
    /// SetWindowDisplayAffinity 返回值被调用方忽略时完全看不出来，实际效果是灵动岛截到自己
    /// 上一帧的玻璃背景，一路自我反馈失真（见 DynamicIslandWindow.SetCaptureExclusion 的验证逻辑）。
    /// 这里改用「截屏瞬间先 ShowWindow(HIDE) 再 CaptureBytes 再 ShowWindow(SHOWNOACTIVATE)」，
    /// 不依赖该 API 是否生效，任何环境下都保证截不到自己，代价是每帧有一瞬间的隐藏（GDI BitBlt
    /// 本身很快，肉眼基本无感）。</summary>
    public static byte[]? CaptureBytesHideWindow(IntPtr hwnd, int x, int y, int w, int h)
    {
        bool hide = hwnd != IntPtr.Zero;
        if (hide) ShowWindow(hwnd, SW_HIDE);
        try { return CaptureBytes(x, y, w, h); }
        finally { if (hide) ShowWindow(hwnd, SW_SHOWNOACTIVATE); }
    }

    /// <summary>抓 (x,y,w,h)（物理像素）→ BGRA byte[]，失败返回 null。</summary>
    public static byte[]? CaptureBytes(int x, int y, int w, int h)
    {
        if (w <= 0 || h <= 0) return null;
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, h);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            if (!BitBlt(mem, 0, 0, w, h, screen, x, y, SRCCOPY)) return null;
            var src = Imaging.CreateBitmapSourceFromHBitmap(
                bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            int stride = w * 4;
            var buf = new byte[h * stride];
            conv.CopyPixels(buf, stride, 0);
            return buf;
        }
        catch { return null; }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }
}
