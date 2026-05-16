using System.Runtime.InteropServices;

namespace OpenIsland.App.Services;

public static class SoundService
{
    [DllImport("user32.dll")]
    private static extern bool MessageBeep(uint uType);

    /// <summary>
    /// 提示音总开关 —— 镜像 WorkspaceSettings.SoundEnabled（由 DynamicIslandViewModel
    /// 在构造时及切换时同步写入）。false 时所有播放方法直接 no-op，相当于全局静音。
    /// Master mute, mirrored from WorkspaceSettings.SoundEnabled. When false every
    /// play call is a no-op.
    /// </summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>
    /// 去抖：上一次真正播放的 UTC 时间。1.5s 内的重复播放请求被忽略 —— 同一个 CLI
    /// 完成事件可能同时走 Stop-hook 路径（SessionManager.TaskCompleted）和 watcher
    /// 聚合 Idle 路径（DynamicIslandViewModel.UpdateStatusColor），不去抖会"叮"两声。
    /// Debounce: ignore replays within 1.5s so the Stop-hook path and the
    /// watcher-aggregate path don't double-ding the same completion.
    /// </summary>
    private static DateTime _lastPlayUtc = DateTime.MinValue;
    private static readonly object _gate = new();
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// 通过去抖闸门：返回 true 表示允许本次播放并已记录时间戳；false 表示在窗口内，跳过。
    /// !Enabled 时永远返回 false（全局静音）。
    /// </summary>
    private static bool ShouldPlay()
    {
        if (!Enabled) return false;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastPlayUtc < DebounceWindow) return false;
            _lastPlayUtc = now;
            return true;
        }
    }

    // 任务完成提示音 —— 单声 Asterisk（典型的"叮"），跟 BeepService 同源。
    // 之前是串行 3 声（Simple Beep + Asterisk + Console.Beep）—— 吵且跟 BeepService 重叠。
    // !Enabled 或 1.5s 内重复 → 直接返回（见 ShouldPlay）。
    public static void PlayTaskComplete()
    {
        if (!ShouldPlay()) return;
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

    // 需关注提示音（进入橙色权限 / 红色待回答）—— 用 Exclamation（比 Asterisk 更"催"
    // 一点，区分于普通完成）。同样受总开关 + 1.5s 去抖约束。
    // Attention chime for entering the orange permission / red question state.
    public static void PlayAttention()
    {
        if (!ShouldPlay()) return;
        try
        {
            System.Media.SystemSounds.Exclamation.Play();
        }
        catch
        {
            // 静默兜底：MB_ICONHAND (0x30) ——更尖锐的告警音
            try { MessageBeep(0x00000030); } catch { }
        }
    }
}
