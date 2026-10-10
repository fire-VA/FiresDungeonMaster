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
        // 0.1.123 (Fire's hand test: "no config settings to rebind the blueprints tab key"; Shift+B also fired VABackpacks' camp pack-up):
        // the bench / Section Archive key is a KeyboardShortcut, default Shift+Y (unused by vanilla and our mods; Shift+N is vanilla's skip-music)
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> SectionArchiveKey;
        // 0.1.124 (Fire: all vanilla dungeon rooms on the hammer, sorted by dungeon type).
        public static ConfigEntry<bool> VanillaRoomsOnHammer;
        // 0.1.125: opens the dungeon presets window (admin, hammer out).
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> DungeonPresetsKey;
        static readonly System.Collections.Generic.Dictionary<string, ConfigEntry<bool>> _roomTabs =
            new System.Collections.Generic.Dictionary<string, ConfigEntry<bool>>();
        public static bool RoomTabEnabled(string tab) => !_roomTabs.TryGetValue(tab, out var e) || e.Value;

        public static void Bind(ConfigFile cfg)
        {
            const string SECTIONS = "Sections";

            VerboseLogging = cfg.Bind("General", "Verbose Logging", false,
                "Log FiresDungeonMaster diagnostic detail (per-theme/room/item registration lines, guide-wall and " +
                "entry-door diagnostics). Warnings/errors always log.");

            SectionArchiveKey = cfg.Bind("Blueprints", "Section Archive key", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.Y, UnityEngine.KeyCode.LeftShift),
                "Opens the blueprint bench / Dungeon Section Archive (admin only). Was a hardcoded Shift+B, which also packed up VABackpacks camps.");
            AllPiecesScalable = cfg.Bind("Blueprints", "AllPiecesScalable", true,
                "Force every prefab scalable (Structure Tweaks' 'All objects scalable'): sets ZNetView.m_syncInitialScale " +
                "on all prefabs so blueprint SCALE persists to the ZDO — without it a scaled piece snaps back to 1.0 on " +
                "reload and never shows scaled to other peers. Hot-reloads. AUTO-STANDS-DOWN when the real Structure " +
                "Tweaks mod is installed (ST owns the flag then; this only fills its absence).");
            AllPiecesScalable.SettingChanged += (_, __) => Modules.AllScalablePatch.Update();

            DungeonPresetsKey = cfg.Bind("Presets", "Dungeon presets key", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.U, UnityEngine.KeyCode.LeftShift),
                "Admin, with the hammer out: opens the dungeon presets window (also on the hammer right-click of a dungeon door when FAP is installed).");
            VanillaRoomsOnHammer = cfg.Bind("Vanilla Rooms", "Rooms on the hammer", true,
                "Admin: every vanilla dungeon room on the hammer, one tab per dungeon type, placed as static geometry (no chests, " +
                "spawners or doors). Applies at the next login.");
            foreach (var tab in Editor.VanillaRoomPieces.Tabs)
                _roomTabs[tab.Label] = cfg.Bind("Vanilla Rooms", tab.Label, true, $"Show the '{tab.Label}' hammer tab. Applies at the next login.");

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
