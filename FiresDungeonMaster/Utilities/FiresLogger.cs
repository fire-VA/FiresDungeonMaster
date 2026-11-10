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

        // 0.1.202 (Fire 11-03: "all of our EXCESSIVE logging to be debug level not info level"): the one helper; the BepInEx source's Debug level, not Unity's Debug.Log (which records as Info).
        public static void LogDebug(string message) => FiresDungeonMasterPlugin.Log?.LogDebug(message != null && message.StartsWith(Tag) ? message.Substring(Tag.Length) : message);

        private const string Tag = "[FiresDungeonMaster] ";

        public static void LogInfo(string message) => LogDebug(message);

        public static void LogVerbose(string message) { if (VerboseEnabled) LogDebug(message); }

        public static void LogWarning(string message) => Log.Warning(message);

        public static void LogError(string message) => Log.Error(message);
    }
}
