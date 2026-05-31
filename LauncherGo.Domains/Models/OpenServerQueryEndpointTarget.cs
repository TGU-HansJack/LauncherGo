namespace LauncherGo.Domains.Models;

public static class OpenServerQueryEndpointTarget
{
    public const string QqRobot = "qqRobot";
    public const string MapWebsite = "mapWebsite";

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Equals(QqRobot, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("qq", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("robot", StringComparison.OrdinalIgnoreCase))
        {
            return QqRobot;
        }

        return MapWebsite;
    }

    public static bool IsMapWebsite(string? value)
    {
        return Normalize(value) == MapWebsite;
    }
}
