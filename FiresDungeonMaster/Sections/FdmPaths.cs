using System.IO;
using BepInEx;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// FdmPaths - resolves the shared PlanBuild blueprint folder that SectionLibrary writes/reads.
    /// Sections live in BepInEx\config\PlanBuild so they cross-populate with Infinity Hammer / PlanBuild
    /// (the .blueprint format is identical; FiresDungeonMaster only adds skipped #fdm_* headers). The
    /// SectionLibrary ctor THROWS DirectoryNotFoundException if the folder does not already exist (it does
    /// NOT auto-create), so every caller must construct the library through OpenLibrary, which ensures the
    /// directory first. Resolution is BepInEx.Paths.ConfigPath + "PlanBuild".
    /// </summary>
    public static class FdmPaths
    {
        public const string PlanBuildFolderName = "PlanBuild";

        /// <summary>Absolute path to the shared PlanBuild blueprint folder (not guaranteed to exist).</summary>
        public static string PlanBuildFolder => Path.Combine(Paths.ConfigPath, PlanBuildFolderName);

        /// <summary>
        /// Ensure the shared PlanBuild folder exists, then construct a SectionLibrary rooted there.
        /// This is the ONLY supported way to obtain a SectionLibrary in FiresDungeonMaster - the bare ctor
        /// throws if the directory is missing.
        /// </summary>
        public static SectionLibrary OpenLibrary()
        {
            string folder = PlanBuildFolder;
            Directory.CreateDirectory(folder); // no-op if present; satisfies the ctor's existence requirement
            return new SectionLibrary(folder);
        }
    }
}
