namespace LauncherGo.Domains.Features;

public static class ExperimentalFeatures
{
#if EXPERIMENTAL_ANTICHEAT
    public static bool AntiCheatEnabled => true;
#else
    public static bool AntiCheatEnabled => false;
#endif
}
