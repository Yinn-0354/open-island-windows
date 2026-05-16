using System.Runtime.InteropServices;

namespace OpenIsland.App.Services;

/// <summary>
/// 系统级媒体控制 —— 播放/暂停/上一首/下一首走虚拟媒体键（Windows 路由给当前
/// 注册媒体键的播放器：网易云 / Spotify / 浏览器等，跟键盘上的媒体键完全等价，
/// 无需任何 WinRT / 第三方依赖）；音量走 CoreAudio IAudioEndpointVolume，直接
/// 读/写系统主音量（0~1 标量），给 UI 一个真滑块。
/// </summary>
public sealed class MediaControlService
{
    #region 媒体键（SendInput）

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    public void PlayPause() => TapKey(VK_MEDIA_PLAY_PAUSE);
    public void Next() => TapKey(VK_MEDIA_NEXT_TRACK);
    public void Previous() => TapKey(VK_MEDIA_PREV_TRACK);

    private static void TapKey(ushort vk)
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = vk;
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].u.ki.wVk = vk;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT { public uint uMsg; public ushort wParamL; public ushort wParamH; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    #endregion

    #region 系统音量（CoreAudio IAudioEndpointVolume）

    /// <summary>读系统主音量，0.0~1.0；失败返回 -1。</summary>
    public float GetVolume()
    {
        var ep = TryGetEndpointVolume();
        if (ep == null) return -1f;
        try
        {
            return ep.GetMasterVolumeLevelScalar(out var lvl) == 0 ? lvl : -1f;
        }
        finally
        {
            Marshal.ReleaseComObject(ep);
        }
    }

    /// <summary>设系统主音量，scalar 0.0~1.0。</summary>
    public void SetVolume(float scalar)
    {
        scalar = Math.Clamp(scalar, 0f, 1f);
        var ep = TryGetEndpointVolume();
        if (ep == null) return;
        try
        {
            // 事件 GUID 传 Guid.Empty —— 不关心是哪个客户端触发的变更通知
            ep.SetMasterVolumeLevelScalar(scalar, Guid.Empty);
        }
        finally
        {
            Marshal.ReleaseComObject(ep);
        }
    }

    private static IAudioEndpointVolume? TryGetEndpointVolume()
    {
        try
        {
            var enumType = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"))!;
            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;
            try
            {
                // eRender(0) + eConsole(0) = 默认输出设备（扬声器/耳机），跟系统音量条一致
                if (enumerator.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device == null)
                    return null;
                try
                {
                    var iid = typeof(IAudioEndpointVolume).GUID;
                    if (device.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out var o) != 0 || o == null)
                        return null;
                    return (IAudioEndpointVolume)o;
                }
                finally { Marshal.ReleaseComObject(device); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }
        }
        catch
        {
            return null;
        }
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppDevice);
        // 其余方法用不到，省略声明（COM vtable 顺序无所谓只要不调用）
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint pnChannelCount);
        // pguidEventContext 是 LPCGUID（const GUID*），必须按指针编组，不能按值传
        int SetMasterVolumeLevel(float fLevelDB, [MarshalAs(UnmanagedType.LPStruct)] Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, [MarshalAs(UnmanagedType.LPStruct)] Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        // 其余方法用不到，按 vtable 顺序到此为止即可（不调用后面的）
    }

    #endregion
}
