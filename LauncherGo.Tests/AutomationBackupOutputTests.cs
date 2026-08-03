using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AutomationBackupOutputTests
{
    [Theory]
    [InlineData("Backup complete")]
    [InlineData("Backup finished")]
    [InlineData("[Notification] 备份已完成。")]
    [InlineData("[Notification] 备份完成")]
    public void BackupCompletionMessagesAreRecognized(string line)
    {
        Assert.True(AutomationService.IsBackupCompletedLine(line));
        Assert.False(AutomationService.IsBackupFailedLine(line));
    }

    [Theory]
    [InlineData("Can't run backup")]
    [InlineData("backup is already in progress")]
    [InlineData("[Notification] 无法执行备份")]
    [InlineData("[Notification] 备份正在进行中")]
    [InlineData("[Notification] 备份已在进行中")]
    [InlineData("[Notification] 备份失败")]
    public void BackupFailureMessagesAreRecognized(string line)
    {
        Assert.True(AutomationService.IsBackupFailedLine(line));
        Assert.False(AutomationService.IsBackupCompletedLine(line));
    }
}
