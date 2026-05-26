using System.Globalization;

namespace OpenIsland.Core;

/// <summary>
/// Parses Claude transcript timestamps (ISO-8601, normally 'Z'-suffixed UTC) into a
/// UTC <see cref="DateTime"/>. Centralised so every parse site produces Kind=Utc — the
/// rest of the app (heartbeats, "time ago", the WaitingForApproval unlock comparison in
/// <c>SessionState</c>) assumes UTC, so a naive <c>DateTime.Parse</c> that yields Local
/// time skews everything by the machine's UTC offset.
/// </summary>
public static class ClaudeTimestamp
{
    public static bool TryParseUtc(string? value, out DateTime utc)
    {
        // AdjustToUniversal => result has Kind=Utc; AssumeUniversal => a timezone-less
        // string is treated as UTC rather than local. Together they guarantee every
        // parsed instant is expressed in UTC regardless of the machine's timezone.
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var parsed))
        {
            utc = parsed;
            return true;
        }

        utc = default;
        return false;
    }
}
