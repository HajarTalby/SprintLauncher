using System.Text.RegularExpressions;

namespace SprintLauncher.Runners;

public sealed record QuotaResetSchedule(DateTimeOffset ResetAt);

public static class QuotaResetParser
{
    private static readonly Regex ResetTime = new(
        @"\breset(?:s|ting)?(?:\s+(?:at|around|vers|a|à))?\s+(?<time>(?:[01]?\d|2[0-3])(?::[0-5]\d)?\s*(?:am|pm)?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static QuotaResetSchedule? TryParse(string? output, string? errorOutput, DateTimeOffset now)
    {
        foreach (var text in new[] { output, errorOutput })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var match = ResetTime.Match(text);
            if (!match.Success) continue;

            if (TryParseTime(match.Groups["time"].Value, now, out var resetAt))
                return new QuotaResetSchedule(resetAt);
        }

        return null;
    }

    private static bool TryParseTime(string raw, DateTimeOffset now, out DateTimeOffset resetAt)
    {
        resetAt = default;
        var text = raw.Trim().Replace(" ", "", StringComparison.Ordinal);
        var amPm = text.EndsWith("am", StringComparison.OrdinalIgnoreCase)
            ? "am"
            : text.EndsWith("pm", StringComparison.OrdinalIgnoreCase) ? "pm" : null;

        if (amPm is not null)
            text = text[..^2];

        var parts = text.Split(':');
        if (parts.Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], out var hour)) return false;
        var minute = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], out minute)) return false;
        if (minute is < 0 or > 59) return false;

        if (amPm is not null)
        {
            if (hour is < 1 or > 12) return false;
            if (hour == 12) hour = 0;
            if (amPm.Equals("pm", StringComparison.OrdinalIgnoreCase)) hour += 12;
        }
        else if (hour is < 0 or > 23)
        {
            return false;
        }

        resetAt = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset);
        if (resetAt <= now)
            resetAt = resetAt.AddDays(1);
        return true;
    }
}
