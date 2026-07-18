using System;
using System.Linq;
using System.Text;
using FiresDungeonMaster.Editor;
using FiresDungeonMaster.Sections;
using UnityEngine;

namespace FiresDungeonMaster.Modules
{
    /// <summary>
    /// Console commands for the section save/load loop (P4). Registered once from Plugin.Setup. Dedi-safe: pure
    /// Terminal.ConsoleCommand registrations (no UI), each carrying the no-local-player / server branch so the
    /// dedicated-server console works.
    ///
    /// Server-authority model (mirrors MausoleumCommands): SAVE is an admin-gated file write (host or admin
    /// client whose world view holds the captured objects); PLACE creates persistent ZDOs, so it runs on the
    /// host directly and returns a host-only message from a remote client (the hammer place stub in P5 is the
    /// client path).
    ///
    ///   firesdungeon_save   [name]          - capture EVERY object inside the footprint you're aiming at -> .blueprint
    ///   firesdungeon_list   [dungeonType]   - list saved sections (optionally one dungeon-type)
    ///   firesdungeon_place  <id> [fwd]       - place a saved section in front of you (host) at fwd metres
    /// </summary>
    internal static class DungeonMasterCommands
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("firesdungeon_save",
                "Capture every object inside the Dungeon Section Footprint you are aiming at and save it as a .blueprint section. Optional arg: a save name to disambiguate same-shape sections.",
                (Terminal.ConsoleEventArgs args) => args.Context?.AddString("[FiresDungeonMaster] " + Save(args)));

            new Terminal.ConsoleCommand("firesdungeon_list",
                "List saved dungeon sections. Optional arg: a dungeon-type name to filter by.",
                (Terminal.ConsoleEventArgs args) => args.Context?.AddString("[FiresDungeonMaster] " + List(args)));

            new Terminal.ConsoleCommand("firesdungeon_place",
                "Place a saved section in front of you (host/server). Args: <sectionIdOrName> [forwardMetres=6].",
                (Terminal.ConsoleEventArgs args) => args.Context?.AddString("[FiresDungeonMaster] " + Place(args)));

            new Terminal.ConsoleCommand("firesdungeon_browser",
                "Open the Saved Section Browser: list saved sections by dungeon-type and place one (client UI; host to place).",
                (Terminal.ConsoleEventArgs args) =>
                {
                    FiresDungeonMaster.UI.BlueprintBenchPanel.Toggle();
                    args.Context?.AddString("[FiresDungeonMaster] blueprint bench toggled.");
                });

