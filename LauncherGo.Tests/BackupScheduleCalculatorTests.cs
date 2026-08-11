using LauncherGo.Domains.Models;
using Xunit;

namespace LauncherGo.Tests;

public sealed class BackupScheduleCalculatorTests
{
    [Fact]
    public void HourlyScheduleRunsOnceAtConfiguredMinuteForEveryHour()
    {
        var schedule = new BackupSchedule
        {
            Type = BackupScheduleType.Hourly,
            MinuteOfHour = 15,
            AnchorDate = "2026-08-11"
        };

        var occurrences = Enumerable.Range(0, 24)
            .Count(hour => BackupScheduleCalculator.IsDue(
                schedule,
                new DateTime(2026, 8, 11, hour, 15, 0)));

        Assert.Equal(24, occurrences);
        Assert.False(BackupScheduleCalculator.IsDue(schedule, new DateTime(2026, 8, 11, 8, 14, 0)));
    }

    [Theory]
    [InlineData(BackupScheduleType.Daily, "2026-08-11 03:00", true)]
    [InlineData(BackupScheduleType.Daily, "2026-08-11 03:01", false)]
    [InlineData(BackupScheduleType.Weekly, "2026-08-10 03:00", true)]
    [InlineData(BackupScheduleType.Weekly, "2026-08-11 03:00", false)]
    [InlineData(BackupScheduleType.Monthly, "2026-02-28 03:00", true)]
    public void CalendarSchedulesMatchExpectedMinute(
        BackupScheduleType type,
        string value,
        bool expected)
    {
        var schedule = new BackupSchedule
        {
            Type = type,
            DayOfMonth = 31,
            DayOfWeek = 1,
            Time = "03:00",
            AnchorDate = "2026-01-01"
        };

        Assert.Equal(expected, BackupScheduleCalculator.IsDue(schedule, DateTime.Parse(value)));
    }

    [Theory]
    [InlineData(BackupScheduleType.EveryNDays, 2, "2026-08-13 03:00", true)]
    [InlineData(BackupScheduleType.EveryNDays, 2, "2026-08-12 03:00", false)]
    [InlineData(BackupScheduleType.EveryNHours, 3, "2026-08-11 09:00", true)]
    [InlineData(BackupScheduleType.EveryNHours, 3, "2026-08-11 10:00", false)]
    [InlineData(BackupScheduleType.EveryNMinutes, 20, "2026-08-11 03:40", true)]
    [InlineData(BackupScheduleType.EveryNMinutes, 20, "2026-08-11 03:41", false)]
    public void IntervalSchedulesUsePersistedAnchor(
        BackupScheduleType type,
        int interval,
        string value,
        bool expected)
    {
        var schedule = new BackupSchedule
        {
            Type = type,
            Interval = interval,
            Time = "03:00",
            AnchorDate = "2026-08-11"
        };

        Assert.Equal(expected, BackupScheduleCalculator.IsDue(schedule, DateTime.Parse(value)));
    }

    [Fact]
    public void NextOccurrenceIsStrictlyAfterCurrentMinute()
    {
        var schedule = new BackupSchedule
        {
            Type = BackupScheduleType.Hourly,
            MinuteOfHour = 0,
            AnchorDate = "2026-08-11"
        };

        var next = BackupScheduleCalculator.GetNextOccurrence(
            schedule,
            new DateTime(2026, 8, 11, 13, 0, 0));

        Assert.Equal(new DateTime(2026, 8, 11, 14, 0, 0), next);
    }

    [Fact]
    public void PreviewReturnsFiveUpcomingOccurrences()
    {
        var schedule = new BackupSchedule
        {
            Type = BackupScheduleType.Hourly,
            MinuteOfHour = 0
        };

        var preview = BackupScheduleCalculator.GetNextOccurrences(
            schedule,
            new DateTime(2026, 8, 11, 13, 30, 0));

        Assert.Equal(5, preview.Count);
        Assert.Equal(new DateTime(2026, 8, 11, 14, 0, 0), preview[0]);
        Assert.Equal(new DateTime(2026, 8, 11, 18, 0, 0), preview[4]);
    }
}
