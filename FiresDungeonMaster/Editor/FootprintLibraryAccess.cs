using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FiresDungeonMaster.Sections;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// FootprintLibraryAccess — a thin, failure-tolerant helper that resolves the shared PlanBuild blueprint folder
    /// and exposes the DISTINCT dungeon-type names already authored in it. P1's SectionLibrary has NO
    /// distinct-type API and its ctor THROWS (DirectoryNotFoundException) if the PlanBuild folder doesn't exist,
    /// so this wrapper guards both: it derives distinct types client-side
    /// (ListAllSections().Select(DungeonType).Where(non-empty).Distinct()) and returns an empty list rather than
    /// throwing when the folder is missing.
    ///
    /// Used by SectionFootprintController.CycleDungeonType (the center-sphere Alt+Scroll dungeon-type picker).
    /// </summary>
    internal static class FootprintLibraryAccess
    {
        /// <summary>
        /// The shared interop folder: BepInEx\config\PlanBuild. Returns null if it does not exist (we never create
        /// it here — PlanBuild/IH own its lifecycle; this is a read-only convenience).
        /// </summary>
        internal static string ResolvePlanBuildFolder()
        {
            try
            {
                string root = Path.Combine(BepInEx.Paths.ConfigPath, "PlanBuild");
                return Directory.Exists(root) ? root : null;
            }
            catch { return null; }
        }

        /// <summary>Distinct, non-empty dungeon-type names across all saved sections (case-insensitive). Empty on any error.</summary>
        internal static List<string> DistinctDungeonTypes()
        {
            var folder = ResolvePlanBuildFolder();
            if (folder == null) return new List<string>();
            try
            {
                var lib = new SectionLibrary(folder);
                return lib.ListAllSections()
                    .Select(s => s.DungeonType)
                    .Where(t => !string.IsNullOrEmpty(t))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch { return new List<string>(); }
        }
    }
}
