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
        public static ConfigEntry<bool> DungeonsBuildTab;   // 0.1.133
        public static ConfigEntry<bool> ClearZoneOnLastEntrance;   // 0.1.190
        public static ConfigEntry<bool> EntranceAutoLinkExperimental;   // 0.1.147
        public static ConfigEntry<bool> VanillaFriendly;   // 0.1.143 (key only; the mode itself is the next build)
        public static ConfigEntry<bool> LiveRoomChildren;   // 0.1.162
        public static ConfigEntry<bool> BuilderClearGround;   // 0.1.166
        // 0.1.125: opens the dungeon presets window (admin, hammer out).
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> DungeonPresetsKey;
        public static ConfigEntry<BepInEx.Configuration.KeyboardShortcut> EnvBoxPresetsKey;   // 0.1.136
        static readonly System.Collections.Generic.Dictionary<string, ConfigEntry<bool>> _roomTabs =
            new System.Collections.Generic.Dictionary<string, ConfigEntry<bool>>();
        public static bool RoomTabEnabled(string tab) => !_roomTabs.TryGetValue(tab, out var e) || e.Value;

        public static void Bind(ConfigFile cfg)
        {
            const string SECTIONS = "Sections";

            // 0.1.170 (Fire 10-20 player-facing config rule): one short plain line per key, nothing pre-filled; without Show Advanced
            // only the keybinds and the two hammer toggles show (Plugin.cs MarkAdvanced). Section / key names and types unchanged.
            VerboseLogging = cfg.Bind("General", "Verbose Logging", false,
                "Extra detail in the log, for troubleshooting.");

            SectionArchiveKey = cfg.Bind("Blueprints", "Section Archive key", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.Y, UnityEngine.KeyCode.LeftShift),
                "Admins: opens the dungeon section archive.");
            AllPiecesScalable = cfg.Bind("Blueprints", "AllPiecesScalable", true,
                "Scaled pieces keep their size after a reload and for other players.");
            AllPiecesScalable.SettingChanged += (_, __) => Modules.AllScalablePatch.Update();

            DungeonPresetsKey = cfg.Bind("Presets", "Dungeon presets key", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.U, UnityEngine.KeyCode.LeftShift),
                "Admins, hammer out: opens the dungeon presets.");
            EnvBoxPresetsKey = cfg.Bind("Presets", "Env box presets key", new BepInEx.Configuration.KeyboardShortcut(UnityEngine.KeyCode.Y, UnityEngine.KeyCode.LeftShift),
                "Admins, hammer out: opens the environment box presets.");
            VanillaRoomsOnHammer = cfg.Bind("Vanilla Rooms", "Rooms on the hammer", true,
                "Admins: every vanilla dungeon room on the hammer. Applies at the next login.");
            DungeonsBuildTab = cfg.Bind("Vanilla Rooms", "Dungeons build tab", true,
                "A Dungeons tab in the build menu. Applies at the next game start.");
            ClearZoneOnLastEntrance = cfg.Bind("Vanilla Rooms", "Clear zone on last entrance", false,
                "Server side. When the last entrance of a zone is removed, delete every object in that zone between Y 4000 and Y 6000, whoever made it. Players and their tombstones stay; a player inside falls. Parts of a neighbouring dungeon that reach into the zone are deleted too.");
            EntranceAutoLinkExperimental = cfg.Bind("Vanilla Rooms", "Entrance auto-link", true,
                "An entrance placed inside a dungeon becomes its way out.");
            LiveRoomChildren = cfg.Bind("Vanilla Rooms", "Live room contents", true,
                "Placed rooms come with working chests, pickables and spawners. Off: walls only.");
            BuilderClearGround = cfg.Bind("Room Builder", "Clear the ground under builders", true,   // 0.1.167: Fire OK, default ON
                "A room builder clears the grass and small plants under it; removing it puts them back.");
            VanillaFriendly = cfg.Bind("Vanilla Rooms", "Vanilla friendly", false,
                "Dungeons use only vanilla objects, so players without this mod can see them.");
            foreach (var tab in Editor.VanillaRoomPieces.Tabs)
                _roomTabs[tab.Label] = cfg.Bind("Vanilla Rooms", tab.Label, true, $"Show the '{tab.Label}' hammer tab. Applies at the next login.");

            CenterpointFallbackPrefab = cfg.Bind(SECTIONS, "CenterpointFallbackPrefab", "",
                "Object marking a saved section's centre when this mod is not installed. Empty: a yellow mushroom.");
        }

        /// <summary>The configured fallback prefab, guarded to a sane default if unbound/blank.</summary>
        public static string FallbackPrefab =>
            CenterpointFallbackPrefab != null && !string.IsNullOrWhiteSpace(CenterpointFallbackPrefab.Value)
                ? CenterpointFallbackPrefab.Value.Trim()
                : "Pickable_Mushroom_yellow";
    }
}