            new Terminal.ConsoleCommand("firesdungeon_lockpick",
                "Open the lock-picking minigame directly to preview/test it (client UI). Optional arg: quality (Easy | Intermediate | Hard | Very Hard).",
                (Terminal.ConsoleEventArgs args) =>
                {
                    string q = (args.Args != null && args.Args.Length > 1) ? string.Join(" ", args.Args, 1, args.Args.Length - 1) : "Intermediate";
                    float factor = FiresDungeonMaster.Editor.LockpickSkill.LocalFactor();   // preview reflects your Lockpicking effectiveness
                    FiresDungeonMaster.UI.LockpickPanel.Open(q, 5, 0.15f, factor,
                        () => Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=#7cff7c>Picked!</color>"),
                        () => Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "The lock holds fast."),
                        () => Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "<color=#ff8080>Your lockpick snapped!</color> (test — nothing consumed)"));
                    args.Context?.AddString("[FiresDungeonMaster] lockpick minigame opened.");
                });
        }

        // -- firesdungeon_save ------------------------------------------------------------------------------

        private static string Save(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null) return "not in a world.";
            if (!IsAdmin()) return "admin only.";

            SectionFootprintController footprint = FindAimedFootprint() ?? FindNearestFootprint();
            if (footprint == null)
                return "no footprint found - aim at (or stand inside) a placed Dungeon Section Footprint.";

            string saveName = (args.Args != null && args.Args.Length > 1) ? args.Args[1] : null;

            SectionCapture.CaptureResult r = SectionCapture.CaptureAndSave(footprint, saveName);
            return r.Message;
        }

        // -- firesdungeon_list ------------------------------------------------------------------------------

        private static string List(Terminal.ConsoleEventArgs args)
        {
            try
            {
                SectionLibrary lib = FdmPaths.OpenLibrary();
                string filter = (args.Args != null && args.Args.Length > 1) ? args.Args[1] : null;

                var sections = string.IsNullOrEmpty(filter)
                    ? lib.ListAllSections()
                    : lib.ListSectionsByDungeonType(filter);

                if (sections.Count == 0)
                    return string.IsNullOrEmpty(filter)
                        ? "no saved sections."
                        : $"no sections of dungeon-type '{filter}'.";

                var sb = new StringBuilder();
                sb.AppendLine($"{sections.Count} section(s):");
                foreach (var s in sections.OrderBy(s => s.DungeonType).ThenBy(s => s.SectionId))
                    sb.AppendLine($"  {s.SectionId}  [{(string.IsNullOrEmpty(s.DungeonType) ? "(no type)" : s.DungeonType)}] " +
                                  $"{s.Role} {s.Footprint.x}x{s.Footprint.y} st{s.Stories} - {s.Objects.Count} objects");

                // Also list the distinct dungeon-types (no built-in distinct-type API - derive client-side).
                var types = lib.ListAllSections()
                    .Select(s => s.DungeonType)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (types.Count > 0)
                    sb.AppendLine($"dungeon-types: {string.Join(", ", types)}");

                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"list failed: {ex.Message}";
            }
        }

        // -- firesdungeon_place -----------------------------------------------------------------------------

        private static string Place(Terminal.ConsoleEventArgs args)
        {
            if (ZNet.instance == null) return "not in a world.";
            if (!IsAdmin()) return "admin only.";
            if (args.Args == null || args.Args.Length < 2) return "usage: firesdungeon_place <sectionIdOrName> [forwardMetres].";
            string id = args.Args[1];

            // Placement creates persistent ZDOs -> must run on the authority. A remote admin client can't spawn
            // persistent objects directly; the hammer place stub (P5) is the client path.
            if (!ZNet.instance.IsServer())
                return "place must run on the host/server (use the hammer 'Place Section' on a client).";

            var player = Player.m_localPlayer;

            // Dedicated-server console (no local player): place at world origin ground.
            if (player == null)
            {
                Vector3 origin = Vector3.zero;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(origin, out float ogh)) origin.y = ogh;
                return SectionPlacer.PlaceById(id, origin, Quaternion.identity).Message;
            }

            float forward = 6f;
            if (args.Args.Length > 2 && float.TryParse(args.Args[2], out float f)) forward = f;
            Vector3 dir = player.transform.forward; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward; else dir.Normalize();
            Vector3 pos = player.transform.position + dir * forward;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float gh)) pos.y = gh;

            return SectionPlacer.PlaceById(id, pos, rot).Message;
        }

        // -- admin gate -------------------------------------------------------------------------------------

        // One canonical admin check (Valheim's synced LocalPlayerIsAdminOrHost + host/server + Core lockExempt).
        private static bool IsAdmin() => FiresDungeonMaster.Editor.SectionFootprintController.IsAdmin();

        // -- footprint discovery ----------------------------------------------------------------------------

        // Raycast from the camera to find the footprint the player is aiming at. Null on dedicated server.
        private static SectionFootprintController FindAimedFootprint()
        {
            var player = Player.m_localPlayer;
            if (player == null || GameCamera.instance == null) return null;

            Transform cam = GameCamera.instance.transform;
            if (Physics.Raycast(cam.position, cam.forward, out RaycastHit hit, 50f,
                                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                var c = hit.collider.GetComponent<SectionFootprintController>()
                        ?? hit.collider.GetComponentInParent<SectionFootprintController>();
                if (c != null) return c;
            }
            return null;
        }

        // Fallback: the footprint whose box CONTAINS the player, else the nearest one. Works when the player is
        // standing inside the room they just built (the common case for save).
        private static SectionFootprintController FindNearestFootprint()
        {
            Vector3 probe = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;

            SectionFootprintController best = null;
            float bestDist = float.MaxValue;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<SectionFootprintController>())
            {
                if (c == null) continue;
                if (c.ContainsPoint(probe)) return c; // player is inside this footprint - unambiguous
                float d = Vector3.Distance(probe, c.transform.position);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }
    }
}
