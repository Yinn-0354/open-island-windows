using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
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
        // bug⑤: 全程 ConfigureAwait(false)，让 await 续体不投回 WPF UI 线程 —— 注入链里的
        // 等待 / Win32 调用都与线程无关，只有真正需要 STA 的剪贴板存取才用最小范围
        // Dispatcher.Invoke 包住（见 Get/Set/RestoreClipboardSafe），避免每次发送卡 UI 1~2s。
        await _injectGate.WaitAsync().ConfigureAwait(false);
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

            var saved = GetClipboardSnapshotSafe();
            if (!await SetClipboardTextSafeAsync(text!).ConfigureAwait(false))
            {
                InjectDiag("terminal: -> clipboard-failed (写剪贴板失败)");
                return new InjectResult(false, "clipboard-failed");
            }

            try
            {
                var fgBefore = GetForegroundWindow();
                ActivateWindow(targetHwnd);
                await Task.Delay(350).ConfigureAwait(false);

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
                await Task.Delay(120).ConfigureAwait(false);
                if (submit && !TrySubmitIfStillForeground(targetHwnd))
                {
                    InjectDiag("terminal: -> foreground-lost (粘贴后目标失焦，未回车提交)");
                    return new InjectResult(false, "foreground-lost");
                }
                if (submit) { InjectDiag("terminal: Enter 已发"); await Task.Delay(80).ConfigureAwait(false); }

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
                await Task.Delay(250).ConfigureAwait(false);
                await RestoreClipboardSafe(saved).ConfigureAwait(false);
            }
        }
        finally
        {
            _injectGate.Release();
        }
    }

    /// <summary>
    /// 把文字粘贴进 Claude Desktop 目标会话的聊天输入框，可选回车提交。
    ///
    /// bug①：旧实现 ActivateClaudeDesktopWindow(null) 不导航，直接假设输入框聚焦就粘贴 +
    /// 回车，会把回复粘进 Claude Desktop *当前打开的会话* 而非目标会话。现在传 sessionTitle，
    /// 先用 UIA 侧边栏导航选中正确会话（同 JumpToSessionAsync 桌面分支）；**导航失败立即中止**
    /// （返回 "session-nav-failed"），绝不盲发进错误会话。
    ///
    /// bug⑤（线程模型）：激活 + UIA 侧边栏导航（含最多 8×250ms≈2s Thread.Sleep）+
    /// IsChatInputFocused（FocusedElement/TreeWalker）+ Win32 前台校验/Ctrl+V/Enter —— 这些
    /// **全部挪到线程池**（await Task.Run(...)），不在 WPF STA UI 线程跑。原因有二：
    ///   1) STA UI 线程上跑 UIA 会阻塞消息泵导致 COM 调用静默失败（项目已知陷阱，
    ///      对齐 SessionManager.cs:1037 既有的 Task.Run 做法）；
    ///   2) 2s 的 Thread.Sleep 不再压 UI 线程，消除发送时的界面卡顿。
    /// 线程亲和性：UIA（FindAll/FocusedElement/Invoke/SetFocus）和 Win32（SendInput/
    /// SetForegroundWindow/GetForegroundWindow）可在线程池线程跑；但剪贴板
    /// （Clipboard.Get/SetDataObject）是 **STA-only**，必须留在 app.Dispatcher.Invoke（UI/STA
    /// 线程）里 —— 故 Get/Set/RestoreClipboard 仍在 UI 上下文调用（其内部已 Dispatcher.Invoke
    /// 到 STA），绝不挪进 Task.Run（否则 COM 异常）。
    /// </summary>
    public async Task<InjectResult> SendTextToClaudeDesktopAsync(string text, bool submit, string? sessionTitle = null)
    {
        InjectDiag($"desktop: enter textLen={text?.Length ?? 0} submit={submit} title='{sessionTitle ?? "(null)"}'");
        // ConfigureAwait(false) —— 续体不强制回 UI 线程；UIA/Win32 段显式 Task.Run 挪线程池。
        await _injectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // ── 第 1 段：激活 + UIA 侧边栏导航（线程池）。剪贴板还没碰，UIA/Win32 全可在后台线程。
            // bug①: 透出 navigated —— 传了 sessionTitle 时导航失败必须中止。
            // bug⑤: Task.Run 把激活 + 2s 重试 UIA 导航推到线程池，避开 STA UIA 陷阱 + UI 卡顿。
            var nav = await Task.Run(() =>
            {
                bool activated = ActivateClaudeDesktopWindow(sessionTitle, out var navigated);
                var hwnd = activated ? FindClaudeDesktopMainHwnd() : IntPtr.Zero;
                return (activated, navigated, hwnd);
            }).ConfigureAwait(false);

            if (!nav.activated)
            {
                InjectDiag("desktop: -> desktop-activate-failed");
                return new InjectResult(false, "desktop-activate-failed");
            }

            // bug①: 传了 sessionTitle 却没导航成功 —— Claude Desktop 仍停在当前会话，其输入框
            // 照样聚焦、照样过 IsChatInputFocused，会把回复发进 *错误会话*。立即中止，不盲发。
            if (!string.IsNullOrEmpty(sessionTitle) && !nav.navigated)
            {
                _logger?.LogWarning(
                    "SendTextToClaudeDesktopAsync: sidebar navigation to session '{Title}' failed; aborting to avoid replying into the wrong session",
                    sessionTitle);
                InjectDiag("desktop: -> session-nav-failed (侧边栏没导航到目标会话，中止以免发进错误会话)");
                return new InjectResult(false, "session-nav-failed");
            }

            var winHwnd = nav.hwnd;
            InjectDiag($"desktop: winHwnd=0x{winHwnd.ToInt64():X} navigated={nav.navigated}");
            if (winHwnd == IntPtr.Zero)
            {
                InjectDiag("desktop: -> no-desktop-window");
                return new InjectResult(false, "no-desktop-window");
            }

            // ── 第 2 段：写剪贴板（STA-only，留在 UI 线程上下文；内部已 Dispatcher.Invoke 到 STA）。
            var saved = GetClipboardSnapshotSafe();
            if (!await SetClipboardTextSafeAsync(text!).ConfigureAwait(false))
            {
                InjectDiag("desktop: -> clipboard-failed");
                return new InjectResult(false, "clipboard-failed");
            }

            try
            {
                // ── 第 3 段：等待 + 前台校验 + 焦点校验 + 粘贴 + 回车（线程池）。
                // 这里只有 UIA（IsChatInputFocused）与 Win32（GetForegroundWindow/SendCtrlV/
                // SendVk）—— 均无线程亲和性，整段进 Task.Run，2s 之外的 350ms 等待也不压 UI。
                // 剪贴板已在第 2 段写好，本段不再碰剪贴板，故安全脱离 STA。
                var result = await Task.Run(() =>
                {
                    // 侧边栏导航后 React 切换会话 + 聊天输入框重新挂载需要时间，多等一会儿再校验聚焦。
                    System.Threading.Thread.Sleep(350);

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

                    // bug②: fg==winHwnd 只证明 Claude Desktop 在最前，证明不了插入符在聊天输入框。
                    // 焦点校验降为 *辅助*（导航成功已保证选中正确会话 + 前台已校验 + 提交前还有
                    // TrySubmitIfStillForeground 兜底）：IsChatInputFocused 失败 **只记日志不强拒**，
                    // 不把可用性赌在未实测的 ControlType 上（实际类型已写进日志，真机一次定位后再收紧）。
                    if (!IsChatInputFocused(winHwnd))
                    {
                        _logger?.LogInformation(
                            "SendTextToClaudeDesktopAsync: focus check did not confirm chat input (continuing — session identity already ensured by navigation)");
                        InjectDiag("desktop: focus-check soft-fail (焦点未确认为输入框，但导航已保证会话身份，继续粘贴)");
                    }

                    SendCtrlV();
                    InjectDiag("desktop: pasted");
                    System.Threading.Thread.Sleep(120);
                    if (submit && !TrySubmitIfStillForeground(winHwnd))
                    {
                        InjectDiag("desktop: -> foreground-lost");
                        return new InjectResult(false, "foreground-lost");
                    }
                    if (submit) { InjectDiag("desktop: Enter 已发"); System.Threading.Thread.Sleep(80); }

                    InjectDiag("desktop: -> OK");
                    return new InjectResult(true, null);
                }).ConfigureAwait(false);

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "SendTextToClaudeDesktopAsync: injection threw");
                InjectDiag($"desktop: -> inject-error ({ex.GetType().Name}: {ex.Message})");
                return new InjectResult(false, "inject-error");
            }
            finally
            {
                // 还原剪贴板（STA-only，留 UI 上下文）。等目标消费完粘贴再还原（粘贴是异步投递）。
                await Task.Delay(250).ConfigureAwait(false);
                await RestoreClipboardSafe(saved).ConfigureAwait(false);
            }
        }
        finally
        {
            _injectGate.Release();
        }
    }

    /// <summary>
    /// bug②: 校验 Claude Desktop 当前键盘焦点是否落在聊天输入框。
    ///
    /// 角色（bug①修好后）：导航成功已保证选中了正确会话 + 前台 HWND 已校验 + 提交前还有
    /// TrySubmitIfStillForeground 兜底，所以本检查 *降为辅助* —— 不再把可用性赌在"焦点必须是
    /// Edit/Document"这个 **未实测** 的假设上（若 Claude Desktop 富文本框实际报告为
    /// Group/Pane/Custom/Text，旧实现会让所有桌面回复永远 input-not-focused 全废）。
    ///
    /// 放宽后的判据（两条都满足才算"已聚焦"，否则返回 false 让调用方按"软失败"处理）：
    ///   (a) 焦点元素属于目标主窗口（winHwnd）子树 —— 沿 NativeWindowHandle 找宿主顶层窗口比对，
    ///       防止焦点跑到别的窗口（这条是 *硬* 防线，保留）；
    ///   (b) ControlType ∈ 放宽集合 {Edit, Document, Text, Group, Pane, Custom} —— 覆盖
    ///       Claude Desktop 富文本框在 UIA 树里可能报告的各种形态。
    /// 无论命中与否，都把实际 FocusedElement 的 ControlType 写进 %TEMP%\openisland-inject.log，
    /// 真机跑一次即可定位聊天框的真实 ControlType，再据此收紧（不继续盲赌 Edit/Document）。
    /// 任何 UIA 调用抛错 / 拿不到 FocusedElement → 返回 false（由调用方决定软/硬处理）。
    /// </summary>
    private bool IsChatInputFocused(IntPtr winHwnd)
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused == null)
            {
                InjectDiag("desktop: focus-check no FocusedElement");
                return false;
            }

            ControlType ct;
            try { ct = focused.Current.ControlType; }
            catch { InjectDiag("desktop: focus-check ControlType threw"); return false; }

            // 焦点元素须在目标主窗口子树内：沿 NativeWindowHandle 找其宿主顶层窗口，与 winHwnd 比对。
            // contenteditable 内部元素往往不直接挂句柄，故向上找最近带句柄的祖先作为窗口归属判据。
            // 这条是硬防线（焦点跑到别的窗口绝不可粘），放在 ControlType 判断之前先排除。
            if (winHwnd != IntPtr.Zero)
            {
                var rootHwnd = TryGetHostWindowHandle(focused);
                if (rootHwnd != IntPtr.Zero && rootHwnd != winHwnd)
                {
                    InjectDiag($"desktop: focus-check ct={ct.ProgrammaticName} hostHwnd=0x{rootHwnd.ToInt64():X} != target=0x{winHwnd.ToInt64():X} (wrong window)");
                    return false;
                }
            }

            // bug②: 放宽 ControlType 集合（不再只认 Edit/Document）。实际类型未实测，多认几种富文本
            // 框常见形态，避免过严把全部桌面回复废掉。无论结果如何都记 ct 以便真机一次定位。
            bool editable = ct == ControlType.Edit
                || ct == ControlType.Document
                || ct == ControlType.Text
                || ct == ControlType.Group
                || ct == ControlType.Pane
                || ct == ControlType.Custom;
            if (!editable)
            {
                InjectDiag($"desktop: focus-check ct={ct.ProgrammaticName} not in relaxed editable set");
                return false;
            }

            InjectDiag($"desktop: focus-check OK ct={ct.ProgrammaticName}");
            return true;
        }
        catch (Exception ex)
        {
            InjectDiag($"desktop: focus-check threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>沿 UIA 祖先链找最近一个带原生窗口句柄的元素，返回该句柄（找不到返回 Zero）。</summary>
    private static IntPtr TryGetHostWindowHandle(AutomationElement el)
    {
        try
        {
            var walker = TreeWalker.ControlViewWalker;
            for (var node = el; node != null; node = walker.GetParent(node))
            {
                int h;
                try { h = node.Current.NativeWindowHandle; }
                catch { continue; }
                if (h != 0)
                {
                    var hwnd = new IntPtr(h);
                    // contenteditable 子窗口属 Chrome_WidgetWin_1，与主窗口同句柄；若不是顶层，
                    // GetAncestor(GA_ROOT) 归一到顶层窗口再比对。
                    var root = GetAncestor(hwnd, GA_ROOT);
                    return root != IntPtr.Zero ? root : hwnd;
                }
            }
        }
        catch { }
        return IntPtr.Zero;
    }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

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

    // bug③: 完整捕获原始剪贴板内容（图片/文件/富文本，不止纯文本）。
    //
    // 旧实现 GetClipboardTextSafe 只存文本，原内容是图片/文件时 saved=null →
    // RestoreClipboardSafe 走 Clipboard.Clear() 清掉用户原内容，造成数据丢失。现在用
    // Clipboard.GetDataObject() 整体抓取原始 IDataObject，把它的所有格式拷进一个本进程持有
    // 的 DataObject 快照，还原时整体回写 —— 图片/文件/富文本都能原样复原。
    //
    // 注意：GetDataObject() 返回的 COM 包装对象只在本次剪贴板会话内有效（剪贴板再被改写后
    // 取它的数据会失败），所以这里立刻把各格式的数据 copy 进自管 DataObject 留存。
    private static System.Windows.IDataObject? GetClipboardSnapshotSafe()
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return null;
            return app.Dispatcher.Invoke(() =>
            {
                try
                {
                    var src = System.Windows.Clipboard.GetDataObject();
                    if (src == null) return null;
                    var snapshot = new System.Windows.DataObject();
                    bool any = false;
                    foreach (var fmt in src.GetFormats())
                    {
                        try
                        {
                            var data = src.GetData(fmt);
                            if (data != null) { snapshot.SetData(fmt, data); any = true; }
                        }
                        catch { /* 个别格式取不出（如延迟渲染），跳过，尽力保留其余格式 */ }
                    }
                    return any ? snapshot : null;
                }
                catch { return null; }
            });
        }
        catch { return null; }
    }

    private async Task<bool> SetClipboardTextSafeAsync(string text)
    {
        var app = System.Windows.Application.Current;
        if (app?.Dispatcher == null) return false;
        // 剪贴板是全局独占资源：剪贴板历史(Win+V)/微信/输入法等会在每次变更后瞬间抢占，
        // SetText(=SetDataObject copy:true) 还要做 OleFlushClipboard —— 正是
        // CLIPBRD_E_CANT_OPEN 的高发点。改用 copy:false（不 flush，数据由本进程持有，
        // 我们随后立刻粘贴，无需为"进程退出后保留"付出 flush 代价）。重试窗口仍 ~1.2s（16×75ms），
        // 足以骑过剪贴板监听者的短暂占用。
        //
        // bug⑤: 不再把 16×75ms 的 Thread.Sleep 整块塞进一个 Dispatcher.Invoke（那会阻塞 UI
        // 线程 1~2s）。改为每次尝试只把真正需 STA 的 SetDataObject 用最小 Dispatcher.Invoke 包住，
        // 失败后的等待用 await Task.Delay（ConfigureAwait(false)，在后台线程上等），sleep 不落 UI。
        for (int i = 0; i < 16; i++)
        {
            bool ok = app.Dispatcher.Invoke(() =>
            {
                try { System.Windows.Clipboard.SetDataObject(text, false); return true; }
                catch { return false; }
            });
            if (ok) return true;
            await Task.Delay(75).ConfigureAwait(false);
        }
        return false;
    }

    // bug③+⑤: 还原原始剪贴板快照（整体回写 IDataObject），重试次数/时长与写入路径对齐
    // （16×75ms），sleep 走非阻塞 await Task.Delay 不压 UI 线程。
    private async Task RestoreClipboardSafe(System.Windows.IDataObject? saved)
    {
        try
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return;
            for (int i = 0; i < 16; i++)
            {
                bool done = app.Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (saved != null)
                            // 整体回写原始数据对象（图片/文件/富文本原样复原）。copy:true 让数据在
                            // 本进程退出后仍保留在剪贴板（还原是终态，值得为持久性付 flush 代价）。
                            System.Windows.Clipboard.SetDataObject(saved, true);
                        else
                            // 原本剪贴板为空（GetDataObject 返回 null）—— 清掉我们注入的文字，
                            // 不让回复内容残留在用户剪贴板里。
                            System.Windows.Clipboard.Clear();
                        return true;
                    }
                    catch { return false; }
                });
                if (done) return;
                await Task.Delay(75).ConfigureAwait(false);
            }
        }
        catch { }
    }
}
