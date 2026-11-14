using System.Globalization;

namespace LocalSqsSnsMessaging;

/// <summary>
/// An EventBridge rule's <c>ScheduleExpression</c>, <c>rate(value unit)</c> or
/// <c>cron(minutes hours day-of-month month day-of-week year)</c>, evaluated in UTC.
/// See https://docs.aws.amazon.com/eventbridge/latest/userguide/eb-scheduled-rule-pattern.html
/// </summary>
/// <remarks>
/// This evaluates expressions; it does not validate them with EventBridge's messages. An expression
/// it cannot read never fires.
/// </remarks>
internal abstract class EventBridgeSchedule
{
    /// <summary>The first time the schedule fires strictly after <paramref name="after"/>, or null if it never fires again.</summary>
    public abstract DateTimeOffset? NextAfter(DateTimeOffset after);

    /// <summary>Reads a <c>rate(...)</c> or <c>cron(...)</c> expression.</summary>
    public static bool TryParse(string? expression, out EventBridgeSchedule schedule)
    {
        schedule = null!;
        if (expression is null || !expression.EndsWith(')'))
        {
            return false;
        }

        if (expression.StartsWith("rate(", StringComparison.Ordinal))
        {
            return RateSchedule.TryRead(expression["rate(".Length..^1], out schedule);
        }
        if (expression.StartsWith("cron(", StringComparison.Ordinal))
        {
            return CronSchedule.TryRead(expression["cron(".Length..^1], out schedule);
        }
        return false;
    }

    /// <summary><c>rate(value unit)</c>: fires every period, counted from when the rule was scheduled.</summary>
    private sealed class RateSchedule(TimeSpan period) : EventBridgeSchedule
    {
        public override DateTimeOffset? NextAfter(DateTimeOffset after) => after + period;

        public static bool TryRead(string body, out EventBridgeSchedule schedule)
        {
            schedule = null!;
            var parts = body.Split(' ');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value <= 0)
            {
                return false;
            }

            TimeSpan? unit = parts[1] switch
            {
                "minute" or "minutes" => TimeSpan.FromMinutes(1),
                "hour" or "hours" => TimeSpan.FromHours(1),
                "day" or "days" => TimeSpan.FromDays(1),
                _ => null
            };
            if (unit is null)
            {
                return false;
            }

