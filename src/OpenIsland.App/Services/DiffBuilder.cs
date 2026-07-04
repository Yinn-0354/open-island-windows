using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace OpenIsland.App.Services;

/// <summary>行在 diff 里的角色：未变/新增/删除，三选一。</summary>
public enum DiffLineKind { Context, Added, Removed }

/// <summary>
/// diff 里的一行。OldLineNumber/NewLineNumber 分别是这一行在旧/新文件里的 1-based 行号——
/// Context 行两边都有（同一行），Added 只有 NewLineNumber，Removed 只有 OldLineNumber。
/// </summary>
public sealed class DiffLine
{
    public int? OldLineNumber { get; init; }
    public int? NewLineNumber { get; init; }
    public DiffLineKind Kind { get; init; }
    public string Text { get; init; } = "";
}

/// <summary>一个连续的改动区块（含前后 <see cref="DiffBuilder.ContextLines"/> 行上下文）。</summary>
public sealed class DiffHunk
{
    public IReadOnlyList<DiffLine> Lines { get; init; } = Array.Empty<DiffLine>();
}

/// <summary>
/// Edit/MultiEdit/Write 的完整 diff 结果，供岛上代码审阅卡片渲染。
/// IsNewFile：Write 且目标文件此前不存在。Additions/Deletions：跨所有 hunk 汇总的 +/- 行数
/// （只数真正变化的行，不含 Context）。TruncatedLineCount &gt; 0 时说明还有改动没渲染（防止
/// 超大 diff 把岛撑爆/卡顿），UI 应显示"…还有约 N 行"提示。
/// </summary>
public sealed class CodeReviewDiff
{
    public string FilePath { get; init; } = "";
    public bool IsNewFile { get; init; }
    public int Additions { get; init; }
    public int Deletions { get; init; }
    public IReadOnlyList<DiffHunk> Hunks { get; init; } = Array.Empty<DiffHunk>();
    public int TruncatedLineCount { get; init; }
}

/// <summary>
/// 从 Edit/MultiEdit/Write 的 tool_input 构建可渲染的 diff。三种工具的 tool_input 形状：
///   Edit:      {"file_path","old_string","new_string","replace_all"?}
///   MultiEdit: {"file_path","edits":[{"old_string","new_string","replace_all"?}, ...]}
///   Write:     {"file_path","content"}
///
/// 核心策略：不做整文件 LCS（Edit/MultiEdit 场景下没必要，文件可能很大但改动通常很小）——
/// 而是在一份"当前文本快照"（currentText，初始 = 磁盘上的文件内容）里用 IndexOf 定位每次
/// 替换的位置，只对被替换的那一小段 + 前后若干行上下文做行级 diff，处理完一次替换就把
/// currentText 更新成替换后的样子，再处理下一次替换——这样每次都是对"当前实际会长什么样"
/// 的文本定位，天然正确处理多次替换之间的行号偏移，不需要手动维护 delta。
/// 只有 Write（整篇覆写，没有 old_string 可定位）才需要对整个文件做 LCS 行级 diff。
/// </summary>
public static class DiffBuilder
{
    /// <summary>每个改动区块前后各带多少行原文上下文（未变化的行），对齐 GitHub PR diff 观感。</summary>
    private const int ContextLines = 3;

    /// <summary>整份 diff 最多渲染多少"行"（含上下文），超过就截断防止岛被超大改动撑爆/卡顿。</summary>
    private const int MaxRenderedLines = 400;

    /// <summary>文件超过这个字节数就不读它做上下文/整文件 diff，退化成无行号的纯 old/new 对照。</summary>
    private const long MaxFileBytesForContext = 2 * 1024 * 1024;

    public static CodeReviewDiff? Build(string? toolName, IDictionary<string, object>? toolInput)
    {
        if (string.IsNullOrEmpty(toolName) || toolInput == null || toolInput.Count == 0) return null;
        try
        {
            var json = JsonSerializer.Serialize(toolInput);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return toolName.ToLowerInvariant() switch
            {
                "edit" => BuildFromEdit(root),
                "multiedit" => BuildFromMultiEdit(root),
                "write" => BuildFromWrite(root),
                _ => null,
            };
        }
        catch { return null; }
    }

