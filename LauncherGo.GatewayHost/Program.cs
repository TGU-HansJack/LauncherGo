using LauncherGo.Services;

namespace LauncherGo.GatewayHost;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await TcpGatewayHostRunner.RunAsync(args);
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
            File.AppendAllText(
                Path.Combine(logRoot, $"LauncherGo.GatewayHost-{DateTime.Now:yyyyMMdd}.log"),
                $"[{DateTimeOffset.Now:O}] Fatal GatewayHost failure{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // A logging failure must not hide the host exit code.
        }
    }
}
