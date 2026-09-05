namespace LauncherGo.Services;

internal static class ServerBroadcastLogParser
{
    private const string Marker = "Message to all in group";

    public static bool TryParse(string value, out string content)
    {
        content = string.Empty;
        var markerIndex = value.IndexOf(Marker, StringComparison.OrdinalIgnoreCase);
        var colonIndex = markerIndex < 0 ? -1 : value.IndexOf(':', markerIndex + Marker.Length);
        if (colonIndex < 0 || colonIndex + 1 >= value.Length) return false;
        content = value[(colonIndex + 1)..].Replace('\r', ' ').Replace('\n', ' ').Trim();
        return content.Length > 0;
    }
}
