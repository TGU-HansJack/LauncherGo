using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AutomationBackupOutputTests
{
    [Fact]
    public async Task BackupFileReadinessWaitsForNonEmptyStableFile()
    {
        var root = Directory.CreateTempSubdirectory("launchergo-backup-readiness-");
        try
        {
            var backupPath = Path.Combine(root.FullName, "world.vcdbs");
            await File.WriteAllBytesAsync(backupPath, []);

            var emptyResult = await AutomationService.WaitForBackupFileReadyAsync(
                backupPath,
                TimeSpan.FromMilliseconds(180),
                TimeSpan.FromMilliseconds(20));
            Assert.False(emptyResult);

            var readyTask = AutomationService.WaitForBackupFileReadyAsync(
                backupPath,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(20));
            await File.WriteAllBytesAsync(backupPath, Enumerable.Repeat((byte)0x5A, 4096).ToArray());

            Assert.True(await readyTask);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

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
    [InlineData("[Notification] 无法运行此备份。备份已在进行中")]
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
