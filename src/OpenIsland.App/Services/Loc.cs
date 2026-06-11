using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;

namespace OpenIsland.App.Services;

/// <summary>
/// 轻量界面本地化（中/英）。
///
/// 用法：
///   · XAML：Text="{Binding [Island_SwitchModel], Source={x:Static svc:Loc.Instance}}"
///     —— 切换语言时 PropertyChanged("Item[]") 让所有 indexer 绑定实时重读，无需重启。
///   · 代码：Loc.Get("key") 或 Loc.Format("key", args)；订阅 LanguageChanged 重建动态文案。
///
/// 语言来源：WorkspaceSettings.Language（"auto"/"zh"/"en"）。auto = 跟随 Windows 显示语言
/// （CurrentUICulture 是 zh 则中文，否则英文）。
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>语言切换后触发，供代码侧（托盘菜单 / VM 动态文案）重建。</summary>
    public event System.Action? LanguageChanged;

    private string _eff = "zh"; // 生效语言 "zh" | "en"

    /// <summary>当前生效语言（"zh"/"en"）。</summary>
    public string Effective => _eff;
    public bool IsEnglish => _eff == "en";

    /// <summary>XAML indexer 绑定入口。</summary>
    public string this[string key] => Get(key);

    /// <summary>启动时设定语言（不触发事件，窗口随后首次绑定即读到正确值）。</summary>
    public void Init(string setting) => _eff = Resolve(setting);

    /// <summary>运行时切换语言：更新生效语言并通知所有绑定 / 监听者实时刷新。</summary>
    public void Apply(string setting)
    {
        var eff = Resolve(setting);
        if (eff == _eff) return;
        _eff = eff;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Effective)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));
        LanguageChanged?.Invoke();
    }

    private static string Resolve(string? setting) => setting switch
    {
        "zh" => "zh",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("zh", System.StringComparison.OrdinalIgnoreCase) ? "zh" : "en"
    };

    public static string Get(string key)
    {
        var table = Instance._eff == "en" ? En : Zh;
        if (table.TryGetValue(key, out var v)) return v;
        return Zh.TryGetValue(key, out var z) ? z : key;
    }

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);

    // ── 中文表 ──────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, string> Zh = new()
    {
        // 托盘
        ["Tray_Status"] = "状态",
        ["Tray_Counts"] = "  运行中: {0}, 需关注: {1}",
        ["Tray_ShowIsland"] = "显示灵动岛",
        ["Tray_Open"] = "打开控制中心",
        ["Tray_Settings"] = "设置...",
        ["Tray_Language"] = "语言",
        ["Lang_Auto"] = "跟随系统",
        ["Lang_Zh"] = "中文",
        ["Lang_En"] = "English",
        ["Tray_Exit"] = "退出",
        ["Tip_Attention"] = "Open Island - {0} 个会话需要关注",
        ["Tip_Running"] = "Open Island - {0} 个会话运行中",
        ["Tip_Ready"] = "Open Island - 就绪",
        // 设置 / 控制中心
        ["Settings_Title"] = "Open Island · 设置",
        ["Settings_Language"] = "语言",
        ["Settings_Language_Desc"] = "切换界面显示语言。默认跟随 Windows 系统语言。",
        ["Settings_Screenshot"] = "区域截图",
        ["Settings_Screenshot_Desc"] = "全局快捷键触发区域截图：拖拽框选一个矩形，松手后自动复制到剪贴板，可直接粘贴。",
        ["Settings_Screenshot_Hotkey"] = "截图快捷键",
        ["Settings_Screenshot_Press"] = "请按下组合键…",
        ["Settings_Screenshot_NeedMod"] = "需含 Ctrl/Alt/Shift/Win",
        ["Settings_Glass"] = "毛玻璃效果",
        ["Settings_Glass_Desc"] = "仿 Apple 毛玻璃：灵动岛背景实时模糊透出后面的内容。不透明度越低越透。开启后立即生效并记住。",
        ["Settings_Glass_Enable"] = "启用毛玻璃",
        ["Settings_Glass_Opacity"] = "背景不透明度",
        ["Settings_Workspaces"] = "工作区目录",
        ["Settings_Workspaces_Desc"] = "选定一个或多个项目根目录。Overview / Models 仅统计 cwd 落在这些目录下的会话。空列表 = 不过滤，全量统计。",
        ["Settings_AddDir"] = "添加目录…",
        ["Settings_RemoveSel"] = "移除选中",
        ["Settings_Models"] = "模型 / Providers",
        ["Settings_Models_Desc"] = "添加第三方模型后，可在灵动岛的会话卡上切换。第三方模型写入 ~/.claude/settings.json，重开终端后生效；官方 Claude / Opus / Sonnet / Haiku 已内置，无需在此添加。",
        ["Settings_DeleteModel"] = "删除选中模型",
        ["Settings_AddModel"] = "添加模型",
        ["Settings_Preset"] = "预设",
        ["Settings_Name"] = "名称",
        ["Settings_Address"] = "地址",
        ["Settings_Model"] = "模型",
        ["Settings_ApiKey"] = "API Key",
        ["Settings_AddSave"] = "添加 / 保存模型",
        ["Settings_Cancel"] = "取消",
        ["Settings_Save"] = "保存",
        ["Settings_FillRequired"] = "请填写名称、地址和 API Key。",
        // 灵动岛
        ["Island_SwitchModel"] = "切换模型",
        ["Island_ClearTasks"] = "清理任务",
        ["Island_ClearTasks_Tip"] = "清理任务卡片（保留正在运行的会话）",
        ["Island_Screenshot_Tip"] = "区域截图：拖拽框选 → 自动复制到剪贴板。快捷键在设置中心更改（默认 Ctrl+Q）",
        ["Island_InstallSkill"] = "安装 Skill",
        ["Island_InstallSkill_Tip"] = "安装 Skill：粘贴 claude plugin 命令或 owner/repo，后台自动安装",
        ["Island_InstallSkill_Go"] = "安装",
        ["Skill_Installing"] = "安装中…",
        ["Skill_Done"] = "✅ 安装完成（新开 Claude 终端生效）",
        ["Skill_Failed"] = "❌ 失败：{0}",
        ["Skill_Invalid"] = "无法识别，请粘贴 claude plugin 命令或 owner/repo",
        ["Island_UsageToggle_Tip"] = "点击切换：5h 余额 ↔ 最近七天 token 用量柱状图",
        ["Island_Refresh_Tip"] = "立即刷新 5 小时余额",
        ["Island_Reply_Tip"] = "快捷回复（敲字回车发送）",
        ["Island_Reply_Placeholder"] = "回复… 回车发送",
        ["Island_Send_Tip"] = "发送（回车）",
        ["Island_Dismiss_Tip"] = "暂时收起（再次活动时自动出现）",
        ["Island_Pin_Tip"] = "图钉固定（清理任务时不会被清掉）",
        ["Island_Sound_Tip"] = "提示音开 / 关",
        ["Island_Prev"] = "上一首",
        ["Island_PlayPause"] = "播放 / 暂停",
        ["Island_Next"] = "下一首",
        ["Island_Volume"] = "系统音量",
        ["Mem_Release_Tip"] = "点击释放内存（清理各进程工作集）",
        ["Mem_Released"] = "已释放约 {0} MB 内存",
        // 5h 余额（VM 动态拼接）
        ["Balance_Format"] = "余 {0}%",
        ["Balance_Unknown"] = "余 --",
        ["Reset_Format"] = "重置 {0}h{1:00}m",
        // 模型切换状态（MapModelReason）
        ["Model_Switching"] = "切换中…",
        ["Model_SwitchedOfficial"] = "已切到官方 Claude（新会话生效）",
        ["Model_NeedsRestart"] = "已写入，重开终端后生效",
        ["Model_NoKey"] = "该模型未配置 API Key",
        ["Model_WriteFailed"] = "写 settings.json 失败",
        ["Model_Switched"] = "已切换",
        ["Model_SwitchFailed"] = "切换失败",
        ["Model_SwitchError"] = "切换出错",
        // 快捷回复状态
        ["Reply_Sending"] = "发送中…",
        ["Reply_Sent"] = "已发送",
        ["Reply_Empty"] = "请输入内容",
        ["Reply_TooLong"] = "内容过长",
        ["Reply_NoSession"] = "会话不存在",
        ["Reply_NoTerminal"] = "没找到该会话的终端",
        ["Reply_ForegroundMismatch"] = "没能切到目标窗口，已取消",
        ["Reply_ForegroundLost"] = "目标窗口失焦，已粘贴未提交",
        ["Reply_InjectError"] = "注入出错，已取消",
        ["Reply_DesktopActivateFailed"] = "没能激活 Claude Desktop",
        ["Reply_ClipboardFailed"] = "剪贴板被占用，请重试",
        ["Reply_SendFailed"] = "发送失败",
        ["Reply_SendError"] = "发送出错",
        // 网页同步（头部地球按钮）
        ["Web_Off_Tip"] = "网页同步：手机/平板实时查看并回复 CLI 与客户端会话（点击开启，本机局域网可访问）",
        ["Web_On"] = "已开启 {0} —— 链接已复制到剪贴板，点击关闭",
        ["Island_DotSync_Tip"] = "同步此对话到网页：置顶 + 完整历史（再点一次取消）",
    };

    // ── 英文表 ──────────────────────────────────────────────────────────────
    private static readonly Dictionary<string, string> En = new()
    {
        // Tray
        ["Tray_Status"] = "Status",
        ["Tray_Counts"] = "  Running: {0}, Attention: {1}",
        ["Tray_ShowIsland"] = "Show Island",
        ["Tray_Open"] = "Open Control Center",
        ["Tray_Settings"] = "Settings...",
        ["Tray_Language"] = "Language",
        ["Lang_Auto"] = "System default",
        ["Lang_Zh"] = "中文",
        ["Lang_En"] = "English",
        ["Tray_Exit"] = "Exit",
        ["Tip_Attention"] = "Open Island - {0} session(s) need attention",
        ["Tip_Running"] = "Open Island - {0} session(s) running",
        ["Tip_Ready"] = "Open Island - Ready",
        // Settings / Control Center
        ["Settings_Title"] = "Open Island · Settings",
        ["Settings_Language"] = "Language",
        ["Settings_Language_Desc"] = "Switch the interface display language. Defaults to the Windows system language.",
        ["Settings_Screenshot"] = "Region Screenshot",
        ["Settings_Screenshot_Desc"] = "Global hotkey for region capture: drag a rectangle, release to copy it to the clipboard, then paste anywhere.",
        ["Settings_Screenshot_Hotkey"] = "Capture hotkey",
        ["Settings_Screenshot_Press"] = "Press a key combo…",
        ["Settings_Screenshot_NeedMod"] = "Needs Ctrl/Alt/Shift/Win",
        ["Settings_Glass"] = "Acrylic Glass",
        ["Settings_Glass_Desc"] = "Apple-style acrylic: the island background blurs whatever is behind it. Lower opacity = more transparent. Applies instantly and is remembered.",
        ["Settings_Glass_Enable"] = "Enable acrylic",
        ["Settings_Glass_Opacity"] = "Background opacity",
        ["Settings_Workspaces"] = "Workspace Folders",
        ["Settings_Workspaces_Desc"] = "Pick one or more project root folders. Overview / Models only count sessions whose cwd is under these folders. Empty list = no filter, count everything.",
        ["Settings_AddDir"] = "Add folder…",
        ["Settings_RemoveSel"] = "Remove selected",
        ["Settings_Models"] = "Models / Providers",
        ["Settings_Models_Desc"] = "After adding a third-party model you can switch to it from a session card on the island. Third-party models are written to ~/.claude/settings.json and take effect in a new terminal. Official Claude / Opus / Sonnet / Haiku are built in — no need to add them here.",
        ["Settings_DeleteModel"] = "Delete selected model",
        ["Settings_AddModel"] = "Add Model",
        ["Settings_Preset"] = "Preset",
        ["Settings_Name"] = "Name",
        ["Settings_Address"] = "Base URL",
        ["Settings_Model"] = "Model",
        ["Settings_ApiKey"] = "API Key",
        ["Settings_AddSave"] = "Add / Save Model",
        ["Settings_Cancel"] = "Cancel",
        ["Settings_Save"] = "Save",
        ["Settings_FillRequired"] = "Please fill in Name, Base URL and API Key.",
        // Island
        ["Island_SwitchModel"] = "Switch Model",
        ["Island_ClearTasks"] = "Clear Tasks",
        ["Island_ClearTasks_Tip"] = "Clear task cards (keep running sessions)",
        ["Island_Screenshot_Tip"] = "Region screenshot: drag to select → auto-copied to clipboard. Change the hotkey in Settings (default Ctrl+Q)",
        ["Island_InstallSkill"] = "Install Skill",
        ["Island_InstallSkill_Tip"] = "Install a skill: paste claude plugin commands or owner/repo; installs in the background",
        ["Island_InstallSkill_Go"] = "Install",
        ["Skill_Installing"] = "Installing…",
        ["Skill_Done"] = "✅ Installed (takes effect in a new Claude terminal)",
        ["Skill_Failed"] = "❌ Failed: {0}",
        ["Skill_Invalid"] = "Unrecognized — paste claude plugin commands or owner/repo",
        ["Island_UsageToggle_Tip"] = "Click to toggle: 5h balance ↔ last 7 days token usage chart",
        ["Island_Refresh_Tip"] = "Refresh 5-hour balance now",
        ["Island_Reply_Tip"] = "Quick reply (type, Enter to send)",
        ["Island_Reply_Placeholder"] = "Reply… Enter to send",
        ["Island_Send_Tip"] = "Send (Enter)",
        ["Island_Dismiss_Tip"] = "Dismiss for now (reappears on activity)",
        ["Island_Pin_Tip"] = "Pin (won't be removed by Clear Tasks)",
        ["Island_Sound_Tip"] = "Sound on / off",
        ["Island_Prev"] = "Previous",
        ["Island_PlayPause"] = "Play / Pause",
        ["Island_Next"] = "Next",
        ["Island_Volume"] = "System volume",
        ["Mem_Release_Tip"] = "Click to free memory (trim process working sets)",
        ["Mem_Released"] = "Freed ~{0} MB",
        // 5h balance
        ["Balance_Format"] = "{0}% left",
        ["Balance_Unknown"] = "-- left",
        ["Reset_Format"] = "reset {0}h{1:00}m",
        // Model switch status
        ["Model_Switching"] = "Switching…",
        ["Model_SwitchedOfficial"] = "Switched to official Claude (new session)",
        ["Model_NeedsRestart"] = "Saved — takes effect in a new terminal",
        ["Model_NoKey"] = "No API key configured for this model",
        ["Model_WriteFailed"] = "Failed to write settings.json",
        ["Model_Switched"] = "Switched",
        ["Model_SwitchFailed"] = "Switch failed",
        ["Model_SwitchError"] = "Switch error",
        // Quick reply status
        ["Reply_Sending"] = "Sending…",
        ["Reply_Sent"] = "Sent",
        ["Reply_Empty"] = "Enter some text",
        ["Reply_TooLong"] = "Too long",
        ["Reply_NoSession"] = "Session not found",
        ["Reply_NoTerminal"] = "Terminal for this session not found",
        ["Reply_ForegroundMismatch"] = "Could not focus the target window; canceled",
        ["Reply_ForegroundLost"] = "Target window lost focus; pasted but not submitted",
        ["Reply_InjectError"] = "Injection error; canceled",
        ["Reply_DesktopActivateFailed"] = "Could not activate Claude Desktop",
        ["Reply_ClipboardFailed"] = "Clipboard busy, please retry",
        ["Reply_SendFailed"] = "Send failed",
        ["Reply_SendError"] = "Send error",
        // Web sync (globe button in the header)
        ["Web_Off_Tip"] = "Web sync: view & reply to CLI / desktop sessions from your phone or tablet (click to start; LAN access)",
        ["Web_On"] = "Running at {0} — URL copied to clipboard; click to stop",
        ["Island_DotSync_Tip"] = "Pin this conversation on the web page: top spot + longer history (click again to unpin)",
    };
}
