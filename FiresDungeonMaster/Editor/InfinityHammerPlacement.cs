using System;
using System.IO;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// Hands a PlanBuild/.blueprint (or .vbuild) file to Infinity Hammer's OWN <c>hammer_blueprint</c> command, so
    /// placement uses the real IH ghost — the blueprint name is announced, and you get IH's full placement UX
    /// (rotation, nudging, scale, snapping, precision) exactly as if you'd typed the command yourself.
    ///
    /// IH only searches its configured blueprint folders (Configuration.BlueprintLocalFolder / BlueprintGlobalFolder),
    /// so a file living in PlanBuild's folder is invisible to it — that's why the old direct command "did nothing".
    /// We copy the selected file into IH's local folder (idempotent; skipped if it already lives under an IH folder),
    /// then run <c>hammer_blueprint &lt;name&gt;</c>. Returns false when IH isn't loaded (or on any failure) so the
    /// caller falls back to our own translucent grab. Soft dependency — reached entirely by reflection.
    /// </summary>
    internal static class InfinityHammerPlacement
    {
        private const string IHGuid = "infinity_hammer";
        private static Type _configType;
        private static bool _probed;

        public static bool IsAvailable =>
            Chainloader.PluginInfos != null && Chainloader.PluginInfos.ContainsKey(IHGuid) && Console.instance != null;

        /// <summary>Select <paramref name="bp"/> onto the hammer via IH. True = IH took it; false = caller should fall back.</summary>
        public static bool TryPlace(FiresDungeonMaster.Sections.SectionBlueprint bp, out string message)
        {
            message = "";
            if (bp == null || !IsAvailable) return false;

            string src = bp.SourcePath;
            if (string.IsNullOrEmpty(src) || !File.Exists(src)) return false;   // no backing file → fall back to our grab

            BlueprintPlaceDiag.EnsurePatched();   // log where IH actually lands each piece (positions + at-origin/far flags)
            try
            {
                if (!AlreadyInIHFolder(src))
                {
                    string localFolder = GetFolder("BlueprintLocalFolder");
                    if (string.IsNullOrEmpty(localFolder)) return false;
                    Directory.CreateDirectory(localFolder);
                    string dst = Path.Combine(localFolder, Path.GetFileName(src));
                    if (Path.GetFullPath(dst) != Path.GetFullPath(src)) File.Copy(src, dst, overwrite: true);
                }

                // IH matches by file-name-without-extension with spaces → underscores.
                string selectName = Path.GetFileNameWithoutExtension(src).Replace(" ", "_");
                Console.instance.TryRunCommand("hammer_blueprint " + selectName, true, true);
                message = $"Selected '{selectName}' — place it with Infinity Hammer (rotate, nudge, scale as usual).";
                return true;
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"IH placement failed: {ex.Message}");
                return false;
            }
        }

        private static bool AlreadyInIHFolder(string src)
        {
            try
            {
                string sdir = Path.GetFullPath(Path.GetDirectoryName(src));
                foreach (var key in new[] { "BlueprintLocalFolder", "BlueprintGlobalFolder" })
                {
                    string f = GetFolder(key);
                    if (!string.IsNullOrEmpty(f) && sdir.StartsWith(Path.GetFullPath(f), StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { }
            return false;
        }

        // Reflect InfinityHammer.Configuration.<propertyName> (a public static string), null if IH not present.
        private static string GetFolder(string propertyName)
        {
            if (!_probed)
            {
                _probed = true;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _configType = asm.GetType("InfinityHammer.Configuration", false); }
                    catch { _configType = null; }
                    if (_configType != null) break;
                }
            }
            if (_configType == null) return null;
            var prop = _configType.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Static);
            return prop?.GetValue(null) as string;
        }
    }
}
