namespace OpenIsland.Core;

/// <summary>
/// Validates that an agent session id is safe to embed in a shell command line.
/// Session ids flow in from untrusted hook payloads / transcript files and are later
/// interpolated into <c>claude --resume {id}</c>, so anything outside a strict
/// allow-list (letters, digits, '-', '_') must be rejected to prevent command injection.
/// </summary>
public static class SessionIdValidator
{
    /// <summary>Max plausible id length; longer inputs are rejected outright.</summary>
    public const int MaxLength = 128;

    public static bool IsValid(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
            return false;

        foreach (var c in id)
        {
            var ok = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                     || c == '-' || c == '_';
            if (!ok) return false;
        }

        return true;
    }
}
