using System.Runtime.InteropServices;
using OpenIsland.Core.Models;

namespace OpenIsland.App.Services;

/// <summary>
/// 任务完成提示音服务 — 监听 SessionManager.TaskCompleted 事件并在 WPF 进程内播放蜂鸣音
/// </summary>
/// <remarks>
/// 这一行为以前由 hook 子进程 (open-island-hooks.exe) 在收到 Stop 事件时播放，
/// 但 Claude 的 hook 路径正在切换为 transcript-watcher 模式，因此提示音必须由
/// 主 WPF 进程在会话进入 Idle 状态时本地触发。
///
/// PlayBeep 的三段降级链与 OpenIsland.Hooks/Program.cs:PlayBeep 严格对齐：
///   1. MessageBeep(0)        — Simple Beep，最可靠的兜底
///   2. MessageBeep(0x40)     — MB_ICONINFORMATION，Windows 提示音方案
///   3. Console.Beep(800,200) — 主板蜂鸣器，在没有声卡的环境下仍可发声
/// 任意一步抛异常都被吞掉，确保提示音永远不会让进程崩溃。
/// </remarks>
public class BeepService
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint uType);

    /// <summary>
    /// SessionManager.TaskCompleted 事件处理器 — 当任意会话进入 Idle 时播放提示音
    /// </summary>
    /// <param name="sender">事件源（通常为 SessionManager）</param>
    /// <param name="session">刚完成的会话</param>
    public void OnTaskCompleted(object? sender, AgentSession session)
    {
        // 任务完成提示 — 当会话切换到 Idle 时立即触发
        PlayBeep();
    }

    /// <summary>
    /// 播放任务完成的"叮"声 — 优先用 Windows Asterisk（清脆短促，典型的提示音），
    /// 失败再退到更生硬的 Simple Beep / 主板蜂鸣兜底。
    /// </summary>
    private static void PlayBeep()
    {
        // 1) SystemSounds.Asterisk —— 系统 .Default 主题里的"叮"声，最自然
        try
        {
            System.Media.SystemSounds.Asterisk.Play();
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BeepService: SystemSounds.Asterisk failed: {ex.Message}");
        }

        // 2) MessageBeep MB_ICONINFORMATION —— 同样的"叮"信息提示，走 Win32 直接调
        try
        {
            if (MessageBeep(0x00000040))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BeepService: MessageBeep(0x40) failed: {ex.Message}");
        }

        // 3) Simple Beep —— 不依赖系统声音方案的兜底
        try
        {
            if (MessageBeep(0x00000000))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BeepService: MessageBeep(0) failed: {ex.Message}");
        }

        // 4) Console.Beep —— 主板蜂鸣最后兜底；880Hz 100ms 给个清亮的"叮"
        try
        {
            Console.Beep(880, 100);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"BeepService: Console.Beep failed: {ex.Message}");
        }
    }
}
