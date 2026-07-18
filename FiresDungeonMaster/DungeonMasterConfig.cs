using BepInEx.Configuration;

namespace FiresDungeonMaster
{
    /// <summary>
    /// BepInEx config for FiresDungeonMaster. Bound once from Plugin.Setup; auto-discovered into the Core F8 config
    /// panel like the rest of the family. Read at authoring time (section save), so the local value is what matters.
    /// </summary>
    public static class DungeonMasterConfig
    {
        public static ConfigEntry<string> CenterpointFallbackPrefab;
        public static ConfigEntry<bool> VerboseLogging;
        public static ConfigEntry<bool> AllPiecesScalable;

        public static void Bind(ConfigFile cfg)
        {
            const string SECTIONS = "Sections";

            VerboseLogging = cfg.Bind("General", "Verbose Logging", false,
                "Log FiresDungeonMaster diagnostic detail (per-theme/room/item registration lines, guide-wall and " +
                "entry-door diagnostics). Warnings/errors always log.");

            AllPiecesScalable = cfg.Bind("Blueprints", "AllPiecesScalable", true,
                "Force every prefab scalable (Structure Tweaks' 'All objects scalable'): sets ZNetView.m_syncInitialScale " +
                "on all prefabs so blueprint SCALE persists to the ZDO — without it a scaled piece snaps back to 1.0 on " +
                "reload and never shows scaled to other peers. Hot-reloads. AUTO-STANDS-DOWN when the real Structure " +
                "Tweaks mod is installed (ST owns the flag then; this only fills its absence).");
            AllPiecesScalable.SettingChanged += (_, __) => Modules.AllScalablePatch.Update();

            CenterpointFallbackPrefab = cfg.Bind(SECTIONS, "CenterpointFallbackPrefab", "Pickable_Mushroom_yellow",
                "Vanilla prefab written at a saved section's centerpoint (local origin) so the .blueprint stays placeable " +
                "by Expand World / Infinity Hammer / PlanBuild WITHOUT FiresDungeonMaster loaded. With the mod this marker " +
                "is skipped as content (the footprint box reconstructs it); without the mod it materialises as this harmless " +
                "prop. Default is the yellow mushroom; e.g. 'GlowingMushroom' for a glowing anchor.");
        }

        /// <summary>The configured fallback prefab, guarded to a sane default if unbound/blank.</summary>
        public static string FallbackPrefab =>
            CenterpointFallbackPrefab != null && !string.IsNullOrWhiteSpace(CenterpointFallbackPrefab.Value)
                ? CenterpointFallbackPrefab.Value.Trim()
                : "Pickable_Mushroom_yellow";
    }
}