            schedule = new RateSchedule(TimeSpan.FromTicks(unit.Value.Ticks * value));
            return true;
        }
    }

    /// <summary>
    /// <c>cron(...)</c> with EventBridge's six fields. Each field takes <c>*</c>, values, ranges,
    /// <c>/</c> increments and lists; day-of-month takes <c>?</c>, <c>L</c>, <c>LW</c> and <c>nW</c>;
    /// day-of-week (1 = SUN) takes <c>?</c>, <c>nL</c> and <c>n#k</c>.
    /// </summary>
    private sealed class CronSchedule : EventBridgeSchedule
    {
        private const int MinYear = 1970;
        private const int MaxYear = 2199;

        private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
        private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

        private bool[] _minutes = null!;
        private bool[] _hours = null!;
        private bool[] _months = null!;
        private bool[] _years = null!;

        // Day of month: any day, listed days, the last day (L), the last weekday (LW), or the
        // weekday nearest a day (nW).
        private bool _anyDayOfMonth;
        private bool[] _daysOfMonth = null!;
        private bool _lastDayOfMonth;
        private bool _lastWeekdayOfMonth;
        private readonly List<int> _nearestWeekdays = [];

        // Day of week: any day, listed days, the last given weekday of the month (nL), or the
        // k-th given weekday of the month (n#k).
        private bool _anyDayOfWeek;
        private bool[] _daysOfWeek = null!;
        private readonly List<int> _lastWeekdaysOfMonth = [];
        private readonly List<(int Day, int Ordinal)> _nthWeekdays = [];

        public static bool TryRead(string body, out EventBridgeSchedule schedule)
        {
            schedule = null!;
            var fields = body.Split([' '], StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 6)
            {
                return false;
            }

            var cron = new CronSchedule();
            if (!TryParseField(fields[0], 0, 59, null, out cron._minutes)
                || !TryParseField(fields[1], 0, 23, null, out cron._hours)
                || !cron.TryParseDayOfMonth(fields[2])
                || !TryParseField(fields[3], 1, 12, MonthNames, out cron._months)
                || !cron.TryParseDayOfWeek(fields[4])
                || !TryParseField(fields[5], MinYear, MaxYear, null, out cron._years))
            {
                return false;
            }

            schedule = cron;
            return true;
        }

        public override DateTimeOffset? NextAfter(DateTimeOffset after)
        {
            var start = after.UtcDateTime;
            start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, 0, DateTimeKind.Utc).AddMinutes(1);

            var day = start.Date;
            while (day.Year <= MaxYear)
            {
                if (!_years[day.Year - MinYear])
                {
                    day = new DateTime(day.Year + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                    continue;
                }
                if (!_months[day.Month - 1])
                {
                    day = new DateTime(day.Year, day.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
                    continue;
                }
                if (DayMatches(day) && FirstTimeOfDay(day, day == start.Date ? start : day) is { } match)
                {
                    return new DateTimeOffset(match);
                }
                day = day.AddDays(1);
            }
            return null;
        }

        private DateTime? FirstTimeOfDay(DateTime day, DateTime notBefore)
        {
            for (var hour = notBefore.Hour; hour < 24; hour++)
            {
                if (!_hours[hour])
                {
                    continue;
                }
                for (var minute = hour == notBefore.Hour ? notBefore.Minute : 0; minute < 60; minute++)
                {
                    if (_minutes[minute])
                    {
                        return day.AddHours(hour).AddMinutes(minute);
                    }
                }
            }
            return null;
        }

        private bool DayMatches(DateTime day)
        {
            var daysInMonth = DateTime.DaysInMonth(day.Year, day.Month);
            var dayOfMonthMatches = _anyDayOfMonth
                || _daysOfMonth[day.Day]
                || (_lastDayOfMonth && day.Day == daysInMonth)
                || (_lastWeekdayOfMonth && day.Day == NearestWeekday(day.Year, day.Month, daysInMonth))
                || _nearestWeekdays.Any(d => day.Day == NearestWeekday(day.Year, day.Month, Math.Min(d, daysInMonth)));

            var dayOfWeek = (int)day.DayOfWeek + 1;
            var dayOfWeekMatches = _anyDayOfWeek
                || _daysOfWeek[dayOfWeek]
                || (_lastWeekdaysOfMonth.Contains(dayOfWeek) && day.Day + 7 > daysInMonth)
                || _nthWeekdays.Contains((dayOfWeek, ((day.Day - 1) / 7) + 1));

            return dayOfMonthMatches && dayOfWeekMatches;
        }

        /// <summary>The weekday nearest the given day of the month, without leaving the month.</summary>
        private static int NearestWeekday(int year, int month, int day)
        {
            var daysInMonth = DateTime.DaysInMonth(year, month);
            return new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc).DayOfWeek switch
            {
                DayOfWeek.Saturday => day > 1 ? day - 1 : day + 2,
                DayOfWeek.Sunday => day < daysInMonth ? day + 1 : day - 2,
                _ => day
            };
        }

        private bool TryParseDayOfMonth(string field)
        {
            _daysOfMonth = new bool[32];
            if (field is "?" or "*")
            {
                _anyDayOfMonth = true;
                return true;
            }

            var listed = new List<string>();
            foreach (var item in field.Split(','))
            {
                if (item == "L")
                {
                    _lastDayOfMonth = true;
                }
                else if (item == "LW")
                {
                    _lastWeekdayOfMonth = true;
                }
                else if (item.EndsWith('W'))
                {
                    if (!TryParseValue(item[..^1], 1, 31, null, out var day))
                    {
                        return false;
                    }
                    _nearestWeekdays.Add(day);
                }
                else
                {
                    listed.Add(item);
                }
            }

            if (listed.Count > 0)
            {
                if (!TryParseField(string.Join(",", listed), 1, 31, null, out var days))
                {
                    return false;
                }
                Array.Copy(days, 0, _daysOfMonth, 1, 31);
            }
            return true;
        }

        private bool TryParseDayOfWeek(string field)
        {
            _daysOfWeek = new bool[8];
            if (field is "?" or "*")
            {
                _anyDayOfWeek = true;
                return true;
            }

            var listed = new List<string>();
            foreach (var item in field.Split(','))
            {
                var hash = item.IndexOf('#', StringComparison.Ordinal);
                if (hash > 0)
                {
                    if (!TryParseValue(item[..hash], 1, 7, DayNames, out var day)
                        || !TryParseValue(item[(hash + 1)..], 1, 5, null, out var ordinal))
                    {
                        return false;
                    }
                    _nthWeekdays.Add((day, ordinal));
                }
                else if (item.Length > 1 && item.EndsWith('L'))
                {
                    if (!TryParseValue(item[..^1], 1, 7, DayNames, out var day))
                    {
                        return false;
                    }
                    _lastWeekdaysOfMonth.Add(day);
                }
                else
                {
                    listed.Add(item);
                }
            }

            if (listed.Count > 0)
            {
                if (!TryParseField(string.Join(",", listed), 1, 7, DayNames, out var days))
                {
                    return false;
                }
                Array.Copy(days, 0, _daysOfWeek, 1, 7);
            }
            return true;
        }

        /// <summary>Parses a list of <c>*</c>, values, ranges and increments into the allowed values, indexed from <paramref name="min"/>.</summary>
        private static bool TryParseField(string field, int min, int max, string[]? names, out bool[] allowed)
        {
            allowed = new bool[max - min + 1];
            foreach (var item in field.Split(','))
            {
                var slash = item.IndexOf('/', StringComparison.Ordinal);
                var range = slash >= 0 ? item[..slash] : item;
                var step = 1;
                if (slash >= 0 && (!int.TryParse(item[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0))
                {
                    return false;
                }

                int first, last;
                if (range == "*")
                {
                    (first, last) = (min, max);
                }
                else
                {
                    var dash = range.IndexOf('-', StringComparison.Ordinal);
                    if (dash > 0)
                    {
                        if (!TryParseValue(range[..dash], min, max, names, out first)
                            || !TryParseValue(range[(dash + 1)..], min, max, names, out last))
                        {
                            return false;
                        }
                    }
                    else
                    {
                        if (!TryParseValue(range, min, max, names, out first))
                        {
                            return false;
                        }
                        last = slash >= 0 ? max : first;
                    }
                }

                if (first > last)
                {
                    return false;
                }
                for (var value = first; value <= last; value += step)
                {
                    allowed[value - min] = true;
                }
            }
            return true;
        }

        private static bool TryParseValue(string text, int min, int max, string[]? names, out int value)
        {
            if (names is not null)
            {
                var index = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    value = min + index;
                    return true;
                }
            }
            return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
        }
    }
}
