using System.Diagnostics;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerHostRuntimeStagerTests
{
    [Fact]
    public void Prepare_StagesSingleFileHostOutsideBuildOutput()
    {
        var sourceDirectory = Path.Combine(Path.GetTempPath(), $"launchergo-host-source-{Guid.NewGuid():N}");
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"launchergo-host-runtime-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(sourceDirectory, "LauncherGo.ServerHost.exe");

        try
        {
            Directory.CreateDirectory(sourceDirectory);
            File.WriteAllText(sourcePath, "single-file-host");

            var stagedPath = ServerHostRuntimeStager.Prepare(sourcePath, runtimeRoot);

            Assert.True(File.Exists(stagedPath));
            Assert.NotEqual(Path.GetFullPath(sourcePath), Path.GetFullPath(stagedPath));
            Assert.StartsWith(
                Path.GetFullPath(runtimeRoot),
                Path.GetFullPath(stagedPath),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal("single-file-host", File.ReadAllText(stagedPath));
        }
        finally
        {
            Directory.Delete(sourceDirectory, recursive: true);
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Prepare_StagesFrameworkDependentHostWithRunnableDependencies()
    {
        var projectOutput = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "LauncherGo.ServerHost",
            "bin",
            "Release",
            "net10.0"));
        var sourcePath = Path.Combine(projectOutput, "LauncherGo.ServerHost.exe");
        var runtimeRoot = Path.Combine(Path.GetTempPath(), $"launchergo-host-runtime-{Guid.NewGuid():N}");

        Assert.True(File.Exists(sourcePath), $"ServerHost build output not found: {sourcePath}");

        try
        {
            var stagedPath = ServerHostRuntimeStager.Prepare(sourcePath, runtimeRoot);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = stagedPath,
                WorkingDirectory = Path.GetDirectoryName(stagedPath)!,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            Assert.NotNull(process);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotEqual(unchecked((int)0xE0434352), process.ExitCode);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(stagedPath)!, "LauncherGo.Services.dll")));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(stagedPath)!,
                "runtimes",
                "win",
                "lib",
                "net10.0",
                "System.Management.dll")));
            Assert.True(File.Exists(Path.Combine(
                Path.GetDirectoryName(stagedPath)!,
                "runtimes",
                System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
                "native",
                "e_sqlite3.dll")));
        }
        finally
        {
            if (Directory.Exists(runtimeRoot))
                Directory.Delete(runtimeRoot, recursive: true);
        }
    }
}
