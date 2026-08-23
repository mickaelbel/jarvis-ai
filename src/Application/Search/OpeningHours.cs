using System.Globalization;
using System.Text.RegularExpressions;

namespace JarvisAI.Application.Search;

public static class OpeningHours
{
    private static readonly Regex SegmentRegex =
        new(@"^\s*(?<days>[A-Za-z]{2}(?:\s*-\s*[A-Za-z]{2})?)\s+(?<times>.+?)\s*$", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> DayIndex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mo"] = 1, ["tu"] = 2, ["we"] = 3, ["th"] = 4, ["fr"] = 5, ["sa"] = 6, ["su"] = 0
    };

    public static bool? IsOpenNow(string? openingHours, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(openingHours))
            return null;

        var current = now ?? DateTimeOffset.UtcNow;
        var text = openingHours.Trim();

        if (text.Contains("24/7", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("open 24", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("24 heures", StringComparison.OrdinalIgnoreCase))
            return true;

        var segments = text.Split(';', StringSplitOptions.RemoveEmptyEntries);
        var parsedAny = false;
        var currentDay = (int)current.DayOfWeek;
        var currentMinutes = current.TimeOfDay.TotalMinutes;

        foreach (var rawSegment in segments)
        {
            var segment = rawSegment.Trim();
            var match = SegmentRegex.Match(segment);
            if (!match.Success)
            {
                if (segment.StartsWith("off", StringComparison.OrdinalIgnoreCase))
                {
                    parsedAny = true;
                    continue;
                }
                if (segment.StartsWith("closed", StringComparison.OrdinalIgnoreCase))
                {
                    parsedAny = true;
                    continue;
                }
                continue;
            }

            var days = ParseDays(match.Groups["days"].Value);
            var times = match.Groups["times"].Value;
            var timeRanges = times.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var dayOpen = days.Contains(currentDay);
            parsedAny = true;

            if (!dayOpen)
                continue;

            foreach (var rawRange in timeRanges)
            {
                var range = rawRange.Trim();
                if (range.StartsWith("off", StringComparison.OrdinalIgnoreCase) ||
                    range.StartsWith("closed", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (TryParseRange(range, out var from, out var to) &&
                    currentMinutes >= from && currentMinutes < to)
                    return true;
            }
        }

        return parsedAny ? false : null;
    }

    private static HashSet<int> ParseDays(string days)
    {
        var set = new HashSet<int>();
        var parts = days.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
            if (DayIndex.TryGetValue(parts[0].Trim(), out var start) &&
                DayIndex.TryGetValue(parts[1].Trim(), out var end))
            {
                var i = start;
                while (true)
                {
                    set.Add(i);
                    if (i == end)
                        break;
                    i = (i + 1) % 7;
                }
                return set;
            }
        }
        if (parts.Length == 1 && DayIndex.TryGetValue(parts[0].Trim(), out var single))
            set.Add(single);
        return set;
    }

    private static bool TryParseRange(string range, out double from, out double to)
    {
        from = 0;
        to = 0;
        var match = Regex.Match(range, @"(?<from>\d{1,2}:\d{2})\s*-\s*(?<to>\d{1,2}:\d{2})");
        if (!match.Success)
            return false;
        if (!TryToMinutes(match.Groups["from"].Value, out from))
            return false;
        if (!TryToMinutes(match.Groups["to"].Value, out to))
            return false;
        return true;
    }

    private static bool TryToMinutes(string hhmm, out double minutes)
    {
        minutes = 0;
        var parts = hhmm.Split(':');
        if (parts.Length != 2)
            return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var m))
            return false;
        minutes = h * 60 + m;
        return h is >= 0 and < 24 && m is >= 0 and < 60;
    }
}
