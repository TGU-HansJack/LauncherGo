using System.Diagnostics;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerProcessLifetimeGuardTests
{
    [Fact]
    public void CurrentProcess_CanJoinAndNormallyDisposeJob()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var guard = new ServerProcessLifetimeGuard();
        guard.CompleteNormalShutdown();
    }

    [Fact]
    public async Task TerminatingJob_TerminatesAssignedProcess()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 60 > nul",
            UseShellExecute = false,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("Failed to start test process.");

        try
        {
            using (var guard = new ServerProcessLifetimeGuard(includeCurrentProcess: false))
            {
                guard.Add(process);
                Assert.False(process.HasExited);
                guard.TerminateForTest();
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
