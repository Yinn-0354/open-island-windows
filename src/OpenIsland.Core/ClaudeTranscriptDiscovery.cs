using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using OpenIsland.Core.Models;

namespace OpenIsland.Core;

/// <summary>
/// Claude Code 会话日志扫描器 - 参考 cc-switch 实现
/// 扫描 ~/.claude/projects/**/*.jsonl 文件获取会话和用量信息
/// </summary>
public class ClaudeTranscriptDiscovery
{
    private readonly ILogger<ClaudeTranscriptDiscovery>? _logger;
    private readonly string _claudeDir;
    private readonly Dictionary<string, SyncState> _syncStates = new();

    // 模型定价（每1K tokens的美元价格）
    private static readonly Dictionary<string, ModelPricing> ModelPricings = new()
    {
        ["claude-opus-4"] = new ModelPricing { InputPrice = 15.0m, OutputPrice = 75.0m, CacheReadPrice = 1.5m, CacheWritePrice = 18.75m },
        ["claude-opus-4-6"] = new ModelPricing { InputPrice = 15.0m, OutputPrice = 75.0m, CacheReadPrice = 1.5m, CacheWritePrice = 18.75m },
        ["claude-sonnet-4"] = new ModelPricing { InputPrice = 3.0m, OutputPrice = 15.0m, CacheReadPrice = 0.3m, CacheWritePrice = 3.75m },
        ["claude-sonnet-4-6"] = new ModelPricing { InputPrice = 3.0m, OutputPrice = 15.0m, CacheReadPrice = 0.3m, CacheWritePrice = 3.75m },
        ["claude-haiku-4"] = new ModelPricing { InputPrice = 0.8m, OutputPrice = 4.0m, CacheReadPrice = 0.08m, CacheWritePrice = 1.0m },
        ["claude-3-5-sonnet"] = new ModelPricing { InputPrice = 3.0m, OutputPrice = 15.0m, CacheReadPrice = 0.3m, CacheWritePrice = 3.75m },
        ["claude-3-opus"] = new ModelPricing { InputPrice = 15.0m, OutputPrice = 75.0m, CacheReadPrice = 1.5m, CacheWritePrice = 18.75m },
        ["claude-3-sonnet"] = new ModelPricing { InputPrice = 3.0m, OutputPrice = 15.0m, CacheReadPrice = 0.3m, CacheWritePrice = 3.75m },
        ["claude-3-haiku"] = new ModelPricing { InputPrice = 0.25m, OutputPrice = 1.25m, CacheReadPrice = 0.025m, CacheWritePrice = 0.3m },
    };

    public ClaudeTranscriptDiscovery(ILogger<ClaudeTranscriptDiscovery>? logger = null)
        : this(GetClaudeConfigDir(), logger)
    {
    }

    /// <summary>
    /// 指定自定义的 .claude 根目录（仅用于测试 / 烟雾测试场景）。
    /// 生产代码总是用无参构造，会落到 %USERPROFILE%\.claude。
    /// </summary>
    public ClaudeTranscriptDiscovery(string claudeDir, ILogger<ClaudeTranscriptDiscovery>? logger = null)
    {
        _logger = logger;
        _claudeDir = claudeDir;
    }

