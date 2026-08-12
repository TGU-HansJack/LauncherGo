using LauncherGo.Services;

namespace LauncherGo.ServerHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await ServerProcessRelay.RunAsync(args);
        }
        catch (Exception ex)
        {
            TryWriteFatalLog(ex);
            return 1;
        }
    }

    private static void TryWriteFatalLog(Exception exception)
    {
        try
        {
            var logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LauncherGo",
                "logs");
            Directory.CreateDirectory(logRoot);
            var logPath = Path.Combine(logRoot, $"LauncherGo.ServerHost-{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(
                logPath,
                $"[{DateTimeOffset.Now:O}] Fatal ServerHost failure{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // A logging failure must not hide the original host exit code.
        }
    }
}
