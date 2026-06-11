using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OpenIsland.App.Services;

/// <summary>注入结果：成功与否 + 失败原因（供卡片显示精确状态）。</summary>
public readonly record struct InjectResult(bool Ok, string? Reason);

/// <summary>
/// 快捷回复 / 模型切换共用的「把一段文字粘贴进目标会话」注入原语。
///
/// 机制：剪贴板粘贴（存当前剪贴板 → 写入文字 → 激活并校验目标在前台 → Ctrl+V →
/// 可选回车 → 还原剪贴板）。相比逐字 SendInput，粘贴对中文/代码/长文都稳，且只有一次
/// 聚焦窗口，发错窗口的暴露面最小。
///
/// 安全不变量（同 <see cref="SendKeysToTerminalAsync"/>）：SendInput 注入到 *当前前台*，
/// 所以粘贴前必须确认前台 HWND 就是我们刚激活的目标窗口，否则中止 —— 绝不把粘贴/回车
/// 打到错误的应用。
/// </summary>
public partial class TerminalJumpService
{
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RETURN = 0x0D;

    // 进程级注入闸门：剪贴板是进程全局资源，串行化所有注入，避免两张卡片同时注入时
    // 互相覆盖各自保存/写入/还原的剪贴板内容（per-card 的 _busy 只防同一张卡重入）。
    private readonly System.Threading.SemaphoreSlim _injectGate = new(1, 1);

