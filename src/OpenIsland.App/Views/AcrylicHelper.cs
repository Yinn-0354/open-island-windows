using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace OpenIsland.App.Views;

/// <summary>
/// Apple 风毛玻璃：通过未公开的 user32!SetWindowCompositionAttribute 给窗口挂
/// ACCENT_ENABLE_ACRYLICBLURBEHIND（Win10 1803+），让窗口背后的内容实时模糊透出。
/// 之所以不用 WPF 自带 BlurEffect —— 它只能模糊窗口内的元素，模糊不了桌面背景。
/// </summary>
public static class AcrylicHelper
{
    /// <summary>对应 Win32 ACCENT_POLICY：State=0 禁用，4=ACCENT_ENABLE_ACRYLICBLURBEHIND。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor; // ABGR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttribData
    {
        public int Attrib; // 19 = WCA_ACCENT_POLICY
        public IntPtr PvData;
        public int CbData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttribData data);


    /// <summary>
    /// 给窗口开/关 acrylic。tintAlpha 是系统混入深灰 tint 的不透明度（0-255）。
    /// hwnd 未就绪（SourceInitialized 之前）时直接 no-op，调用方在 SourceInitialized 后再调。
    /// </summary>
    public static void Apply(System.Windows.Window w, bool enable, byte tintAlpha)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;

        var accent = new AccentPolicy
        {
            // 4 = ACCENT_ENABLE_ACRYLICBLURBEHIND。宿主是灵动岛窗口（AllowsTransparency
            // 分层窗口）。实测（Win10 19045）：accent 只在分层窗口上渲染且 state 4 正常
            // （带饱和度提升的真亚克力质感）；非分层窗口上反而整窗纯黑。
            AccentState = enable ? 4 : 0,
            AccentFlags = 2,
            // GradientColor 为 ABGR：高 8 位 alpha + 深灰 tint（#0D0D0D，与岛底色一致；
            // R/G/B 同值，BGR 字节序无影响）
            GradientColor = ((uint)tintAlpha << 24) | 0x000D0D0D,
            AnimationId = 0
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WindowCompositionAttribData
            {
                Attrib = 19, // WCA_ACCENT_POLICY
                PvData = ptr,
                CbData = size
            };
            SetWindowCompositionAttribute(h, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

}
