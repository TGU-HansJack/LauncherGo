using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

internal static class AntiCheatAlertRelay
{
    internal const string AlertMarker = "[LauncherGoAntiCheat] ALERT";
    internal const string ActionMarker = "[LauncherGoAntiCheat] ACTION";

    public static bool TryBuildMessage(ServerOutputLine output, out string message)
    {
        message = string.Empty;
        if (output is null || string.IsNullOrWhiteSpace(output.ProfileId) || string.IsNullOrWhiteSpace(output.Line))
            return false;

        var alertIndex = output.Line.IndexOf(AlertMarker, StringComparison.Ordinal);
        var actionIndex = output.Line.IndexOf(ActionMarker, StringComparison.Ordinal);
        var markerIndex = SelectFirstMarkerIndex(alertIndex, actionIndex);
        if (markerIndex < 0)
            return false;

        var linePrefix = output.Line[..markerIndex];
        if (linePrefix.Contains("[Talk]", StringComparison.OrdinalIgnoreCase) ||
            linePrefix.Contains("[Chat]", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var profileName = NormalizeInlineText(
            string.IsNullOrWhiteSpace(output.ProfileName) ? output.ProfileId : output.ProfileName);
        var evidence = NormalizeInlineText(output.Line[markerIndex..]);
        if (string.IsNullOrWhiteSpace(evidence))
            return false;

        message = $"[反作弊][{profileName}] {evidence}";
        return true;
    }

    private static int SelectFirstMarkerIndex(int alertIndex, int actionIndex)
    {
        if (alertIndex < 0)
            return actionIndex;
        if (actionIndex < 0)
            return alertIndex;
        return Math.Min(alertIndex, actionIndex);
    }

    private static string NormalizeInlineText(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
