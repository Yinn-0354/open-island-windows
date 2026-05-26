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

    public static PreparedText Prepare(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return new PreparedText(false, "", "empty");

        var text = raw.Trim();
        if (text.Length > MaxLength)
            return new PreparedText(false, "", "too long");

        return new PreparedText(true, text, null);
    }
}
