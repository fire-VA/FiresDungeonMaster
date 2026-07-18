using FiresCore.Logging;

namespace FiresDungeonMaster
{
    // FiresDungeonMaster's tagged logger. Thin wrapper over the shared FiresCore FiresLog engine
    // so every line carries the mod's own tag and verbose gating.
    public static class FiresLogger
    {
        private static readonly FiresLog Log = new FiresLog(
            "FiresDungeonMaster",
            () => DungeonMasterConfig.VerboseLogging?.Value ?? false);

        public static bool VerboseEnabled => Log.VerboseEnabled;

        public static void LogInfo(string message) => Log.Info(message);

        public static void LogVerbose(string message) => Log.Verbose(message);

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
