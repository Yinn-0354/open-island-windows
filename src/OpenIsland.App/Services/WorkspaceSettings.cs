using System.IO;
using System.Text.Json;

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
            var payload = JsonSerializer.Serialize(
                new { workspaces = Workspaces },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, payload);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WorkspaceSettings.Save failed: {ex.Message}");
        }
    }
}