    private static string? GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool GetBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    // ── Edit ──
    private static CodeReviewDiff? BuildFromEdit(JsonElement root)
    {
        var filePath = GetStr(root, "file_path");
        var oldStr = GetStr(root, "old_string");
        var newStr = GetStr(root, "new_string");
        if (string.IsNullOrEmpty(filePath) || oldStr == null || newStr == null) return null;
        bool replaceAll = GetBool(root, "replace_all");

        var edits = new List<(string oldStr, string newStr, bool replaceAll)> { (oldStr, newStr, replaceAll) };
        return BuildFromReplacements(filePath, edits);
    }

    // ── MultiEdit ──
    private static CodeReviewDiff? BuildFromMultiEdit(JsonElement root)
    {
        var filePath = GetStr(root, "file_path");
        if (string.IsNullOrEmpty(filePath)) return null;
        if (!root.TryGetProperty("edits", out var editsEl) || editsEl.ValueKind != JsonValueKind.Array) return null;

        var edits = new List<(string oldStr, string newStr, bool replaceAll)>();
        foreach (var e in editsEl.EnumerateArray())
        {
            var o = GetStr(e, "old_string");
            var n = GetStr(e, "new_string");
            if (o == null || n == null) continue;
            edits.Add((o, n, GetBool(e, "replace_all")));
        }
        if (edits.Count == 0) return null;
        return BuildFromReplacements(filePath, edits);
    }

    /// <summary>Edit 和 MultiEdit 共用：在一份不断推进的 currentText 快照上依次应用每次替换。</summary>
    private static CodeReviewDiff? BuildFromReplacements(
        string filePath, List<(string oldStr, string newStr, bool replaceAll)> edits)
    {
        var original = ReadFileSafely(filePath);
        if (original == null)
        {
            // 文件读不到（不存在/太大/无权限）—— 退化成无行号的纯 old/new 对照，每次编辑一个 hunk。
            return FallbackWithoutContext(filePath, edits);
        }

        var hunks = new List<DiffHunk>();
        int additions = 0, deletions = 0, renderedLines = 0, truncatedLines = 0;
        bool truncated = false;
        var currentText = original;

        foreach (var (oldStr, newStr, replaceAll) in edits)
        {
            if (truncated)
            {
                // 已经截断：后面的编辑不再定位/渲染，只用行数差做"还有约 N 行"的粗略估计。
                truncatedLines += Math.Max(CountLines(oldStr), CountLines(newStr));
                continue;
            }

            int searchFrom = 0;
            while (true)
            {
                var result = ProcessOneReplacement(currentText, oldStr, newStr, searchFrom);
                if (result == null) break; // 定位不到（也可能是 replace_all 已经全部替换完）

                var (hunk, updatedText, nextSearchFrom, hunkAdditions, hunkDeletions) = result.Value;
                int hunkLineCount = hunk.Lines.Count;

                if (renderedLines + hunkLineCount > MaxRenderedLines)
                {
                    truncated = true;
                    truncatedLines += hunkLineCount;
                    break;
                }

                hunks.Add(hunk);
                renderedLines += hunkLineCount;
                additions += hunkAdditions;
                deletions += hunkDeletions;
                currentText = updatedText;
                searchFrom = nextSearchFrom;

                if (!replaceAll) break;
            }
        }

        if (hunks.Count == 0) return FallbackWithoutContext(filePath, edits);

        return new CodeReviewDiff
        {
            FilePath = filePath,
            IsNewFile = false,
            Additions = additions,
            Deletions = deletions,
            Hunks = hunks,
            TruncatedLineCount = truncatedLines,
        };
    }

