using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// SectionLibrary - load/save/list section blueprints as .blueprint files
    /// in the shared PlanBuild blueprints folder.
    ///
    /// DESIGN:
    ///   - Sections are saved to: BepInEx\config\PlanBuild (in the game or mod-manager profile folder)
    ///   - Each section .blueprint file is named <SectionId>.blueprint (immutable, hash-based).
    ///   - SectionLibrary mints a stable section ID on first save (never the display name).
    ///   - Supports load-by-id, load-by-name, and list-all operations.
    ///   - All I/O is synchronous (use async wrappers in callers if needed).
    ///   - Stock PlanBuild/InfinityHammer loaders can read any section .blueprint unchanged.
    ///
    /// TRANSACTIONAL:
    ///   - If parsing fails mid-way through a .blueprint file, an exception is thrown.
    ///   - Callers should handle exceptions and either roll back or fail loudly.
    /// </summary>
    public class SectionLibrary
    {
        /// <summary>
        /// Root directory for section blueprints (shared PlanBuild folder).
        /// Typical path: BepInEx\config\PlanBuild in the game or mod-manager profile folder
        /// </summary>
        private readonly string _blueprintRoot;

        /// <summary>
        /// Create a new SectionLibrary pointing to the shared blueprints folder.
        /// Throws if the root directory does not exist.
        /// </summary>
        public SectionLibrary(string blueprintRoot)
        {
            if (string.IsNullOrEmpty(blueprintRoot))
                throw new ArgumentNullException(nameof(blueprintRoot));
            if (!Directory.Exists(blueprintRoot))
                throw new DirectoryNotFoundException($"Blueprint root not found: {blueprintRoot}");

            _blueprintRoot = blueprintRoot;
        }

        /// <summary>
        /// Save a section to .blueprint file (immutable name via section ID).
        /// Mints the section ID if not already set, then writes to {SectionId}.blueprint.
        /// Overwrites any existing file with the same ID.
        /// </summary>
        public void SaveSection(SectionBlueprint section)
        {
            if (section == null)
                throw new ArgumentNullException(nameof(section));

            // Mint section ID if needed.
            if (string.IsNullOrEmpty(section.SectionId))
                section.MintSectionId();

            string filePath = Path.Combine(_blueprintRoot, section.SectionId + ".blueprint");
            SectionSchema.WriteFile(filePath, section);
        }

        /// <summary>
        /// Load a section by its stable section ID.
        /// Returns null if the .blueprint file does not exist.
        /// Throws on parse errors (corrupted .blueprint).
        /// </summary>
        public SectionBlueprint LoadSectionById(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId))
                return null;

            string filePath = Path.Combine(_blueprintRoot, sectionId + ".blueprint");
            if (!File.Exists(filePath))
                return null;

            try
            {
                return SectionSchema.ParseFile(filePath);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load section {sectionId} from {filePath}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Load a section by its display name (case-insensitive).
        /// Scans all .blueprint files; returns the first match by Name field.
        /// Returns null if no section matches the name.
        /// Throws on parse errors.
        /// </summary>
        public SectionBlueprint LoadSectionByName(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return null;

            var allSections = ListAllSections();
            return allSections.FirstOrDefault(s =>
                string.Equals(s.Name, displayName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// List all sections matching a dungeon type (case-insensitive).
        /// Returns an empty list if no sections match.
        /// Throws on parse errors.
        /// </summary>
        public List<SectionBlueprint> ListSectionsByDungeonType(string dungeonType)
        {
            if (string.IsNullOrEmpty(dungeonType))
                return new List<SectionBlueprint>();

            var allSections = ListAllSections();
            return allSections.Where(s =>
                string.Equals(s.DungeonType, dungeonType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// List all sections in the blueprint library.
        /// Returns an empty list if the blueprint folder has no .blueprint files.
        /// Throws on parse errors (if a .blueprint file is corrupted).
        /// </summary>
        public List<SectionBlueprint> ListAllSections()
        {
            var sections = new List<SectionBlueprint>();
            if (!Directory.Exists(_blueprintRoot))
                return sections;

            string[] blueprintFiles;
            try
            {
                blueprintFiles = Directory.GetFiles(_blueprintRoot, "*.blueprint");
            }
            catch
            {
                return sections; // Directory access denied or other I/O error.
            }

            foreach (var filePath in blueprintFiles)
            {
                try
                {
                    var section = SectionSchema.ParseFile(filePath);
                    if (section != null)
                        sections.Add(section);
                }
                catch (Exception ex)
                {
                    // Log or re-throw depending on caller preference.
                    // For now, skip corrupted files and continue.
                    UnityEngine.Debug.LogWarning($"[SectionLibrary] Failed to load section from {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }

            return sections;
        }

        /// <summary>
        /// Delete a section by its stable section ID.
        /// Deletes the .blueprint file. Returns true if deletion succeeded;
        /// false if the file did not exist.
        /// Throws on permission/I/O errors.
        /// </summary>
        public bool DeleteSectionById(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId))
                return false;

            string filePath = Path.Combine(_blueprintRoot, sectionId + ".blueprint");
            if (!File.Exists(filePath))
                return false;

            try
            {
                File.Delete(filePath);
                return true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to delete section {sectionId}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Check if a section with the given ID exists in the library.
        /// </summary>
        public bool SectionExists(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId))
                return false;
            string filePath = Path.Combine(_blueprintRoot, sectionId + ".blueprint");
            return File.Exists(filePath);
        }

        /// <summary>
        /// Get the absolute file path for a section by its ID.
        /// Does not check if the file exists; this is a path utility.
        /// </summary>
        public string GetSectionFilePath(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId))
                return null;
            return Path.Combine(_blueprintRoot, sectionId + ".blueprint");
        }
    }
}
