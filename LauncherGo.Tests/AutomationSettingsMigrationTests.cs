using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AutomationSettingsMigrationTests
{
    [Fact]
    public void LegacyBackupTimesMigrateToDailySchedules()
    {
        var settings = AutomationSettingsService.Normalize(new AutomationSettings
        {
            BackupEnabled = true,
            BackupTimes = ["01:00", "13:30", "13:30"]
        });

        Assert.Empty(settings.BackupTimes);
        Assert.Collection(
            settings.BackupSchedules,
            first =>
            {
                Assert.Equal(BackupScheduleType.Daily, first.Type);
                Assert.Equal("01:00", first.Time);
            },
            second =>
            {
                Assert.Equal(BackupScheduleType.Daily, second.Type);
                Assert.Equal("13:30", second.Time);
            });
    }

    [Fact]
    public void BackupScheduleValuesAreNormalized()
    {
        var settings = AutomationSettingsService.Normalize(new AutomationSettings
        {
            BackupRetentionCount = -4,
            BackupSchedules =
            [
                new BackupSchedule
                {
                    Type = BackupScheduleType.Hourly,
                    MinuteOfHour = 72,
                    Interval = 0,
                    Time = "invalid"
                }
            ]
        });

        var schedule = Assert.Single(settings.BackupSchedules);
        Assert.Equal(0, settings.BackupRetentionCount);
        Assert.Equal(59, schedule.MinuteOfHour);
        Assert.Equal(1, schedule.Interval);
        Assert.Equal("03:00", schedule.Time);
        Assert.False(string.IsNullOrWhiteSpace(schedule.Id));
        Assert.False(string.IsNullOrWhiteSpace(schedule.AnchorDate));
    }
}