    private static string GetClaudeConfigDir()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude");
    }

    /// <summary>
    /// 扫描所有会话并返回增量更新
    /// </summary>
    public async Task<List<ClaudeSessionInfo>> ScanSessionsAsync(CancellationToken ct = default)
    {
        var sessions = new List<ClaudeSessionInfo>();
        var projectsDir = Path.Combine(_claudeDir, "projects");

        if (!Directory.Exists(projectsDir))
        {
            _logger?.LogDebug("Claude projects directory not found: {Path}", projectsDir);
            return sessions;
        }

        // 递归查找所有 .jsonl 文件
        var jsonlFiles = Directory.GetFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories);

        foreach (var filePath in jsonlFiles)
        {
            try
            {
                // 排除 agent 会话
                if (IsAgentSession(filePath))
                    continue;

                var session = await ParseSessionFileAsync(filePath, ct);
                if (session != null)
                {
                    sessions.Add(session);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to parse session file: {File}", filePath);
            }
        }

        return sessions;
    }

    /// <summary>
    /// Claude projects directory: %USERPROFILE%\.claude\projects
    /// </summary>
    public string ProjectsDirectory => Path.Combine(_claudeDir, "projects");

    /// <summary>
    /// 排除 agent 会话（通常文件名以 agent- 开头）
    /// </summary>
    public static bool IsAgentSession(string filePath)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        return fileName.StartsWith("agent-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析单个会话文件（增量模式）
    ///
    /// 偏移量语义：<see cref="SyncState.LastLineOffset"/> 是已完整消费过的字节偏移，
    /// 仅在某行以 '\n' 结尾被读完后才前进。这样在 watcher 场景下，半截行（claude.exe
    /// 还在 flush 中）会被搁置到下次调用，不会被错误的当成 JSON 解析失败丢弃。
    /// </summary>
    public async Task<ClaudeSessionInfo?> ParseSessionFileAsync(string filePath, CancellationToken ct = default)
    {
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            return null;

        // 检查同步状态，避免重复处理
        _syncStates.TryGetValue(filePath, out var state);
        if (state != null
            && state.LastModified >= fileInfo.LastWriteTimeUtc
            && state.FileSize == fileInfo.Length
            && state.CachedSession != null)
        {
            // 文件未变化，返回缓存
            return state.CachedSession;
        }

        // 在 watcher 增量模式下沿用上次的累积状态；ScanSessionsAsync 启动时
        // 缓存里已经有完整 session，因此走的是上面的快路径。
        var session = state?.CachedSession ?? new ClaudeSessionInfo
        {
            SourcePath = filePath,
            SessionId = Path.GetFileNameWithoutExtension(filePath)
        };

        var messages = session.Messages.Count > 0 ? new List<ClaudeMessageInfo>(session.Messages) : new List<ClaudeMessageInfo>();
        ClaudeUsageInfo currentUsage = state?.CachedUsage ?? new ClaudeUsageInfo();
        long lastOffset = state?.LastLineOffset ?? 0;

        // 文件被截断或回缩：从头开始
        if (lastOffset > fileInfo.Length)
        {
            messages.Clear();
            currentUsage = new ClaudeUsageInfo();
            lastOffset = 0;
            session = new ClaudeSessionInfo
            {
                SourcePath = filePath,
                SessionId = Path.GetFileNameWithoutExtension(filePath)
            };
        }

        long newOffset = lastOffset;
        // 直接在字节层面扫描 \n（0x0A）。UTF-8 的多字节延续位都是 0x80-0xBF，
        // 0x0A 只出现在真正的换行处，所以按字节切分是安全的。
        // 这样 newOffset 永远是"下一个未消费字节"的绝对偏移，watcher 增量调用之间精确续传。
        await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Seek(lastOffset, SeekOrigin.Begin);
            var byteBuf = new byte[8192];
            var pending = new List<byte>(capacity: 1024);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(byteBuf.AsMemory(0, byteBuf.Length), ct);
                if (read == 0) break;

                for (int i = 0; i < read; i++)
                {
                    var b = byteBuf[i];
                    if (b == 0x0A) // '\n'
                    {
                        // 处理一行：去掉末尾的 '\r'
                        if (pending.Count > 0 && pending[^1] == 0x0D) pending.RemoveAt(pending.Count - 1);
                        if (pending.Count > 0)
                        {
                            var line = System.Text.Encoding.UTF8.GetString(pending.ToArray());
                            ApplyLine(line, session, messages, currentUsage, filePath);
                        }
                        pending.Clear();
                        // 已消费到 '\n' 之后的位置：流当前位置 - 当前批中尚未处理的剩余字节
                        newOffset = stream.Position - (read - i - 1);
                    }
                    else
                    {
                        pending.Add(b);
                    }
                }
            }
            // 残留 pending（尾行没有 \n）保持不动；newOffset 停在最后一个 \n 之后。
        }

        // 重算派生字段（基于完整 messages 集合）
        session.Messages = messages;
        session.Usage = currentUsage;
        session.TotalCost = CalculateCost(session.Usage, session.Model);

        session.Title = session.CustomTitle
            ?? FindFirstMeaningfulUserContent(messages)?.Truncate(60)
            ?? session.ProjectName
            ?? session.SessionId;

        var lastAssistantMsg = messages.LastOrDefault(m => m.Role == "assistant");
        session.Summary = lastAssistantMsg?.Content.Truncate(100) ?? "New session";

        _syncStates[filePath] = new SyncState
        {
            LastModified = fileInfo.LastWriteTimeUtc,
            FileSize = fileInfo.Length,
            LastLineOffset = newOffset,
            CachedSession = session,
            CachedUsage = currentUsage
        };

        return session;
    }

    /// <summary>
    /// 解析单条 JSONL 行并把状态写入 session/messages/usage（in-place）。
    /// </summary>
    private void ApplyLine(string line, ClaudeSessionInfo session, List<ClaudeMessageInfo> messages,
        ClaudeUsageInfo currentUsage, string filePath)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        try
        {
            var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (root.TryGetProperty("sessionId", out var sessionIdElement))
            {
                session.SessionId = sessionIdElement.GetString() ?? session.SessionId;
            }

            if (root.TryGetProperty("cwd", out var cwdElement))
            {
                session.WorkingDirectory = cwdElement.GetString();
                session.ProjectName = Path.GetFileName(session.WorkingDirectory);
            }

            // entrypoint: 仅在第一次见到时锁定（后续行不覆盖，避免一行写错就把整个 session 的
            // entrypoint 翻盘）。常见值：cli / claude-desktop
            if (string.IsNullOrEmpty(session.Entrypoint)
                && root.TryGetProperty("entrypoint", out var entryElement))
            {
                session.Entrypoint = entryElement.GetString();
            }

            if (root.TryGetProperty("timestamp", out var tsElement))
            {
                if (ClaudeTimestamp.TryParseUtc(tsElement.GetString(), out var ts))
                {
                    session.LastActiveAt = ts;
                    if (!session.CreatedAt.HasValue)
                        session.CreatedAt = ts;
                }
            }

            if (root.TryGetProperty("type", out var typeElement) &&
                typeElement.GetString() == "custom-title" &&
                root.TryGetProperty("customTitle", out var titleElement))
            {
                session.CustomTitle = titleElement.GetString();
            }

            if (root.TryGetProperty("message", out var messageElement))
            {
                var msg = ParseMessage(messageElement, doc);
                if (msg != null)
                {
                    messages.Add(msg);

                    if (msg.Role == "assistant" && msg.Usage != null)
                    {
                        AccumulateUsage(currentUsage, msg.Usage);
                        session.Model = msg.Model ?? session.Model;
                    }
                }
            }
        }
        catch (JsonException)
        {
            _logger?.LogDebug("Failed to parse JSON line in {File}: {Line}", filePath, line[..Math.Min(100, line.Length)]);
        }
    }

    private ClaudeMessageInfo? ParseMessage(JsonElement messageElement, JsonDocument rootDoc)
    {
        var msg = new ClaudeMessageInfo();

        // 获取角色
        if (messageElement.TryGetProperty("role", out var roleElement))
        {
            msg.Role = roleElement.GetString() ?? "unknown";
        }

        // 获取内容
        if (messageElement.TryGetProperty("content", out var contentElement))
        {
            msg.Content = ExtractContentText(contentElement);
            msg.IsToolResult = HasToolResultBlock(contentElement);
            msg.IsInterruptMarker = HasInterruptMarker(contentElement);
        }

        // 获取模型（assistant消息）
        if (messageElement.TryGetProperty("model", out var modelElement))
        {
            msg.Model = modelElement.GetString();
        }

        // 获取时间戳
        if (rootDoc.RootElement.TryGetProperty("timestamp", out var tsElement))
        {
            if (ClaudeTimestamp.TryParseUtc(tsElement.GetString(), out var ts))
            {
                msg.Timestamp = ts;
            }
        }

        // 获取消息ID（用于去重）
        if (messageElement.TryGetProperty("id", out var idElement))
        {
            msg.MessageId = idElement.GetString();
        }

        // 获取stop_reason（用于判断消息是否完整）
        if (messageElement.TryGetProperty("stop_reason", out var stopReasonElement))
        {
            msg.StopReason = stopReasonElement.GetString();
        }

        // 解析用量信息
        if (messageElement.TryGetProperty("usage", out var usageElement))
        {
            msg.Usage = new ClaudeUsageInfo
            {
                InputTokens = GetUInt32OrDefault(usageElement, "input_tokens"),
                OutputTokens = GetUInt32OrDefault(usageElement, "output_tokens"),
                CacheReadTokens = GetUInt32OrDefault(usageElement, "cache_read_input_tokens"),
                CacheCreationTokens = GetUInt32OrDefault(usageElement, "cache_creation_input_tokens")
            };
        }

        return msg;
    }

    /// <summary>
    /// 判断是否是 Claude Code 写入的 Ctrl+C 中断标记（user message 内一个 text block，文本以
    /// "[Request interrupted by user" 开头，已观察到的两种变体："...by user]" 和 "...by user for tool use]"）。
    /// watcher 见到末尾是中断标记 → 直接转 Idle，相当于一轮强制结束。
    /// </summary>
    private static bool HasInterruptMarker(JsonElement contentElement)
    {
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            var s = contentElement.GetString();
            return s != null && s.StartsWith("[Request interrupted by user", StringComparison.Ordinal);
        }
        if (contentElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in contentElement.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                    && block.TryGetProperty("text", out var textElement))
                {
                    var text = textElement.GetString();
                    if (text != null && text.StartsWith("[Request interrupted by user", StringComparison.Ordinal))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// 判断 user message 的 content 是否含有 tool_result block。
    /// 用于区分用户输入（content 是字符串或含 text block）与 tool 调用结果（含 tool_result block）：
    /// 前者意味着 Claude 在思考，后者意味着 Claude 应当立即处理 tool 输出。watcher 据此决定 idle-tick 兜底。
    /// </summary>
    private static bool HasToolResultBlock(JsonElement contentElement)
    {
        if (contentElement.ValueKind != JsonValueKind.Array) return false;
        foreach (var block in contentElement.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_result")
                return true;
        }
        return false;
    }

    private string ExtractContentText(JsonElement contentElement)
    {
        // 处理字符串类型
        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? "";
        }

        // 处理数组类型（content blocks）
        if (contentElement.ValueKind == JsonValueKind.Array)
        {
            var texts = new List<string>();
            foreach (var block in contentElement.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var blockType))
                {
                    var type = blockType.GetString();
                    if (type == "text" && block.TryGetProperty("text", out var textElement))
                    {
                        texts.Add(textElement.GetString() ?? "");
                    }
                    else if (type == "tool_use")
                    {
                        texts.Add("[Tool Use]");
                    }
                    else if (type == "tool_result")
                    {
                        texts.Add("[Tool Result]");
                    }
                }
            }
            return string.Join(" ", texts);
        }

        return "";
    }

    private void AccumulateUsage(ClaudeUsageInfo total, ClaudeUsageInfo current)
    {
        total.InputTokens += current.InputTokens;
        total.OutputTokens += current.OutputTokens;
        total.CacheReadTokens += current.CacheReadTokens;
        total.CacheCreationTokens += current.CacheCreationTokens;
    }

    private decimal CalculateCost(ClaudeUsageInfo usage, string? model)
    {
        if (string.IsNullOrEmpty(model))
            return 0;

        // 查找模型定价（模糊匹配）
        var pricing = FindModelPricing(model);
        if (pricing == null)
            return 0;

        // 计算费用（价格按每1K tokens）
        var inputCost = (usage.InputTokens / 1000.0m) * pricing.InputPrice;
        var outputCost = (usage.OutputTokens / 1000.0m) * pricing.OutputPrice;
        var cacheReadCost = (usage.CacheReadTokens / 1000.0m) * pricing.CacheReadPrice;
        var cacheWriteCost = (usage.CacheCreationTokens / 1000.0m) * pricing.CacheWritePrice;

        return inputCost + outputCost + cacheReadCost + cacheWriteCost;
    }

    private ModelPricing? FindModelPricing(string modelId)
    {
        // 精确匹配
        if (ModelPricings.TryGetValue(modelId, out var exactMatch))
            return exactMatch;

        // 去掉日期后缀匹配，如 "claude-opus-4-6-20260206" → "claude-opus-4-6"
        var parts = modelId.Split('-');
        if (parts.Length > 1 && Regex.IsMatch(parts[^1], @"^\d{8}$"))
        {
            var withoutDate = string.Join("-", parts[..^1]);
            if (ModelPricings.TryGetValue(withoutDate, out var withoutDateMatch))
                return withoutDateMatch;
        }

        // 前缀匹配
        foreach (var (key, value) in ModelPricings)
        {
            if (modelId.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    /// <summary>
    /// 从消息列表里找"有意义"的首条用户内容用作会话标题。
    /// 跳过纯 caveat / stdout / system-reminder 这种 Claude Code 自己塞进来的元数据条，
    /// 直到找到用户真实输入或 slash 命令。
    /// </summary>
    private static string? FindFirstMeaningfulUserContent(List<ClaudeMessageInfo> messages)
    {
        foreach (var m in messages)
        {
            if (m.Role != "user") continue;
            var raw = m.Content?.Trim();
            if (string.IsNullOrEmpty(raw)) continue;
            var pretty = ExtractDisplayTitle(raw);
            if (!string.IsNullOrWhiteSpace(pretty)) return pretty;
        }
        return null;
    }

    private static readonly Regex CommandNameRegex =
        new(@"<command-name>\s*([^<]+?)\s*</command-name>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommandMessageRegex =
        new(@"<command-message>\s*([^<]+?)\s*</command-message>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MetadataOnlyRegex =
        new(@"^\s*<(local-command-caveat|local-command-stdout|local-command-stderr|system-reminder)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 单条消息 → 给 UI 看的标题，提取不出有意义内容时返回 null（让上层跳到下一条）。
    /// 例：
    ///   "<command-name>/save</command-name>..."     → "/save"
    ///   "<local-command-caveat>...</...>"           → null（跳过）
    ///   "你好"                                      → "你好"
    /// </summary>
    private static string? ExtractDisplayTitle(string raw)
    {
        var s = raw.Trim();

        // 用户敲的 slash 命令是最准确的标题信号
        var nameMatch = CommandNameRegex.Match(s);
        if (nameMatch.Success) return nameMatch.Groups[1].Value;

        var msgMatch = CommandMessageRegex.Match(s);
        if (msgMatch.Success) return msgMatch.Groups[1].Value;

        // Claude Code 自己塞进来的 caveat / stdout / system-reminder：本身没有用户意图，跳过
        if (MetadataOnlyRegex.IsMatch(s)) return null;

        // 其他都当作用户输入直接显示
        return s;
    }

    private static uint GetUInt32OrDefault(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.TryGetUInt32(out var value))
                return value;
            if (prop.TryGetInt32(out var intValue) && intValue >= 0)
                return (uint)intValue;
        }
        return 0;
    }
}

/// <summary>
/// Claude Code 会话信息
/// </summary>
public class ClaudeSessionInfo
{
    public string SessionId { get; set; } = "";
    public string? CustomTitle { get; set; }
    public string Title { get; set; } = "";
    public string? Summary { get; set; }
    public string? WorkingDirectory { get; set; }
    public string? ProjectName { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? LastActiveAt { get; set; }
    public string? SourcePath { get; set; }
    public string? Model { get; set; }
    /// <summary>
    /// "cli" 或 "claude-desktop"。决定 Jump 时是开终端跑 claude --resume 还是激活
    /// 桌面端窗口。从 transcript JSONL 行的 entrypoint 字段读出来。
    /// </summary>
    public string? Entrypoint { get; set; }
    public List<ClaudeMessageInfo> Messages { get; set; } = new();
    public ClaudeUsageInfo Usage { get; set; } = new();
    public decimal TotalCost { get; set; }

    /// <summary>
    /// 是否拥有"真正的"标题（自定义标题或首条用户消息），而非项目名后备
    /// </summary>
    public bool HasRealTitle => !string.IsNullOrEmpty(CustomTitle)
        || Messages.Any(m => m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content));

    /// <summary>
    /// 由工作目录构造 JumpTarget（用于"一键回终端"），无 cwd 则为 null
    /// </summary>
    public JumpTarget? BuildJumpTarget()
    {
        if (string.IsNullOrEmpty(WorkingDirectory)) return null;
        return new JumpTarget
        {
            WorkingDirectory = WorkingDirectory,
            WindowTitle = ProjectName
        };
    }

    /// <summary>
    /// 转换为 AgentSession
    /// </summary>
    public AgentSession ToAgentSession()
    {
        return new AgentSession
        {
            Id = SessionId,
            Title = Title,
            Tool = AgentTool.ClaudeCode,
            Phase = Usage.TotalTokens > 0 ? SessionPhase.Completed : SessionPhase.Running,
            Summary = $"{Summary} | Tokens: {Usage.TotalTokens:N0} | Cost: ${TotalCost:F4}",
            UpdatedAt = LastActiveAt ?? DateTime.UtcNow,
            IsHookManaged = false,
            JumpTarget = BuildJumpTarget(),
            ClaudeMetadata = new ClaudeMetadata
            {
                TranscriptPath = SourcePath,
                Model = Model,
                Entrypoint = Entrypoint,
                ActiveSubagents = 0,
                ActiveTasks = 0
            }
        };
    }
}

/// <summary>
/// Claude Code 消息信息
/// </summary>
public class ClaudeMessageInfo
{
    public string MessageId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime? Timestamp { get; set; }
    public string? Model { get; set; }
    public string? StopReason { get; set; }
    public ClaudeUsageInfo? Usage { get; set; }
    /// <summary>
    /// 仅 user message：content 数组里是否含 tool_result block。区分"用户输入文本"与
    /// "tool 调用结果回填"。watcher 用这个决定末尾是 user 时是否允许 idle-tick 兜底。
    /// </summary>
    public bool IsToolResult { get; set; }
    /// <summary>
    /// 仅 user message：是不是 Ctrl+C 中断标记（"[Request interrupted by user..."）。
    /// 末尾是中断标记 → watcher 立即转 Idle，相当于强制结束当前一轮。
    /// </summary>
    public bool IsInterruptMarker { get; set; }
}

/// <summary>
/// Claude Code 用量信息
/// </summary>
public class ClaudeUsageInfo
{
    public uint InputTokens { get; set; }
    public uint OutputTokens { get; set; }
    public uint CacheReadTokens { get; set; }
    public uint CacheCreationTokens { get; set; }

    public uint TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>
/// 模型定价信息
/// </summary>
public class ModelPricing
{
    /// <summary>输入token价格（每1K tokens，美元）</summary>
    public decimal InputPrice { get; set; }

    /// <summary>输出token价格（每1K tokens，美元）</summary>
    public decimal OutputPrice { get; set; }

    /// <summary>缓存读取token价格（每1K tokens，美元）</summary>
    public decimal CacheReadPrice { get; set; }

    /// <summary>缓存写入token价格（每1K tokens，美元）</summary>
    public decimal CacheWritePrice { get; set; }
}

/// <summary>
/// 同步状态（用于增量更新）
///
/// <see cref="LastLineOffset"/> 是已完整消费过的字节偏移：仅在某行以 '\n' 结尾被读完后
/// 才会前进。半截行（claude.exe 还在 flush）会被搁置到下次调用，从而避免在 watcher 高频
/// 触发的场景下错误地把半截行当作 JSON 解析失败丢弃。
/// </summary>
public class SyncState
{
    public DateTime LastModified { get; set; }
    public long FileSize { get; set; }
    public long LastLineOffset { get; set; }
    public ClaudeSessionInfo? CachedSession { get; set; }
    /// <summary>
    /// 累积的用量（跨增量调用沿用），不是从 messages 重新求和——避免每次增量都重算整段历史。
    /// </summary>
    public ClaudeUsageInfo? CachedUsage { get; set; }
}

/// <summary>
/// 字符串扩展方法
/// </summary>
public static class StringExtensions
{
    public static string Truncate(this string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
