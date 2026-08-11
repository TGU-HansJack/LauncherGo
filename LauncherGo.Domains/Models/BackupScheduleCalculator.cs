using System.Globalization;

namespace LauncherGo.Domains.Models;

/// <summary>
///     自动备份周期的归一化、到期和下次执行时间计算。
/// </summary>
public static class BackupScheduleCalculator
{
    public static BackupSchedule Normalize(BackupSchedule? source, DateTime now)
    {
        source ??= new BackupSchedule();
        var type = Enum.IsDefined(source.Type) ? source.Type : BackupScheduleType.Daily;
        var anchor = ParseDate(source.AnchorDate) ?? DateOnly.FromDateTime(now);
        return new BackupSchedule
        {
            Id = string.IsNullOrWhiteSpace(source.Id) ? Guid.NewGuid().ToString("N") : source.Id.Trim(),
            Enabled = source.Enabled,
            Type = type,
            DayOfMonth = Math.Clamp(source.DayOfMonth <= 0 ? 1 : source.DayOfMonth, 1, 31),
            DayOfWeek = Math.Clamp(source.DayOfWeek <= 0 ? 1 : source.DayOfWeek, 1, 7),
            Time = NormalizeTime(source.Time, "03:00"),
            MinuteOfHour = Math.Clamp(source.MinuteOfHour, 0, 59),
            Interval = Math.Clamp(source.Interval <= 0 ? 1 : source.Interval, 1, 100_000),
            AnchorDate = anchor.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    public static bool IsDue(BackupSchedule source, DateTime minute)
    {
        if (!source.Enabled)
            return false;

        minute = TruncateToMinute(minute);
        var schedule = Normalize(source, minute);
        var time = ParseTime(schedule.Time);
        return schedule.Type switch
        {
            BackupScheduleType.Monthly =>
                minute.Day == Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(minute.Year, minute.Month)) &&
                minute.TimeOfDay == time,
            BackupScheduleType.Weekly =>
                ToScheduleDayOfWeek(minute.DayOfWeek) == schedule.DayOfWeek &&
                minute.TimeOfDay == time,
            BackupScheduleType.Daily => minute.TimeOfDay == time,
            BackupScheduleType.Hourly => minute.Minute == schedule.MinuteOfHour,
            BackupScheduleType.EveryNDays => IsIntervalDue(schedule, minute, TimeSpan.FromDays(schedule.Interval), time),
            BackupScheduleType.EveryNHours => IsIntervalDue(schedule, minute, TimeSpan.FromHours(schedule.Interval), time),
            BackupScheduleType.EveryNMinutes => IsIntervalDue(schedule, minute, TimeSpan.FromMinutes(schedule.Interval), time),
            _ => false
        };
    }

    public static DateTime? GetNextOccurrence(BackupSchedule source, DateTime from)
    {
        if (!source.Enabled)
            return null;

        from = TruncateToMinute(from);
        var schedule = Normalize(source, from);
        var time = ParseTime(schedule.Time);
        switch (schedule.Type)
        {
            case BackupScheduleType.Daily:
                return NextDaily(from, time);
            case BackupScheduleType.Hourly:
                return NextHourly(from, schedule.MinuteOfHour);
            case BackupScheduleType.Weekly:
                for (var offset = 0; offset <= 7; offset++)
                {
                    var candidateDate = from.Date.AddDays(offset);
                    var candidate = candidateDate.Add(time);
                    if (ToScheduleDayOfWeek(candidateDate.DayOfWeek) == schedule.DayOfWeek && candidate > from)
                        return candidate;
                }

                break;
            case BackupScheduleType.Monthly:
                for (var offset = 0; offset <= 13; offset++)
                {
                    var month = from.Date.AddMonths(offset);
                    var day = Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(month.Year, month.Month));
                    var candidate = new DateTime(month.Year, month.Month, day).Add(time);
                    if (candidate > from)
                        return candidate;
                }

                break;
            case BackupScheduleType.EveryNDays:
                return NextInterval(schedule, from, TimeSpan.FromDays(schedule.Interval), time);
            case BackupScheduleType.EveryNHours:
                return NextInterval(schedule, from, TimeSpan.FromHours(schedule.Interval), time);
            case BackupScheduleType.EveryNMinutes:
                return NextInterval(schedule, from, TimeSpan.FromMinutes(schedule.Interval), time);
        }

        return null;
    }

    public static IReadOnlyList<DateTime> GetNextOccurrences(
        BackupSchedule source,
        DateTime from,
        int count = 5)
    {
        if (count <= 0 || !source.Enabled)
            return [];

        var occurrences = new List<DateTime>(count);
        var cursor = from;
        for (var index = 0; index < count; index++)
        {
            var next = GetNextOccurrence(source, cursor);
            if (!next.HasValue)
                break;

            occurrences.Add(next.Value);
            cursor = next.Value;
        }

        return occurrences;
    }

    public static bool TryParseTime(string? value, out TimeSpan time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(value) || !TimeSpan.TryParse(value.Trim(), CultureInfo.InvariantCulture, out var parsed))
            return false;

        if (parsed < TimeSpan.Zero || parsed >= TimeSpan.FromDays(1))
            return false;

        time = new TimeSpan(parsed.Hours, parsed.Minutes, 0);
        return true;
    }

    private static bool IsIntervalDue(BackupSchedule schedule, DateTime minute, TimeSpan interval, TimeSpan time)
    {
        var anchorDate = ParseDate(schedule.AnchorDate) ?? DateOnly.FromDateTime(minute);
        var anchor = anchorDate.ToDateTime(TimeOnly.FromTimeSpan(time));
        if (minute < anchor)
            return false;

        var elapsed = minute - anchor;
        return elapsed.Ticks % interval.Ticks == 0;
    }

    private static DateTime NextInterval(BackupSchedule schedule, DateTime from, TimeSpan interval, TimeSpan time)
    {
        var anchorDate = ParseDate(schedule.AnchorDate) ?? DateOnly.FromDateTime(from);
        var anchor = anchorDate.ToDateTime(TimeOnly.FromTimeSpan(time));
        if (anchor > from)
            return anchor;

        var elapsedTicks = (from - anchor).Ticks;
        var periods = elapsedTicks / interval.Ticks + 1;
        return anchor.AddTicks(periods * interval.Ticks);
    }

    private static DateTime NextDaily(DateTime from, TimeSpan time)
    {
        var candidate = from.Date.Add(time);
        return candidate > from ? candidate : candidate.AddDays(1);
    }

    private static DateTime NextHourly(DateTime from, int minuteOfHour)
    {
        var candidate = new DateTime(from.Year, from.Month, from.Day, from.Hour, minuteOfHour, 0);
        return candidate > from ? candidate : candidate.AddHours(1);
    }

    private static DateTime TruncateToMinute(DateTime value) =>
        new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Kind);

    private static TimeSpan ParseTime(string value) =>
        TryParseTime(value, out var time) ? time : new TimeSpan(3, 0, 0);

    private static string NormalizeTime(string? value, string fallback)
    {
        return TryParseTime(value, out var time)
            ? $"{time.Hours:00}:{time.Minutes:00}"
            : fallback;
    }

    private static DateOnly? ParseDate(string? value)
    {
        return DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static int ToScheduleDayOfWeek(DayOfWeek dayOfWeek) =>
        dayOfWeek == DayOfWeek.Sunday ? 7 : (int)dayOfWeek;
}