    /// <summary>
    /// 在 currentText（从 searchFrom 开始找）里定位一次 oldStr→newStr 替换，构建它的 hunk
    /// （含前后 ContextLines 行上下文，改动区间内部再做行级 LCS diff），并算出替换后的新文本。
    /// 找不到就返回 null。返回的 nextSearchFrom 是"下一次同一个 old_string 该从哪里继续找"——
    /// 从新插入的 newStr 之后开始，避免 newStr 里恰好包含 oldStr 导致死循环。
    /// </summary>
    private static (DiffHunk hunk, string updatedText, int nextSearchFrom, int additions, int deletions)?
        ProcessOneReplacement(string currentText, string oldStr, string newStr, int searchFrom)
    {
        // 空 old_string 定位不到任何有意义的位置——IndexOf 会在 searchFrom 原地"命中"，
        // 且替换前后文本不变时下一次搜索还是原地，replace_all 循环会死循环卡死 UI 线程
        // （已被独立审阅实测复现）。空 old_string 直接判定失败，退化到无上下文兜底。
        if (oldStr.Length == 0) return null;
        if (searchFrom > currentText.Length) return null;
        int idx = currentText.IndexOf(oldStr, searchFrom, StringComparison.Ordinal);
        if (idx < 0) return null;

        // 把改动区间"吸附"到整行边界，方便按行渲染（即便 old_string 本身从行中间开始/结束）。
        int lineStart = idx == 0 ? 0 : currentText.LastIndexOf('\n', idx - 1) + 1;
        int spanEndExclusive = idx + oldStr.Length;
        int lineEndSearchFrom = Math.Min(Math.Max(spanEndExclusive - 1, lineStart), currentText.Length - 1);
        int newlineAfter = currentText.Length == 0 ? -1 : currentText.IndexOf('\n', lineEndSearchFrom);
        int lineEnd = newlineAfter < 0 ? currentText.Length : newlineAfter; // 不含这个 '\n' 本身

        string prefixInLine = currentText.Substring(lineStart, idx - lineStart);
        // old_string 自身以 '\n' 结尾时（删整行是最常见场景），它已经把该行自己的换行符也
        // 吃掉了——这时 lineEnd 会恰好落在 spanEndExclusive 之前（该行终止符的位置），
        // 也就是这一行在换行符之后已经没有剩余内容，suffixInLine 应为空串，而不是对
        // Substring 传入负长度（独立审阅实测复现过这个崩溃：'length (-1) must be
        // non-negative'，被外层 try/catch 吞掉后表现为整份 diff 静默变 null）。
        string suffixInLine = lineEnd > spanEndExclusive
            ? currentText.Substring(spanEndExclusive, lineEnd - spanEndExclusive)
            : "";
        string oldSpanText = currentText.Substring(lineStart, lineEnd - lineStart);
        string newSpanText = prefixInLine + newStr + suffixInLine;

        var oldSpanLines = SplitLines(oldSpanText);
        var newSpanLines = SplitLines(newSpanText);
        var spanDiff = DiffLinesLcs(oldSpanLines, newSpanLines);

        int spanFirstLine1Based = LineNumberAt(currentText, lineStart);

        var lines = new List<DiffLine>();

        // 前置上下文：span 起始行往前最多 ContextLines 行，此时改动还没发生，old/new 行号一致。
        var beforeLines = CollectContextBefore(currentText, lineStart, ContextLines);
        int ctxLn = spanFirstLine1Based - beforeLines.Count;
        foreach (var l in beforeLines)
        {
            lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = l, OldLineNumber = ctxLn, NewLineNumber = ctxLn });
            ctxLn++;
        }

        // span 内部：按 LCS 结果逐行推进 old/new 行号。
        int oldLn = spanFirstLine1Based, newLn = spanFirstLine1Based;
        int additions = 0, deletions = 0;
        foreach (var (kind, text) in spanDiff)
        {
            switch (kind)
            {
                case DiffLineKind.Context:
                    lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = text, OldLineNumber = oldLn, NewLineNumber = newLn });
                    oldLn++; newLn++;
                    break;
                case DiffLineKind.Removed:
                    lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = text, OldLineNumber = oldLn });
                    oldLn++; deletions++;
                    break;
                case DiffLineKind.Added:
                    lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = text, NewLineNumber = newLn });
                    newLn++; additions++;
                    break;
            }
        }

        // 后置上下文：span 结束处往后最多 ContextLines 行（在替换前的 currentText 里读，
        // 这些行不受本次替换影响，old/new 行号从这里开始重新对齐、继续同步递增）。
        var afterLines = CollectContextAfter(currentText, lineEnd, ContextLines);
        foreach (var l in afterLines)
        {
            lines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = l, OldLineNumber = oldLn, NewLineNumber = newLn });
            oldLn++; newLn++;
        }

        // 实际文本替换用最简单直接的拼接，不依赖上面的行边界推导，两者独立、互相印证。
        string updatedText = currentText.Substring(0, idx) + newStr + currentText.Substring(spanEndExclusive);
        int nextSearchFrom = idx + newStr.Length;

        return (new DiffHunk { Lines = lines }, updatedText, nextSearchFrom, additions, deletions);
    }

    // ── Write：整篇覆写，没有 old_string 可定位，只能对整个文件做行级 diff。──
    private static CodeReviewDiff? BuildFromWrite(JsonElement root)
    {
        var filePath = GetStr(root, "file_path");
        var content = GetStr(root, "content");
        if (string.IsNullOrEmpty(filePath) || content == null) return null;

        bool exists = File.Exists(filePath);
        string original = "";
        bool tooLargeForLcs = false;
        if (exists)
        {
            var read = ReadFileSafely(filePath);
            if (read != null)
            {
                original = read;
            }
            else
            {
                // 超过 ReadFileSafely 那个偏保守的上限（给"读整个文件做 LCS"用的）就做不起
                // O(N*M) 的智能对齐了，但下面的朴素"整段删除旧内容 + 整段新增新内容"兜底只是
                // O(N) 切行，不需要那么保守——用更宽松的上限单独再读一次，否则旧内容会一直是
                // 空串，"整段删除"这半份 diff 就无从谈起，用户看到的会是"看起来像新建文件"的
                // 误导性画面（哪怕 IsNewFile 字段本身仍然正确是 false）。
                tooLargeForLcs = true;
                original = ReadFileForNaiveFallback(filePath) ?? "";
            }
        }

        var oldLines = SplitLines(original);
        var newLines = SplitLines(content);

        List<(DiffLineKind kind, string text)> diff;
        if (tooLargeForLcs || (long)oldLines.Length * newLines.Length > 4_000_000)
        {
            // 文件太大做不起 O(N*M) LCS —— 退化成"整段删除旧内容 + 整段新增新内容"，
            // 没有智能对齐但至少能看到改动全貌，不做完全没有上下文的裸 old/new 对照。
            diff = new List<(DiffLineKind, string)>(oldLines.Length + newLines.Length);
            diff.AddRange(oldLines.Select(l => (DiffLineKind.Removed, l)));
            diff.AddRange(newLines.Select(l => (DiffLineKind.Added, l)));
        }
        else
        {
            diff = DiffLinesLcs(oldLines, newLines);
        }

        // 先把整份 diff 展开成带行号的 DiffLine，并在全量上统计 +/−（统计绝不受下面的渲染
        // 截断影响——否则改动落在第 400 行之后时头部会错报 "+0 −0"）。
        var allLines = new List<DiffLine>(diff.Count);
        int oldLn = 1, newLn = 1, additions = 0, deletions = 0;
        foreach (var (kind, text) in diff)
        {
            switch (kind)
            {
                case DiffLineKind.Context:
                    allLines.Add(new DiffLine { Kind = DiffLineKind.Context, Text = text, OldLineNumber = oldLn, NewLineNumber = newLn });
                    oldLn++; newLn++; break;
                case DiffLineKind.Removed:
                    allLines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = text, OldLineNumber = oldLn });
                    oldLn++; deletions++; break;
                case DiffLineKind.Added:
                    allLines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = text, NewLineNumber = newLn });
                    newLn++; additions++; break;
            }
        }

        // 只渲染改动附近的行（每个 +/− 前后各留 ContextLines 行），长段未变行折叠成 hunk 间隔。
        // 这样即便整篇覆写只改了末尾一行，改动也一定出现在卡片里，而不是被前面几百行未变内容
        // 顶到 MaxRenderedLines 截断线之外、卡片只剩一屏未变上下文（之前从第 1 行顺序渲染就是
        // 这个毛病）。
        var (hunks, truncatedLines) = ExtractChangeHunks(allLines, MaxRenderedLines);

        return new CodeReviewDiff
        {
            FilePath = filePath,
            IsNewFile = !exists,
            Additions = additions,
            Deletions = deletions,
            Hunks = hunks,
            TruncatedLineCount = truncatedLines,
        };
    }

    /// <summary>
    /// 从完整行级 diff 里抽取 git 风格 hunk：每个 Added/Removed 行前后各保留 ContextLines 行
    /// 未变上下文，相邻/重叠窗口合并成一个 hunk，中间的长段未变行跳过不渲染。总渲染行数封顶
    /// maxLines，超出部分不再产出 hunk，剩余的"改动行"计入 truncated（跟 UI 的"…还有约 N 行
    /// 改动未显示"语义一致，只数 +/− 行）。整篇无改动时返回空 hunk 列表（此时卡片头 +0 −0 是
    /// 真实的）。
    /// </summary>
    private static (IReadOnlyList<DiffHunk> hunks, int truncated) ExtractChangeHunks(List<DiffLine> all, int maxLines)
    {
        int n = all.Count;
        var keep = new bool[n];
        bool anyChange = false;
        for (int i = 0; i < n; i++)
        {
            if (all[i].Kind == DiffLineKind.Context) continue;
            anyChange = true;
            int lo = Math.Max(0, i - ContextLines);
            int hi = Math.Min(n - 1, i + ContextLines);
            for (int j = lo; j <= hi; j++) keep[j] = true;
        }
        if (!anyChange) return (Array.Empty<DiffHunk>(), 0);

        var hunks = new List<DiffHunk>();
        int rendered = 0, truncated = 0;
        bool capped = false;
        int k = 0;
        while (k < n)
        {
            if (!keep[k]) { k++; continue; }
            int start = k;
            while (k < n && keep[k]) k++;   // [start, k) 是一段连续保留区
            int len = k - start;

            if (capped)
            {
                for (int j = start; j < k; j++)
                    if (all[j].Kind != DiffLineKind.Context) truncated++;
                continue;
            }
            if (rendered + len > maxLines)
            {
                int room = maxLines - rendered;
                if (room > 0)
                {
                    hunks.Add(new DiffHunk { Lines = all.GetRange(start, room) });
                    rendered += room;
                }
                for (int j = start + Math.Max(room, 0); j < k; j++)
                    if (all[j].Kind != DiffLineKind.Context) truncated++;
                capped = true;
                continue;
            }
            hunks.Add(new DiffHunk { Lines = all.GetRange(start, len) });
            rendered += len;
        }
        return (hunks, truncated);
    }

    /// <summary>文件读不到 / 定位不到时的兜底：把每次编辑渲染成无行号的纯 old/new 对照。</summary>
    private static CodeReviewDiff FallbackWithoutContext(
        string filePath, List<(string oldStr, string newStr, bool replaceAll)> edits)
    {
        var hunks = new List<DiffHunk>();
        int additions = 0, deletions = 0;
        foreach (var (oldStr, newStr, _) in edits)
        {
            var lines = new List<DiffLine>();
            foreach (var l in SplitLines(oldStr)) { lines.Add(new DiffLine { Kind = DiffLineKind.Removed, Text = l }); deletions++; }
            foreach (var l in SplitLines(newStr)) { lines.Add(new DiffLine { Kind = DiffLineKind.Added, Text = l }); additions++; }
            hunks.Add(new DiffHunk { Lines = lines });
        }
        return new CodeReviewDiff
        {
            FilePath = filePath, IsNewFile = false,
            Additions = additions, Deletions = deletions,
            Hunks = hunks, TruncatedLineCount = 0,
        };
    }

    private static string? ReadFileSafely(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytesForContext) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    /// <summary>
    /// Write 覆写一个超过 MaxFileBytesForContext 的旧文件时，专用的"读旧内容做朴素兜底"路径——
    /// 不需要 LCS（只是切行拼进 Removed 列表，O(N)），所以不用 ReadFileSafely 那个偏保守、给
    /// LCS 用的上限，换一个宽松得多的上限。真超过这个上限（罕见）才彻底放弃，调用方把旧内容
    /// 当空串处理。
    /// </summary>
    private const long MaxFileBytesForNaiveFallback = 20 * 1024 * 1024;

    private static string? ReadFileForNaiveFallback(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytesForNaiveFallback) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    /// <summary>1-based 行号：数 text[0..charIndex) 里的 '\n' 个数 + 1。</summary>
    private static int LineNumberAt(string text, int charIndex)
    {
        int count = 1;
        for (int i = 0; i < charIndex && i < text.Length; i++)
            if (text[i] == '\n') count++;
        return count;
    }

    private static int CountLines(string s) => SplitLines(s).Length;

    /// <summary>
    /// 按 '\n' 切行，去掉每行末尾可能残留的 '\r'（兼容 CRLF）。空字符串返回空数组（0 行）。
    /// 文本以 '\n' 结尾时（几乎所有正常文件都是）Split 会在末尾多产出一个空字符串元素——
    /// 那不是真实存在的一行，是"最后一个换行符之后"的占位，git/大多数行工具都不算它是一行，
    /// 这里去掉，否则每份 diff 底部都会凭空多一行空行。
    /// </summary>
    private static string[] SplitLines(string s)
    {
        if (s.Length == 0) return Array.Empty<string>();
        var parts = s.Split('\n');
        if (s.EndsWith('\n')) parts = parts[..^1];
        return parts.Select(l => l.EndsWith("\r", StringComparison.Ordinal) ? l[..^1] : l).ToArray();
    }

    /// <summary>行尾 '\r' 剥离（CRLF 兼容）。上下文收集器必须跟 <see cref="SplitLines"/> 一样剥，
    /// 否则 CRLF 文件里上下文行的 Text 带一个尾部 '\r'——WPF TextBlock 会把它当换行，整行渲染
    /// 成双倍高度（幽灵空行），而同一张卡上的 +/- 行走 SplitLines 已剥 CR、是正常单行高，看起来
    /// 就是"上下文行莫名其妙多出一截"。</summary>
    private static string StripCr(string s) => s.EndsWith('\r') ? s[..^1] : s;

    /// <summary>lineStart 往前最多 count 行（不含 lineStart 所在行本身），按从早到晚顺序返回。</summary>
    private static List<string> CollectContextBefore(string text, int lineStart, int count)
    {
        var result = new List<string>();
        int pos = lineStart;
        for (int i = 0; i < count && pos > 0; i++)
        {
            int prevLineEnd = pos - 1; // 上一行末尾的 '\n' 所在位置
            int prevLineStart = prevLineEnd == 0 ? 0 : text.LastIndexOf('\n', prevLineEnd - 1) + 1;
            result.Insert(0, StripCr(text.Substring(prevLineStart, prevLineEnd - prevLineStart)));
            pos = prevLineStart;
        }
        return result;
    }

    /// <summary>
    /// lineEnd（不含这个位置的 '\n'）往后最多 count 行。
    /// 边界：pos 落在"文件最后一个 '\n'"上时，nextLineStart 会正好等于 text.Length——那是
    /// 换行符之后的"文件结尾"，不是一行真实内容（文件以换行符结尾是常态，不能因此多凭空
    /// 造出一行空行）。用 >= 而非 > 判断，把这个占位位置也当"没有更多行"处理。
    /// </summary>
    private static List<string> CollectContextAfter(string text, int lineEnd, int count)
    {
        var result = new List<string>();
        int pos = lineEnd;
        for (int i = 0; i < count && pos < text.Length; i++)
        {
            int nextLineStart = pos + 1; // 跳过当前这个 '\n'
            if (nextLineStart >= text.Length) break;
            int nextLineEnd = text.IndexOf('\n', nextLineStart);
            if (nextLineEnd < 0) nextLineEnd = text.Length;
            result.Add(StripCr(text.Substring(nextLineStart, nextLineEnd - nextLineStart)));
            pos = nextLineEnd;
        }
        return result;
    }

    /// <summary>
    /// 经典 O(N*M) LCS 动态规划行级 diff。输入通常很小（单次改动 span 内的行数，或已做过
    /// 大小 guard 的整文件），性能足够。返回值按 old→new 顺序交替给出 Context/Removed/Added。
    /// </summary>
    private static List<(DiffLineKind kind, string text)> DiffLinesLcs(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length, m = newLines.Length;
        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
            for (int j = m - 1; j >= 0; j--)
                dp[i, j] = oldLines[i] == newLines[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        var result = new List<(DiffLineKind, string)>();
        int a = 0, b = 0;
        while (a < n && b < m)
        {
            if (oldLines[a] == newLines[b]) { result.Add((DiffLineKind.Context, oldLines[a])); a++; b++; }
            else if (dp[a + 1, b] >= dp[a, b + 1]) { result.Add((DiffLineKind.Removed, oldLines[a])); a++; }
            else { result.Add((DiffLineKind.Added, newLines[b])); b++; }
        }
        while (a < n) { result.Add((DiffLineKind.Removed, oldLines[a])); a++; }
        while (b < m) { result.Add((DiffLineKind.Added, newLines[b])); b++; }
        return result;
    }
}
