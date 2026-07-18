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
        public static string GenerateForDoor(EntryDoorController door, Vector3 pos, Quaternion rot)
        {
            if (door == null) return "no door.";
            if (string.IsNullOrEmpty(door.DungeonType)) return "set a dungeon type first.";

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

            string result = FiresCore.Bridge.FiresDungeonBridge.SpawnDungeonAt(spec, pos, rot);
            // Only flag the door "generated" when the spawn actually placed the dungeon — a failure (e.g. ZoneLocation
            // unavailable) must NOT paint the door green, or it looks done while y+5000 is empty.
            if (result != null && result.StartsWith("dungeon spawned")) door.Generated = true;
            return result;
        }

        /// <summary>
        /// Server-authoritative teardown of the dungeon a door spawned when that door is removed. Rides the shared
        /// Core teardown (FiresDungeonTeardown): interior at +5000 (rooms/portal/env box/props — live objects AND
        /// orphan ZDOs the old loaded-only sweep missed), the location's surface structures, and the zone's
        /// location-instance record. The spec is resolved from the zone's DG instance; a null spec still runs the
        /// generic interior sweep. No-op off the server.
        /// </summary>
        public static void CleanupDungeonForDoor(Vector3 doorPos)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            FiresCore.Dungeon.DungeonSpec spec = null;
            Vector2i zone = ZoneSystem.GetZone(doorPos);
            foreach (var dg in UnityEngine.Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None))
            {
                if (dg == null || dg.gameObject == null) continue;
                if (ZoneSystem.GetZone(dg.transform.position) != zone) continue;
                spec = FiresCore.Dungeon.FiresDungeonRegistry.MatchForDg(dg.gameObject.name);
                if (spec != null) break;
            }

            int destroyed = FiresCore.Bridge.FiresDungeonBridge.DestroyDungeonAt(spec, doorPos);
            Debug.Log($"[FiresDungeonMaster] door removed at {doorPos}: tore down dungeon ({(spec != null ? spec.DgNamePrefix : "no spec match")}) — {destroyed} object(s).");
        }
    }
}