    private static readonly string InjectDiagPath =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "openisland-inject.log");

    /// <summary>注入诊断日志（落 %TEMP%\openisland-inject.log）。绝不打印注入文字本身，仅记长度。</summary>
    internal static void InjectDiag(string msg)
    {
        try { System.IO.File.AppendAllText(InjectDiagPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n"); } catch { }
    }

    /// <summary>把文字粘贴进承载 claude pid 的终端窗口，可选回车提交。</summary>
    public async Task<InjectResult> SendTextToTerminalAsync(int claudePid, string text, bool submit)
    {
        InjectDiag($"terminal: enter pid={claudePid} textLen={text?.Length ?? 0} submit={submit}");
        await _injectGate.WaitAsync();
        try
        {
            var targetHwnd = FindTerminalHwndByPidChain(claudePid);
            InjectDiag($"terminal: resolved hwnd=0x{targetHwnd.ToInt64():X}");
            if (targetHwnd == IntPtr.Zero)
            {
                _logger?.LogWarning("SendTextToTerminalAsync: no terminal window for pid {Pid}", claudePid);
                InjectDiag("terminal: -> no-terminal (FindTerminalHwndByPidChain 找不到窗口)");
                return new InjectResult(false, "no-terminal");
            }

            var saved = GetClipboardTextSafe();
            if (!SetClipboardTextSafe(text!))
            {
                InjectDiag("terminal: -> clipboard-failed (写剪贴板失败)");
                return new InjectResult(false, "clipboard-failed");
            }

            try
            {
                var fgBefore = GetForegroundWindow();
                ActivateWindow(targetHwnd);
                await Task.Delay(350);

                var fg = GetForegroundWindow();
                InjectDiag($"terminal: fgBefore=0x{fgBefore.ToInt64():X} fgAfterActivate=0x{fg.ToInt64():X} target=0x{targetHwnd.ToInt64():X}");
                if (fg != targetHwnd)
                {
                    _logger?.LogWarning(
                        "SendTextToTerminalAsync: foreground mismatch (target=0x{Tgt:X}, fg=0x{Fg:X}); aborting paste to avoid wrong window",
                        targetHwnd.ToInt64(), fg.ToInt64());
                    InjectDiag("terminal: -> foreground-mismatch (激活后目标不在前台，未粘贴)");
                    return new InjectResult(false, "foreground-mismatch");
                }

                SendCtrlV();
                InjectDiag("terminal: pasted (Ctrl+V 已发)");
                await Task.Delay(120);
                if (submit && !TrySubmitIfStillForeground(targetHwnd))
                {
                    InjectDiag("terminal: -> foreground-lost (粘贴后目标失焦，未回车提交)");
                    return new InjectResult(false, "foreground-lost");
                }
                if (submit) { InjectDiag("terminal: Enter 已发"); await Task.Delay(80); }

                InjectDiag("terminal: -> OK");
                return new InjectResult(true, null);
            }
            catch (Exception ex)
            {
                // 注入途中（激活/SendInput/粘贴）抛出 —— 兜住，绝不让它从 async 命令逃逸导致进程崩溃。
                _logger?.LogWarning(ex, "SendTextToTerminalAsync: injection threw");
                InjectDiag($"terminal: -> inject-error ({ex.GetType().Name}: {ex.Message})");
                return new InjectResult(false, "inject-error");
            }
            finally
            {
                // 等目标消费完粘贴再还原剪贴板（粘贴是异步投递，过早还原会粘到旧内容）。
                await Task.Delay(250);
                RestoreClipboardSafe(saved);
            }
        }
        finally
        {
            _injectGate.Release();
        }
    }

    /// <summary>把文字粘贴进 Claude Desktop 的聊天输入框（默认聚焦），可选回车提交。</summary>
    public async Task<InjectResult> SendTextToClaudeDesktopAsync(string text, bool submit)
    {
        InjectDiag($"desktop: enter textLen={text?.Length ?? 0} submit={submit}");
        await _injectGate.WaitAsync();
        try
        {
            if (!ActivateClaudeDesktopWindow(null))
            {
                InjectDiag("desktop: -> desktop-activate-failed");
                return new InjectResult(false, "desktop-activate-failed");
            }

            var winHwnd = FindClaudeDesktopMainHwnd();
            InjectDiag($"desktop: winHwnd=0x{winHwnd.ToInt64():X}");
            if (winHwnd == IntPtr.Zero)
                return new InjectResult(false, "no-desktop-window");

            var saved = GetClipboardTextSafe();
            if (!SetClipboardTextSafe(text!))
            {
                InjectDiag("desktop: -> clipboard-failed");
                return new InjectResult(false, "clipboard-failed");
            }

            try
            {
                await Task.Delay(200);

                var fg = GetForegroundWindow();
                InjectDiag($"desktop: fgAfterActivate=0x{fg.ToInt64():X} target=0x{winHwnd.ToInt64():X}");
                if (fg != winHwnd)
                {
                    _logger?.LogWarning(
                        "SendTextToClaudeDesktopAsync: foreground mismatch (target=0x{Tgt:X}, fg=0x{Fg:X}); aborting",
                        winHwnd.ToInt64(), fg.ToInt64());
                    InjectDiag("desktop: -> foreground-mismatch");
                    return new InjectResult(false, "foreground-mismatch");
                }

                // Claude Desktop 激活后聊天输入框默认聚焦，直接粘贴 + 回车。
                SendCtrlV();
                InjectDiag("desktop: pasted");
                await Task.Delay(120);
                if (submit && !TrySubmitIfStillForeground(winHwnd))
                {
                    InjectDiag("desktop: -> foreground-lost");
                    return new InjectResult(false, "foreground-lost");
                }
                if (submit) { InjectDiag("desktop: Enter 已发"); await Task.Delay(80); }

                InjectDiag("desktop: -> OK");
                return new InjectResult(true, null);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SendTextToClaudeDesktopAsync: injection threw");
                InjectDiag($"desktop: -> inject-error ({ex.GetType().Name}: {ex.Message})");
                return new InjectResult(false, "inject-error");
            }
            finally
            {
                await Task.Delay(250);
                RestoreClipboardSafe(saved);
            }
        }
        finally
        {
            _injectGate.Release();
        }
    }

    /// <summary>
    /// 提交前再确认目标仍在前台：粘贴后等待的 120ms 里可能被别的窗口抢走焦点，
    /// 此时绝不能把回车打到错误窗口（会误提交别处的内容）。仅在目标仍是前台时发回车。
    /// </summary>
    private bool TrySubmitIfStillForeground(IntPtr target)
    {
        var fg = GetForegroundWindow();
        if (fg != target)
        {
            _logger?.LogWarning(
                "submit aborted: foreground changed before Enter (target=0x{Tgt:X}, fg=0x{Fg:X})",
                target.ToInt64(), fg.ToInt64());
            return false;
        }
        SendVk(VK_RETURN);
        return true;
    }

    /// <summary>发一次 Ctrl+V 组合键：CTRL 按下 → V 按下 → V 抬起 → CTRL 抬起（一次投递）。</summary>
    private static void SendCtrlV()
    {
        var inputs = new INPUT[4];
        inputs[0].type = INPUT_KEYBOARD; inputs[0].u.ki.wVk = VK_CONTROL;
        inputs[1].type = INPUT_KEYBOARD; inputs[1].u.ki.wVk = VK_V;
        inputs[2].type = INPUT_KEYBOARD; inputs[2].u.ki.wVk = VK_V; inputs[2].u.ki.dwFlags = KEYEVENTF_KEYUP;
        inputs[3].type = INPUT_KEYBOARD; inputs[3].u.ki.wVk = VK_CONTROL; inputs[3].u.ki.dwFlags = KEYEVENTF_KEYUP;
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static string? GetClipboardTextSafe()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return null;
            return app.Dispatcher.Invoke(() =>
                System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null);
        }
        catch { return null; }
    }

    private static bool SetClipboardTextSafe(string text)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return false;
        // 剪贴板是全局独占资源：剪贴板历史(Win+V)/微信/输入法等会在每次变更后瞬间抢占，
        // SetText(=SetDataObject copy:true) 还要做 OleFlushClipboard —— 正是
        // CLIPBRD_E_CANT_OPEN 的高发点。改用 copy:false（不 flush，数据由本进程持有，
        // 我们随后立刻粘贴，无需为"进程退出后保留"付出 flush 代价），并把重试窗口拉长到
        // ~1.2s（16×75ms），足以骑过剪贴板监听者的短暂占用。
        return app.Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < 16; i++)
            {
                try { System.Windows.Clipboard.SetDataObject(text, false); return true; }
                catch { System.Threading.Thread.Sleep(75); }
            }
            return false;
        });
    }

    private static void RestoreClipboardSafe(string? saved)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return;
            app.Dispatcher.Invoke(() =>
            {
                // 还原也做几次重试（非致命，尽力而为）
                for (int i = 0; i < 4; i++)
                {
                    try
                    {
                        if (saved != null)
                            System.Windows.Clipboard.SetText(saved);
                        else
                            // 原内容非文本（图片/文件），SetText 时已被覆盖、无法完美还原；
                            // 至少清掉我们注入的文字，不让回复内容残留在用户剪贴板里。
                            System.Windows.Clipboard.Clear();
                        return;
                    }
                    catch { System.Threading.Thread.Sleep(50); }
                }
            });
        }
        catch { }
    }
}
