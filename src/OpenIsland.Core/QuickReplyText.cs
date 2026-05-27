using System.Text.RegularExpressions;

namespace OpenIsland.Core;

/// <summary>Result of validating/normalising a quick-reply message.</summary>
public readonly record struct PreparedText(bool Ok, string Text, string? Reason);

/// <summary>
/// Validates and normalises a quick-reply message before it is injected into a session.
/// </summary>
public static class QuickReplyText
{
    /// <summary>Reject absurdly long input to avoid pathological pastes.</summary>
    public const int MaxLength = 16384;

    // 匹配「内部换行段」：一个或多个 \r/\n（含其周围的空白与连续空行）。
    private static readonly Regex InnerNewlineRun = new(@"\s*[\r\n]+\s*", RegexOptions.Compiled);

    public static PreparedText Prepare(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new PreparedText(false, "", "empty");

        // 把内部换行规整成单个空格。注入机制是「粘贴整段 + 末尾一个回车」：若保留内部换行，
        // 多数终端会把每个换行当成一次提交，于是 line1 被当 prompt 先跑掉、末尾回车又提交残段。
        // 规整为单行后，整段作为一条 prompt 一次性发送。
        var text = InnerNewlineRun.Replace(raw, " ").Trim();
        if (text.Length == 0)
            return new PreparedText(false, "", "empty");
        if (text.Length > MaxLength)
            return new PreparedText(false, "", "too long");

        return new PreparedText(true, text, null);
    }
}
