using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// EntryDoorGeneration — the single server-side seam that turns an entry door's configured DungeonType into an
    /// actual dungeon via the shared FiresDungeon.Core engine. Runs on the server (host directly, remote clients via
    /// EntryDoorRpc). CUSTOM section-set dungeons use the existing DungeonMasterSpecBuilder → FiresDungeonBridge path
    /// (blocked until the Location/DG/Portal template is baked — see reference_dungeon_door_pipeline). VANILLA
    /// dungeons + the "empty room" env-box mode + display-name/discovery are Phase 3.
    /// </summary>
    internal static class EntryDoorGeneration
    {
        private const int MaxDungeonTypeLen = 64;

        // 0.1.187: what the server builds from. It rides the generate RPC (the door's ZDO may not have reached the server yet, and a
        // dedi usually has no door instance), and is checked by ValidateDoorConfig either way.
        internal struct DoorConfig
        {
            internal string DungeonType, Environment, KeyPrefab;
            internal bool IsVanilla, EmptyBox;
        }

        // 10-23 security audit: null when the door's ZDO config names only known values, else the reason.
        //   DungeonType: a vanilla label (vanilla doors) or a type present in the server's own section library (custom
        //   doors); empty-room doors accept either. Environment: EntryDoorController.Environments. KeyPrefab: empty or
        //   EntryDoorController.KeyOptions.
        private static string ValidateDoorConfig(DoorConfig door)
        {
            string type = door.DungeonType ?? "";
            if (type.Length > MaxDungeonTypeLen) return "dungeon type name too long.";
            foreach (char c in type) if (char.IsControl(c)) return "dungeon type name has control characters.";

            bool isVanillaLabel = System.Array.IndexOf(EntryDoorController.VanillaDungeons, type) >= 0;
            bool needCustom = !door.EmptyBox && !door.IsVanilla;
            bool needVanilla = !door.EmptyBox && door.IsVanilla;
            if (needVanilla && !isVanillaLabel) return $"unknown vanilla dungeon '{type}'.";
            if (needCustom || (door.EmptyBox && !isVanillaLabel))
            {
                bool known = false;
                try
                {
                    var lib = FiresDungeonMaster.Sections.FdmPaths.OpenLibrary();
                    foreach (var t in FiresDungeonMaster.Generation.DungeonGenerationCommands.DistinctTypes(lib))
                        if (string.Equals(t, type, System.StringComparison.OrdinalIgnoreCase)) { known = true; break; }
                }
                catch { known = false; }
                if (!known) return $"unknown dungeon type '{type}'.";
            }

            string env = door.Environment ?? "";
            if (env.Length > 0 && System.Array.IndexOf(EntryDoorController.Environments, env) < 0) return $"unknown environment '{env}'.";

            string key = door.KeyPrefab ?? "";
            if (key.Length > 0 && System.Array.IndexOf(EntryDoorController.KeyOptions, key) < 0) return $"unknown key '{key}'.";
            return null;
        }

        /// <summary>Builds the door's dungeon; a dungeon it already has is torn down first (picking a dungeon replaces the empty box). dungeonId = the new id on success.</summary>
        internal static string GenerateForDoor(DoorConfig door, long oldId, ZDOID doorId, Vector3 pos, Quaternion rot, out long dungeonId)
        {
            dungeonId = 0L;
            if (string.IsNullOrEmpty(door.DungeonType)) return "set a dungeon type first.";
            // 10-23 security audit (EntryDoorController.cs:193-200 / EntryDoorGeneration.cs:17-48, Low): the door's config
            // lives on a client-owned ZDO, so the server checks it against the known lists before building any spec.
            string invalid = ValidateDoorConfig(door);
            if (invalid != null) return "refused: " + invalid;

            // Server-authoritative ground snap (the door sits at its placed height; the dungeon anchors from there).
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float gh)) pos.y = gh;

            FiresCore.Dungeon.DungeonSpec spec;
            if (door.EmptyBox)
            {
                // Empty-room door: our template + env box, but NO procedural rooms — you teleport into a single
                // enclosed dark room (landing pad + exit door), not a dungeon. Honours the door's chosen environment.
                spec = FiresDungeonMaster.Generation.DungeonMasterSpecBuilder.BuildAndRegisterEmpty(door.DungeonType, door.Environment, out string ereason);
                if (spec == null) return "empty-room build failed: " + ereason;
            }
            else if (door.IsVanilla)
            {
                // A stock Valheim dungeon: our template + DG driven with the label's Room.Theme, no custom rooms.
                spec = FiresDungeonMaster.Generation.DungeonMasterSpecBuilder.BuildAndRegisterVanilla(door.DungeonType, out string vreason);
                if (spec == null) return "vanilla build failed: " + vreason;
            }
            else
            {
                // Custom section-set dungeon. BuildAndRegister returns null + a reason if the type has no usable
                // sections / the baked Location template isn't loaded; the door reports that instead of doing nothing.
                var lib = FiresDungeonMaster.Sections.FdmPaths.OpenLibrary();
                spec = FiresDungeonMaster.Generation.DungeonMasterSpecBuilder.BuildAndRegister(lib, door.DungeonType, out string reason);
                if (spec == null) return "build failed: " + reason;
            }

            // Honour the door's chosen interior environment (default is "Crypt"). Re-register so a reload keeps it.
            if (!string.IsNullOrEmpty(door.Environment) && door.Environment != "Nothing" && door.Environment != spec.EnvName)
            {
                spec.EnvName = door.Environment;
                FiresCore.Bridge.FiresDungeonBridge.RegisterDungeon(spec);
            }

            if (oldId != 0L) DungeonLink.Teardown(oldId, ZDOID.None, ZDOID.None, false);

            long id = DungeonLink.NewId();
            string result = DungeonLink.Stamped(id, () => FiresCore.Bridge.FiresDungeonBridge.SpawnDungeonAt(spec, pos, rot));
            // Only flag the door "generated" when the spawn actually placed the dungeon — a failure (e.g. ZoneLocation
            // unavailable) must NOT paint the door green, or it looks done while y+5000 is empty.
            if (result != null && result.StartsWith("dungeon spawned")) dungeonId = id;
            return result;
        }
    }
}
