using System;
using System.Text.Json;

namespace OpenIsland.Core;

/// <summary>
/// 解析 Claude 订阅用量端点（GET https://api.anthropic.com/api/oauth/usage）返回的 JSON，
/// 取出"5 小时滚动窗口"的已用比例 + 精确重置时刻。这是 Claude Code `/usage` 背后的同一份数据，
/// 零 message 配额开销。
///
/// 端点未公开、字段形状可能变动，因此解析按宽容策略：
///   - 优先 five_hour.utilization（0..100 的已用百分比）；
///   - 退路 five_hour.used_percent / .percent；
///   - 再退路 five_hour.remaining + .limit → used = 1 - remaining/limit；
///   - 重置时刻接受 resets_at / reset / resets（RFC3339 字符串或 unix 秒）。
/// </summary>
public static class OAuthUsageParser
{
    /// <summary>
    /// 解析 5 小时窗口。成功返回 true，并给出 usedFraction(0..1 已用) 与 resetUtc（可能为 null）。
    /// </summary>
    public static bool TryParseFiveHour(string? json, out double usedFraction, out DateTime? resetUtc)
    {
        usedFraction = 0;
        resetUtc = null;
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            // 找 5 小时窗口对象：优先 snake_case，再兼容其它常见写法。
            if (!TryGetWindow(root, out var win)) return false;

            double? used = ReadUsedFraction(win);
            if (used == null) return false;

            usedFraction = Math.Clamp(used.Value, 0, 1);
            resetUtc = ReadResetUtc(win);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetWindow(JsonElement root, out JsonElement win)
    {
        foreach (var name in new[] { "five_hour", "fiveHour", "5h", "five_hour_window" })
        {
            if (root.TryGetProperty(name, out win) && win.ValueKind == JsonValueKind.Object)
                return true;
        }
        win = default;
        return false;
    }

    private static double? ReadUsedFraction(JsonElement win)
    {
        // utilization / used_percent / percent：0..100 的已用百分比
        foreach (var name in new[] { "utilization", "used_percent", "usedPercent", "percent" })
        {
            if (win.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
                && p.TryGetDouble(out var v))
                return v / 100.0;
        }

        // remaining + limit → used = 1 - remaining/limit
        if (TryNum(win, "remaining", out var rem) && TryNum(win, "limit", out var lim) && lim > 0)
            return 1.0 - rem / lim;

        return null;
    }

    private static DateTime? ReadResetUtc(JsonElement win)
    {
        foreach (var name in new[] { "resets_at", "reset_at", "resetsAt", "reset", "resets" })
        {
            if (!win.TryGetProperty(name, out var r)) continue;
            if (r.ValueKind == JsonValueKind.String)
            {
                var s = r.GetString();
                if (!string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, out var dto))
                    return dto.UtcDateTime;
            }
            else if (r.ValueKind == JsonValueKind.Number && r.TryGetInt64(out var secs))
            {
                // 经验：>10^12 视为毫秒，否则秒
                return secs > 1_000_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(secs).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
            }
        }
        return null;
    }

    private static bool TryNum(JsonElement obj, string name, out double value)
    {
        value = 0;
        return obj.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
               && p.TryGetDouble(out value);
    }
}
