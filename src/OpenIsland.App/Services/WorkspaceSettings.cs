using System.IO;
using System.Text.Json;
using OpenIsland.Core;

namespace OpenIsland.App.Services;

/// <summary>
/// 工作区设置：用户在控制中心选定的目录列表，控制 Overview / Models 仅统计 cwd 落在
/// 这些目录下的 session（项目级筛选）。空列表 = 不筛选，等同于全量统计。
/// 持久化到 %APPDATA%\OpenIsland\settings.json。
/// </summary>
public class WorkspaceSettings
{
    private readonly string _path;

    public List<string> Workspaces { get; private set; } = new();

    /// <summary>
    /// Plan usage 行的 5 小时窗口 token 预算。0 = 未配置（PlanUsageService 走自适应历史峰值）。
    /// </summary>
    public ulong Plan5hTokenBudget { get; private set; }

    /// <summary>
    /// 灵动岛提示音总开关（任务完成 / 需关注的"叮"）。默认 true（开）。
    /// false = 全局静音，SoundService 据此 no-op。json key = "soundEnabled"。
    /// Master mute for the island's completion / attention chimes. Default true.
    /// </summary>
    public bool SoundEnabled { get; private set; } = true;

    /// <summary>用户在控制中心保存的第三方模型档案（含 API key）。内置 Claude 档不存这里。</summary>
    public List<ModelProfile> ModelProfiles { get; private set; } = new();

    /// <summary>当前活动模型档案 Id（全局；第三方写 env 对新 CLI 会话生效）。默认官方 Claude。</summary>
    public string ActiveModelProfileId { get; private set; } = ModelProfile.OfficialClaudeId;

    public event EventHandler? Changed;

    public WorkspaceSettings()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _path = Path.Combine(appData, "OpenIsland", "settings.json");
        Load();
    }

    /// <summary>整理 + 持久化 + 通知监听者。</summary>
    public void SetWorkspaces(IEnumerable<string> paths)
    {
        Workspaces = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置 Plan usage 5h token 预算 + 持久化 + 通知监听者。</summary>
    public void SetPlan5hTokenBudget(ulong v)
    {
        Plan5hTokenBudget = v;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置提示音总开关 + 持久化 + 通知监听者。</summary>
    public void SetSoundEnabled(bool v)
    {
        SoundEnabled = v;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>新增或更新一个第三方模型档案（按 Id）+ 持久化 + 通知。</summary>
    public void AddOrUpdateModelProfile(ModelProfile profile)
    {
        var idx = ModelProfiles.FindIndex(p => p.Id == profile.Id);
        if (idx >= 0) ModelProfiles[idx] = profile;
        else ModelProfiles.Add(profile);
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>删除一个第三方模型档案 + 持久化 + 通知；删的若是活动档则回落官方 Claude。</summary>
    public void RemoveModelProfile(string id)
    {
        ModelProfiles.RemoveAll(p => p.Id == id);
        if (ActiveModelProfileId == id) ActiveModelProfileId = ModelProfile.OfficialClaudeId;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>设置当前活动模型档案 Id + 持久化 + 通知。</summary>
    public void SetActiveModelProfile(string id)
    {
        ActiveModelProfileId = id;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// session.cwd 是否命中任一工作区。Workspaces 空 → 永远 true（不筛选）。
    /// </summary>
    public bool Matches(string? cwd)
    {
        if (Workspaces.Count == 0) return true;
        if (string.IsNullOrEmpty(cwd)) return false;
        var c = cwd.TrimEnd('\\', '/');
        foreach (var w in Workspaces)
        {
            if (c.StartsWith(w, StringComparison.OrdinalIgnoreCase)
                && (c.Length == w.Length || c[w.Length] is '\\' or '/'))
                return true;
        }
        return false;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("workspaces", out var ws)
                && ws.ValueKind == JsonValueKind.Array)
            {
                Workspaces = ws.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.TrimEnd('\\', '/'))
                    .ToList();
            }
            // plan5hTokenBudget 缺失 / 解析失败 → 保持默认 0
            if (doc.RootElement.TryGetProperty("plan5hTokenBudget", out var budget)
                && budget.ValueKind == JsonValueKind.Number
                && budget.TryGetUInt64(out var b))
            {
                Plan5hTokenBudget = b;
            }
            // soundEnabled 缺失 / 解析失败 → 保持默认 true（旧 settings.json 没这个 key
            // 时不静音，符合"默认开"语义）
            if (doc.RootElement.TryGetProperty("soundEnabled", out var snd)
                && (snd.ValueKind == JsonValueKind.True || snd.ValueKind == JsonValueKind.False))
            {
                SoundEnabled = snd.GetBoolean();
            }
            if (doc.RootElement.TryGetProperty("modelProfiles", out var mp)
                && mp.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    var loaded = mp.Deserialize<List<ModelProfile>>() ?? new();
                    // 解密落盘的 API key（旧明文向后兼容、解密失败置空，见 ApiKeyProtector）。
                    var result = new List<ModelProfile>(loaded.Count);
                    foreach (var p in loaded)
                        result.Add(string.IsNullOrEmpty(p.ApiKey)
                            ? p : p with { ApiKey = ApiKeyProtector.Unprotect(p.ApiKey) });
                    ModelProfiles = result;
                }
                catch { ModelProfiles = new(); }
            }
            if (doc.RootElement.TryGetProperty("activeModelProfileId", out var amp)
                && amp.ValueKind == JsonValueKind.String)
            {
                ActiveModelProfileId = amp.GetString() ?? ModelProfile.OfficialClaudeId;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WorkspaceSettings.Load failed: {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 三个 key 一起序列化 —— 写任一项都不丢另外两项
            // (workspaces / plan5hTokenBudget / soundEnabled 互不覆盖)
            // 落盘前把每个 profile 的明文 API key 用 DPAPI 加密（运行时内存里 ModelProfiles 仍是明文，不改）。
            var profilesForDisk = new List<ModelProfile>(ModelProfiles.Count);
            foreach (var p in ModelProfiles)
                profilesForDisk.Add(string.IsNullOrEmpty(p.ApiKey)
                    ? p : p with { ApiKey = ApiKeyProtector.Protect(p.ApiKey) });

            var payload = JsonSerializer.Serialize(
                new
                {
                    workspaces = Workspaces,
                    plan5hTokenBudget = Plan5hTokenBudget,
                    soundEnabled = SoundEnabled,
                    modelProfiles = profilesForDisk,
                    activeModelProfileId = ActiveModelProfileId
                },
                new JsonSerializerOptions { WriteIndented = true });
            // 原子写：先写 tmp 再替换，避免写到一半崩溃/断电截断文件、丢失全部模型配置（含 API key）。
            var tmp = _path + ".tmp." + System.Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, payload);
            if (File.Exists(_path)) File.Replace(tmp, _path, null);
            else File.Move(tmp, _path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WorkspaceSettings.Save failed: {ex.Message}");
        }
    }
}
