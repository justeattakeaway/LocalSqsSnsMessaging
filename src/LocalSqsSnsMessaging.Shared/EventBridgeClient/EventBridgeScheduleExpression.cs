using System.Globalization;

namespace LocalSqsSnsMessaging;

/// <summary>
/// Validates a rule's <c>ScheduleExpression</c> against EventBridge's grammar: <c>rate(value unit)</c>
/// or a six-field <c>cron(minutes hours day-of-month month day-of-week year)</c>.
/// See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-scheduled-rule-pattern.html
/// </summary>
/// <remarks>
/// <see cref="EventBridgeSchedule"/> computes when an accepted schedule fires.
/// </remarks>
internal static class EventBridgeScheduleExpression
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private static readonly CronField Minutes = new(0, 59);
    private static readonly CronField Hours = new(0, 23);
    private static readonly CronField DayOfMonth = new(1, 31);
    private static readonly CronField Month = new(1, 12, MonthNames);
    private static readonly CronField DayOfWeek = new(1, 7, DayNames);
    private static readonly CronField Year = new(1970, 2199, wraps: false);

    /// <summary>
    /// Whether <paramref name="expression"/> is a schedule EventBridge accepts. A rate needs a value of
    /// at least 1 and a unit of minute(s), hour(s) or day(s), singular exactly when the value is 1. A
    /// cron needs six fields, and exactly one of day-of-month and day-of-week must be <c>?</c>.
    /// </summary>
    public static bool IsValid(string expression)
    {
        if (TryUnwrap(expression, "rate(", out var rate))
        {
            return IsValidRate(rate);
        }

        return TryUnwrap(expression, "cron(", out var cron) && IsValidCron(cron);
    }

    private static bool TryUnwrap(string expression, string prefix, out string body)
    {
        if (expression.StartsWith(prefix, StringComparison.Ordinal) && expression.EndsWith(')'))
        {
            body = expression.Substring(prefix.Length, expression.Length - prefix.Length - 1);
            return true;
        }

        body = "";
        return false;
    }

    private static bool IsValidRate(string rate)
    {
        var parts = rate.Split(' ');
        if (parts.Length != 2 || !TryParseNumber(parts[0], out var value) || value < 1)
        {
            return false;
        }

        var unit = parts[1];
        var singular = unit is "minute" or "hour" or "day";
        var plural = unit is "minutes" or "hours" or "days";
        return value == 1 ? singular : plural;
    }

    private static bool IsValidCron(string cron)
    {
        var fields = cron.Split(' ');
        if (fields.Length != 6)
        {
            return false;
        }

        var dayOfMonth = fields[2];
        var dayOfWeek = fields[4];

        // One of the two day fields must be '?', and only one: they cannot both constrain the day.
        if ((dayOfMonth == "?") == (dayOfWeek == "?"))
        {
            return false;
        }

        return IsValidList(fields[0], Minutes, IsPlainItem)
            && IsValidList(fields[1], Hours, IsPlainItem)
            && (dayOfMonth == "?" || IsValidList(dayOfMonth, DayOfMonth, IsDayOfMonthItem))
            && IsValidList(fields[3], Month, IsPlainItem)
            && (dayOfWeek == "?" || IsValidList(dayOfWeek, DayOfWeek, IsDayOfWeekItem))
            && IsValidList(fields[5], Year, IsPlainItem);
    }

    private static bool IsValidList(string field, CronField spec, Func<string, CronField, bool> isValidItem)
    {
        var items = field.Split(',');
        foreach (var item in items)
        {
            if (!isValidItem(item, spec))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary><c>*</c>, a value, a range <c>a-b</c>, or any of those with an increment <c>/n</c>.</summary>
    private static bool IsPlainItem(string item, CronField spec)
    {
        var slash = item.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            return TryParseNumber(item[(slash + 1)..], out var increment)
                && increment >= 1
                && IsPlainItem(item[..slash], spec);
        }

        if (item == "*")
        {
            return true;
        }

        var dash = item.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            // A range in a cyclic field may wrap (22-2, FRI-MON); a year range cannot.
            return spec.TryParse(item[..dash], out var first)
                && spec.TryParse(item[(dash + 1)..], out var last)
                && (spec.Wraps || first <= last);
        }

        return spec.TryParse(item, out _);
    }

    /// <summary>Day-of-month also takes <c>L</c> (last day), <c>LW</c> and <c>nW</c> (nearest weekday).</summary>
    private static bool IsDayOfMonthItem(string item, CronField spec)
    {
        if (item is "L" or "LW")
        {
            return true;
        }

        return item.EndsWith('W')
            ? spec.TryParse(item[..^1], out _)
            : IsPlainItem(item, spec);
    }

    /// <summary>Day-of-week also takes <c>L</c>, <c>dL</c> (last such day) and <c>d#n</c> (nth such day).</summary>
    private static bool IsDayOfWeekItem(string item, CronField spec)
    {
        if (item == "L")
        {
            return true;
        }

        var hash = item.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            return spec.TryParse(item[..hash], out _)
                && TryParseNumber(item[(hash + 1)..], out var nth)
                && nth is >= 1 and <= 5;
        }

        return item.EndsWith('L')
            ? spec.TryParse(item[..^1], out _)
            : IsPlainItem(item, spec);
    }

    private static bool TryParseNumber(string text, out int value)
    {
        value = 0;
        return text.Length > 0
            && text.All(c => c is >= '0' and <= '9')
            && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// The numeric range of one cron field, the names (e.g. <c>JAN</c>, <c>MON</c>) it also accepts,
    /// and whether a range in it may wrap past its maximum.
    /// </summary>
    private sealed class CronField(int min, int max, string[]? names = null, bool wraps = true)
    {
        public bool Wraps => wraps;

        public bool TryParse(string text, out int value)
        {
            if (names is not null)
            {
                var index = Array.IndexOf(names, text.ToUpperInvariant());
                if (index >= 0)
                {
                    value = min + index;
                    return true;
                }
            }

            return TryParseNumber(text, out value) && value >= min && value <= max;
        }
    }
}
