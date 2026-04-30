using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenIsland.Core.Models;

namespace OpenIsland.Core.Registry;

/// <summary>
/// 会话注册表 - 持久化存储会话记录
/// </summary>
public class SessionRegistry
{
    private readonly ILogger<SessionRegistry>? _logger;
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public SessionRegistry(string? filePath = null, ILogger<SessionRegistry>? logger = null)
    {
        _filePath = filePath ?? GetDefaultFilePath();
        _logger = logger;
    }

    private static string GetDefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "OpenIsland");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "session-registry.json");
    }

    /// <summary>
    /// 加载所有会话记录
    /// </summary>
    public async Task<List<TrackedSessionRecord>> LoadAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!File.Exists(_filePath))
            {
                _logger?.LogDebug("Registry file not found, returning empty list");
                return new List<TrackedSessionRecord>();
            }

            var json = await File.ReadAllTextAsync(_filePath, ct);
            var records = JsonSerializer.Deserialize<List<TrackedSessionRecord>>(json, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return records ?? new List<TrackedSessionRecord>();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load session registry");
            return new List<TrackedSessionRecord>();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 保存会话记录
    /// </summary>
    public async Task SaveAsync(List<TrackedSessionRecord> records, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 写入临时文件然后原子替换
            var tempFile = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(records, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            });

            await File.WriteAllTextAsync(tempFile, json, ct);
            File.Move(tempFile, _filePath, overwrite: true);

            _logger?.LogDebug("Saved {Count} sessions to registry", records.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save session registry");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 更新或添加会话记录
    /// </summary>
    public async Task UpsertAsync(AgentSession session, CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var existing = records.FirstOrDefault(r => r.SessionId == session.Id);

        if (existing != null)
        {
            records.Remove(existing);
        }

        records.Add(TrackedSessionRecord.FromSession(session));

        // 只保留最近的100条记录
        if (records.Count > 100)
        {
            records = records.OrderByDescending(r => r.UpdatedAt).Take(100).ToList();
        }

        await SaveAsync(records, ct);
    }

    /// <summary>
    /// 删除会话记录
    /// </summary>
    public async Task RemoveAsync(string sessionId, CancellationToken ct = default)
    {
        var records = await LoadAsync(ct);
        var existing = records.FirstOrDefault(r => r.SessionId == sessionId);

        if (existing != null)
        {
            records.Remove(existing);
            await SaveAsync(records, ct);
        }
    }
}

/// <summary>
/// 可追踪的会话记录
/// </summary>
public class TrackedSessionRecord
{
    public string SessionId { get; set; } = "";
    public string Title { get; set; } = "";
    public AgentTool Tool { get; set; }
    public SessionOrigin Origin { get; set; }
    public SessionAttachmentState AttachmentState { get; set; }
    public SessionPhase Phase { get; set; }
    public string Summary { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
    public bool IsRemote { get; set; }
    public JumpTarget? JumpTarget { get; set; }

    public static TrackedSessionRecord FromSession(AgentSession session)
    {
        return new TrackedSessionRecord
        {
            SessionId = session.Id,
            Title = session.Title,
            Tool = session.Tool,
            Origin = session.Origin,
            AttachmentState = session.AttachmentState,
            Phase = session.Phase,
            Summary = session.Summary,
            UpdatedAt = session.UpdatedAt,
            IsRemote = session.IsRemote,
            JumpTarget = session.JumpTarget
        };
    }

    public AgentSession ToSession()
    {
        return new AgentSession
        {
            Id = SessionId,
            Title = Title,
            Tool = Tool,
            Origin = Origin,
            AttachmentState = AttachmentState,
            Phase = Phase,
            Summary = Summary,
            UpdatedAt = UpdatedAt,
            IsRemote = IsRemote,
            JumpTarget = JumpTarget
        };
    }

    /// <summary>
    /// 创建可恢复到活跃状态的会话（附着状态重置为Stale）
    /// </summary>
    public AgentSession ToRestorableSession()
    {
        return new AgentSession
        {
            Id = SessionId,
            Title = Title,
            Tool = Tool,
            Origin = Origin,
            AttachmentState = SessionAttachmentState.Stale,
            Phase = Phase == SessionPhase.Completed ? SessionPhase.Idle : Phase,
            Summary = Summary,
            UpdatedAt = UpdatedAt,
            IsRemote = IsRemote,
            JumpTarget = JumpTarget
        };
    }

    /// <summary>
    /// 是否应该恢复到活跃状态
    /// </summary>
    public bool ShouldRestoreToLiveState => Origin != SessionOrigin.Demo;
}
