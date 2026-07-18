using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using FiresDungeonMaster.Editor;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// SectionCapture (P4a) - INFINITY-HAMMER-STYLE ALL-OBJECT AREA CAPTURE. Gathers EVERY networked world object
    /// (any live ZNetView/ZDO) whose position lies inside a placed footprint box volume - build pieces AND
    /// furniture, containers WITH their stored contents, crafting stations, fireplaces, props, vegetation, item
    /// drops, signs, portals, wards, beehives - converts each to a Schema Piece with pos/rot/scale RELATIVE to
    /// the footprint transform, captures the object's FULL ZDO data blob (so a chest keeps its inventory, a
    /// smelter its ore, a sign its text), pulls the footprint metadata (footprint cells / stories / role /
    /// dungeon-type / edge tags) off the controller's ZDO, assembles a SectionBlueprint, and saves it to a
    /// .blueprint in the shared PlanBuild folder.
    ///
    /// EXCLUSIONS (what a static layout capture must skip): the footprint markers themselves, player characters,
    /// and monster/enemy AI (any Character carrying a MonsterAI). Everything else inside the box is captured
    /// generically by prefab name + relative transform + ZDO blob - exactly how Infinity Hammer /
    /// ServerDevcommands save objects (HammerSaveCommand.AddObject + DataEntry.GetBase64), except the area is a
    /// WORLD box (ZNetScene.m_instances scan) rather than a placement-ghost selection.
    ///
    /// Capture only READS world ZDOs + writes a file (no world mutation), so it runs wherever those objects are
    /// streamed in (host, or an admin client standing in the room). Dedi-safe: no renderer/mesh/UI.
    /// </summary>
    public static class SectionCapture
    {
        /// <summary>Result of a capture attempt - success flag, the saved blueprint (if any), and a message.</summary>
        public struct CaptureResult
        {
            public bool Success;
            public SectionBlueprint Blueprint;
            public string Message;
        }

        /// <summary>
        /// Capture every object inside the footprint, build a SectionBlueprint, and save it.
        /// <paramref name="saveName"/> (optional) disambiguates same-shape sections so they never clobber each
        /// other on disk (folded into the unique id); null/empty falls back to the derived display name.
        /// </summary>
        public static CaptureResult CaptureAndSave(SectionFootprintController footprint, string name = null, string notes = null)
        {
            if (footprint == null)
                return Fail("no footprint (aim at a placed Dungeon Section Footprint).");

            try
            {
                // A scaled NxM box dices into one blueprint PER CELL (all cells saved, empty or not) sharing a set id;
                // a 1x1 box returns a single standalone section. RealObjects excludes the auto centerpoint marker.
                var set = BuildBlueprintSet(footprint, name, notes);
                int totalReal = set.Sum(RealObjects);
                if (totalReal == 0)
                    return Fail("footprint is empty - build/place something inside it first.");

                SectionLibrary lib = FdmPaths.OpenLibrary();
                foreach (var bp in set) lib.SaveSection(bp);

                bool isSet = set.Count > 1;
                string msg = isSet
                    ? $"saved {set.Count}-room set ({footprint.FootprintCellsX}x{footprint.FootprintCellsZ} cells, {totalReal} objects) — set '{set[0].SetId}'."
                    : $"saved '{set[0].Name}' ({RealObjects(set[0])} objects) as section {set[0].SectionId}.";
                FiresLogger.LogInfo($"{msg}");

                return new CaptureResult { Success = true, Blueprint = set[0], Message = msg };
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"capture/save failed: {ex}");
                return Fail($"capture failed: {ex.Message}");
            }
        }

        private static int RealObjects(SectionBlueprint bp) => bp.Objects.Count(p => !SectionCenterpoint.Is(p));

        /// <summary>
        /// Build a SectionBlueprint from the footprint + every captured object inside it (no file I/O).
        /// Public so the placement-preview / P6 paths can reuse it without forcing a save.
        /// </summary>
        public static SectionBlueprint BuildBlueprint(SectionFootprintController footprint, string name = null, string notes = null)
        {
            if (footprint == null) throw new ArgumentNullException(nameof(footprint));

            Transform ft = footprint.transform;
            Vector3 center = ft.position;
            Quaternion footprintRot = ft.rotation;
            Quaternion invRot = Quaternion.Inverse(footprintRot);

            var bp = new SectionBlueprint
            {
                Name = string.IsNullOrWhiteSpace(name) ? MakeName(footprint) : name.Trim(),
                Description = notes ?? "",
                Category = "FiresDungeonMaster",
                Footprint = new Vector2Int(Mathf.Max(1, footprint.FootprintCellsX), Mathf.Max(1, footprint.FootprintCellsZ)),
                Stories = Mathf.Max(1, footprint.Stories),
                Role = ClampRole(footprint.Role),
                DungeonType = footprint.DungeonType ?? "",
                EdgeTags = footprint.BuildEdgeTagsForBlueprint(),
                EntranceFace = footprint.EntranceFace,
            };

            ZNetScene scene = ZNetScene.instance;
            if (scene == null || scene.m_instances == null)
                return bp; // nothing streamed in - empty section (caller reports "footprint is empty")

            // Snapshot the live-instance ZNetViews first: we only read each object's ZDO (no spawns/destroys
            // happen during capture), so a copy is enough to iterate without mutation hazards.
            var views = new List<ZNetView>(scene.m_instances.Count);
            foreach (var kv in scene.m_instances)
            {
                if (kv.Value != null) views.Add(kv.Value);
            }

            foreach (ZNetView nv in views)
            {
                if (nv == null) continue;
                GameObject go = nv.gameObject;
                if (go == null) continue;

                ZDO zdo = nv.GetZDO();
                if (zdo == null || !nv.IsValid()) continue;

                if (!ShouldCapture(go, footprint)) continue;

                Vector3 worldPos = go.transform.position;
                if (!footprint.ContainsPoint(worldPos)) continue;

                string prefabName = Utils.GetPrefabName(go);
                if (string.IsNullOrEmpty(prefabName)) continue;

                // Re-origin: position/rotation relative to the footprint transform (worldPos -> local).
                Vector3 localPos = invRot * (worldPos - center);
                Quaternion localRot = invRot * go.transform.rotation;

                bp.Objects.Add(new Piece
                {
                    Prefab = prefabName,
                    Pos = localPos,
                    Rot = localRot,
                    Scale = go.transform.localScale,
                    ExtraInfo = "",                       // unused - the full ZDO blob below carries sign text / tags / etc.
                    Data = SectionZdoData.Capture(zdo),   // FULL lossless ZDO blob (container contents, fuel, text, ...)
                    Chance = 1f,
                });
            }

            // CENTERPOINT FALLBACK: drop a real vanilla prop at local origin (the box centerpoint) + declare it via the
            // standard #center: header, so a mod-less Expand World / Infinity Hammer / PlanBuild load has a valid object
            // to build the section around. The mod itself skips this marker as content everywhere it spawns pieces
            // (SectionPlacer / DungeonMasterInteriorPatch / edit-in-box) — see SectionCenterpoint.
            string fallback = DungeonMasterConfig.FallbackPrefab;
            bp.CenterPiece = fallback;
            bp.Objects.Insert(0, SectionCenterpoint.Make(fallback));

            // Stable, COLLISION-RESISTANT id. MintSectionId hashes only footprint+stories+role+dungeontype, so two
            // distinct authored sections with the same shape clobber each other's file. Fold the display Name AND
            // an explicit save-name (when supplied) into the key so each authored section gets its own .blueprint
            // (required for P6 multiple-sections-per-type).
            bp.SectionId = MintUniqueId(bp, name);

            return bp;
        }

        /// <summary>
        /// Build ONE blueprint per footprint cell for a scaled (NxM) box — the "dice on save" path. Each 8x8 cell's
        /// contents are captured RELATIVE to that cell's own floor centre (so every cell is an independent 1x1
        /// section), its perimeter faces inherit the box's outer tags while internal seams are Open, and all cells
        /// share a SetId + carry their (i,j) grid position + set size so they always place together. A 1x1 box just
        /// returns the single standalone <see cref="BuildBlueprint"/> result (no set metadata).
        /// </summary>
        public static List<SectionBlueprint> BuildBlueprintSet(SectionFootprintController footprint, string name = null, string notes = null)
        {
            if (footprint == null) throw new ArgumentNullException(nameof(footprint));

            int X = Mathf.Max(1, footprint.FootprintCellsX), Z = Mathf.Max(1, footprint.FootprintCellsZ);
            if (X == 1 && Z == 1)
                return new List<SectionBlueprint> { BuildBlueprint(footprint, name, notes) };

            Transform ft = footprint.transform;
            Vector3 boxCenter = ft.position;
            Quaternion invRot = Quaternion.Inverse(ft.rotation);
            Vector3 boxSize = footprint.BoxSize;
            float cell = SectionFootprintController.CellMeters;

            string setId = MintSetId(footprint, name);
            string baseName = string.IsNullOrWhiteSpace(name) ? MakeName(footprint) : name.Trim();

            var cells = new SectionBlueprint[X, Z];
            for (int i = 0; i < X; i++)
                for (int j = 0; j < Z; j++)
                {
                    cells[i, j] = new SectionBlueprint
                    {
                        Name = $"{baseName}_r{j}c{i}",
                        Description = notes ?? "",
                        Category = "FiresDungeonMaster",
                        Footprint = new Vector2Int(1, 1),
                        Stories = Mathf.Max(1, footprint.Stories),
                        Role = ClampRole(footprint.Role),
                        DungeonType = footprint.DungeonType ?? "",
                        EdgeTags = footprint.BuildEdgeTagsForCell(i, j),
                        SetId = setId,
                        SetPos = new Vector2Int(i, j),
                        SetSize = new Vector2Int(X, Z),
                        SectionId = $"{setId}_{i}_{j}",
                    };
                }

            // The box's entrance face maps onto the MIDDLE cell of that outer edge — only that cell carries EntranceFace.
            if (footprint.EntranceFace >= 0)
            {
                int ef = footprint.EntranceFace, ei, ej;
                switch (ef)
                {
                    case 0: ei = X / 2; ej = Z - 1; break;   // north +Z (top row)
                    case 1: ei = X - 1; ej = Z / 2; break;   // east  +X (right col)
                    case 2: ei = X / 2; ej = 0;     break;   // south -Z (bottom row)
                    default: ei = 0;    ej = Z / 2; break;   // west  -X (left col)
                }
                cells[ei, ej].EntranceFace = ef;
            }

            // Bucket every captured object into the cell that holds its origin, re-origined to that cell's floor centre.
            ZNetScene scene = ZNetScene.instance;
            if (scene != null && scene.m_instances != null)
            {
                var views = new List<ZNetView>(scene.m_instances.Values);
                foreach (ZNetView nv in views)
                {
                    if (nv == null) continue;
                    GameObject go = nv.gameObject; if (go == null) continue;
                    ZDO zdo = nv.GetZDO(); if (zdo == null || !nv.IsValid()) continue;
                    if (!ShouldCapture(go, footprint)) continue;

                    Vector3 worldPos = go.transform.position;
                    if (!footprint.ContainsPoint(worldPos)) continue;
                    string prefabName = Utils.GetPrefabName(go);
                    if (string.IsNullOrEmpty(prefabName)) continue;

                    Vector3 boxLocal = invRot * (worldPos - boxCenter);
                    int i = Mathf.Clamp(Mathf.FloorToInt((boxLocal.x + boxSize.x * 0.5f) / cell), 0, X - 1);
                    int j = Mathf.Clamp(Mathf.FloorToInt((boxLocal.z + boxSize.z * 0.5f) / cell), 0, Z - 1);

                    cells[i, j].Objects.Add(new Piece
                    {
                        Prefab = prefabName,
                        Pos = boxLocal - footprint.CellCenterLocal(i, j),
                        Rot = invRot * go.transform.rotation,
                        Scale = go.transform.localScale,
                        ExtraInfo = "",
                        Data = SectionZdoData.Capture(zdo),
                        Chance = 1f,
                    });
                }
            }

            // Centerpoint fallback per cell + flatten (row-major: j outer so the on-disk order reads bottom-to-top).
            string fallback = DungeonMasterConfig.FallbackPrefab;
            var list = new List<SectionBlueprint>(X * Z);
            for (int j = 0; j < Z; j++)
                for (int i = 0; i < X; i++)
                {
                    var bp = cells[i, j];
                    bp.CenterPiece = fallback;
                    bp.Objects.Insert(0, SectionCenterpoint.Make(fallback));
                    list.Add(bp);
                }
            return list;
        }

        // Stable id shared by every cell of one saved set (keyed on the whole box, NOT per cell).
        private static string MintSetId(SectionFootprintController fp, string saveName)
        {
            string nameKey = string.IsNullOrEmpty(saveName) ? MakeName(fp) : $"{MakeName(fp)}_{saveName}";
            string key = $"set_{fp.FootprintCellsX}x{fp.FootprintCellsZ}_{fp.Stories}_{fp.DungeonType}_{nameKey}";
            unchecked
            {
                uint hash = 5381;
                foreach (char c in key) hash = ((hash << 5) + hash) ^ (uint)c;
                return "set" + hash.ToString("x8");
            }
        }

        // -- capture filter -------------------------------------------------------------------------------------

        /// <summary>
        /// All-object inclusion predicate. Capture EVERYTHING networked inside the box EXCEPT: the footprint
        /// platform being captured (its controller GameObject AND every gizmo child — the FootprintTagMarkers
        /// holder, the WireBox wireframe, the edge/center marker spheres, the snap-point children), ANY other
        /// placed footprint platform, player characters, and monster/enemy AI (a Character that also carries a
        /// MonsterAI). Pure structural / furniture / prop / vegetation / item-drop objects are captured.
        ///
        /// EXCLUDE-SELF mirrors kg_Blueprint.BlueprintPiece.GetObjectedInside, which passes the blueprint piece's
        /// own GameObject as the GetObjectsInside exclude[] so the tool box never writes itself into the saved
        /// blueprint. Here the footprint's wireframe + marker spheres carry no ZNetView so they are already skipped
        /// by the ZDO guard upstream, but excluding the whole footprint hierarchy makes the contract explicit and
        /// future-proof against any gizmo gaining a networked component.
        /// </summary>
        private static bool ShouldCapture(GameObject go, SectionFootprintController self)
        {
            // Never capture the footprint platform being captured, nor any of its gizmo children (the
            // GetComponentInParent walk catches WireBox / EdgeMarker_* / CenterMarker / SnapEdge_* under it).
            if (self != null)
            {
                Transform t = go.transform;
                if (t == self.transform || t.IsChildOf(self.transform)) return false;
            }

            // Never capture ANY footprint platform (a different placed section's box is the tool, not content).
            if (go.GetComponentInParent<SectionFootprintController>() != null) return false;

            // Never capture players.
            if (go.GetComponent<Player>() != null) return false;

            // Exclude monster/enemy AI: a Character driven by a MonsterAI. A section is a static layout - live
            // creatures are not part of it (the prompt's exclusion is explicitly "Character with MonsterAI").
            Character ch = go.GetComponent<Character>();
            if (ch != null && go.GetComponent<MonsterAI>() != null) return false;

            return true;
        }

        // -- helpers ----------------------------------------------------------------------------------------

        // MintSectionId is non-unique by design (excludes Name). Reproduce its DJB2 hash over a key that ALSO
        // includes the Name and an optional author-supplied save-name, so authored sections never silently
        // overwrite one another even when footprint+stories+role+dungeontype match.
        private static string MintUniqueId(SectionBlueprint bp, string saveName)
        {
            string nameKey = string.IsNullOrEmpty(saveName) ? bp.Name : $"{bp.Name}_{saveName}";
            string key = $"{bp.Footprint.x}x{bp.Footprint.y}_{bp.Stories}_{bp.Role}_{bp.DungeonType}_{nameKey}";
            unchecked
            {
                uint hash = 5381;
                foreach (char c in key)
                    hash = ((hash << 5) + hash) ^ (uint)c;
                return hash.ToString("x8");
            }
        }

        private static SectionRole ClampRole(int role)
        {
            if (Enum.IsDefined(typeof(SectionRole), role)) return (SectionRole)role;
            return SectionRole.Center;
        }

        private static string MakeName(SectionFootprintController footprint)
        {
            string type = string.IsNullOrEmpty(footprint.DungeonType) ? "section" : footprint.DungeonType;
            SectionRole role = ClampRole(footprint.Role);
            // e.g. "crypt_Entry_3x2" - readable, but the on-disk file is named by the (unique) SectionId.
            return $"{type}_{role}_{Mathf.Max(1, footprint.FootprintCellsX)}x{Mathf.Max(1, footprint.FootprintCellsZ)}";
        }

        private static CaptureResult Fail(string message)
            => new CaptureResult { Success = false, Blueprint = null, Message = message };
    }
}
