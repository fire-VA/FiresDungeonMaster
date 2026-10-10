using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Bridge;
using FiresCore.Dungeon;
using FiresDungeonMaster.Sections;
using UnityEngine;

namespace FiresDungeonMaster.Generation
{
    /// <summary>
    /// Console surface for P6 generation. Registered once from Plugin.Setup. Server-authoritative: the host/listen-
    /// server runs the spawn locally (it persists the ZDOs); a dedicated-server console with no local player spawns at
    /// world origin. Mirrors the FiresMausoleum / FiresDungeonMaster.Modules.DungeonMasterCommands authority branch and
    /// drives ONLY <see cref="FiresDungeonBridge"/>. The section library is opened the on-disk way via
    /// <see cref="FdmPaths.OpenLibrary"/> (ensures the shared PlanBuild folder exists, then constructs the library).
    ///
    /// Commands:
    ///   firesdungeon_types                     — list distinct dungeon-type names found in the shared library.
    ///   firesdungeon_sections [type]           — list sections (optionally filtered to a type).
    ///   firesdungeon_generate &lt;type&gt; [fwd] — build+register the type's spec and spawn it ahead of you (default 8m).
    ///   firesdungeon_worldgen &lt;type&gt;       — opt a type into world-gen near/spawn (takes effect next world load).
    /// </summary>
    internal static class DungeonGenerationCommands
    {
        private static bool _registered;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;

            new Terminal.ConsoleCommand("firesdungeon_types",
                "List the distinct dungeon-type names found in the shared section library.",
                (Terminal.ConsoleEventArgs args) => args.Context?.AddString("[FiresDungeonMaster] " + ListTypes()));

            new Terminal.ConsoleCommand("firesdungeon_sections",
                "List saved sections. Optional arg: dungeon-type to filter by.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    string type = (args.Args != null && args.Args.Length > 1) ? args.Args[1] : null;
                    args.Context?.AddString("[FiresDungeonMaster] " + ListSections(type));
                });

            new Terminal.ConsoleCommand("firesdungeon_generate",
                "Generate a dungeon from every section of a dungeon-type, ahead of you. Args: <dungeonType> [forwardMetres].",
                (Terminal.ConsoleEventArgs args) =>
                {
                    string type = (args.Args != null && args.Args.Length > 1) ? args.Args[1] : null;
                    float fwd = 8f;
                    if (args.Args != null && args.Args.Length > 2) float.TryParse(args.Args[2], out fwd);
                    args.Context?.AddString("[FiresDungeonMaster] " + GenerateForward(type, fwd));
                });

            new Terminal.ConsoleCommand("firesdungeon_worldgen",
                "Opt a dungeon-type into world generation (takes effect on next world load). Args: <dungeonType>.",
                (Terminal.ConsoleEventArgs args) =>
                {
                    string type = (args.Args != null && args.Args.Length > 1) ? args.Args[1] : null;
                    args.Context?.AddString("[FiresDungeonMaster] " + EnableWorldGen(type));
                });
        }

        // ── library helpers ──────────────────────────────────────────────────────

        private static SectionLibrary Library()
        {
            try { return FdmPaths.OpenLibrary(); }
            catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] section library unavailable: {ex.Message}"); return null; }
        }

        internal static List<string> DistinctTypes(SectionLibrary lib)
        {
            if (lib == null) return new List<string>();
            return lib.ListAllSections()
                .Select(s => s.DungeonType)
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string ListTypes()
        {
            var lib = Library();
            if (lib == null) return "section library unavailable (PlanBuild folder missing?).";
            var types = DistinctTypes(lib);
            return types.Count == 0 ? "no dungeon-types saved yet." : "dungeon-types: " + string.Join(", ", types);
        }

        private static string ListSections(string type)
        {
            var lib = Library();
            if (lib == null) return "section library unavailable.";
            var sections = string.IsNullOrEmpty(type) ? lib.ListAllSections() : lib.ListSectionsByDungeonType(type);
            if (sections.Count == 0) return string.IsNullOrEmpty(type) ? "no sections saved." : $"no sections for '{type}'.";
            return $"{sections.Count} section(s): " + string.Join(", ",
                sections.Select(s => $"{(string.IsNullOrEmpty(s.Name) ? s.SectionId : s.Name)}[{s.Role},{s.Footprint.x}x{s.Footprint.y}]"));
        }

        // ── generation ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Build+register the type's spec and spawn it. Host/server runs locally (persists the ZDOs); a remote client
        /// is refused (server-authoritative — run on the host). Returns a status string.
        /// </summary>
        internal static string GenerateForward(string dungeonType, float forward)
        {
            if (string.IsNullOrEmpty(dungeonType)) return "usage: firesdungeon_generate <dungeonType> [forwardMetres].";
            if (ZNet.instance == null) return "not in a world.";

            var lib = Library();
            if (lib == null) return "section library unavailable.";

            DungeonSpec spec = DungeonMasterSpecBuilder.BuildAndRegister(lib, dungeonType, out string reason);
            if (spec == null) return reason;

            // Dedicated-server console (no local player): spawn at world origin ground.
            var player = Player.m_localPlayer;
            if (player == null)
            {
                if (!ZNet.instance.IsServer()) return "no local player and not the server.";
                Vector3 origin = Vector3.zero;
                if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(origin, out float ogh)) origin.y = ogh;
                return FiresDungeonBridge.SpawnDungeonAt(spec, origin, Quaternion.identity);
            }

            if (!ZNet.instance.IsServer())
                return "you are not the server/host — run firesdungeon_generate on the host (server-authoritative).";

            Vector3 dir = player.transform.forward; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward; else dir.Normalize();
            Vector3 pos = player.transform.position + dir * Mathf.Max(2f, forward);
            Quaternion rot = Quaternion.LookRotation(-dir, Vector3.up);

            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float gh)) pos.y = gh;
            return FiresDungeonBridge.SpawnDungeonAt(spec, pos, rot);
        }

        private static string EnableWorldGen(string dungeonType)
        {
            if (string.IsNullOrEmpty(dungeonType)) return "usage: firesdungeon_worldgen <dungeonType>.";
            var lib = Library();
            if (lib == null) return "section library unavailable.";
            DungeonMasterSpecBuilder.EnableWorldGen(lib, dungeonType, null, out string reason);
            return reason;
        }
    }
}
