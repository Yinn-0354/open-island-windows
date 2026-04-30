using System.Runtime.InteropServices;

namespace OpenIsland.App.Services;

public static class SoundService
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    // 任务完成 / 权限关注提示音 —— 单声 Asterisk（典型的"叮"），跟 BeepService 同源。
    // 之前是串行 3 声（Simple Beep + Asterisk + Console.Beep）—— 吵且跟 BeepService 重叠。
    public static void PlayTaskComplete()
    {
        try
        {
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch
        {
            // 静默兜底
            try { MessageBeep(0x00000040); } catch { }
        }
    }
}
