using System;
using System.Collections.Generic;
using System.Linq;
using FiresCore.Logging;
using FiresDungeonMaster.Editor;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    using EdgeTag = SectionFootprintController.EdgeTag;

    /// <summary>
    /// SectionEditSpawner — the blueprint-bench EDIT flow. Given a saved section, spawn a fresh footprint box in
    /// front of the player, configure that box from the section (size / stories / edge tags / role / dungeon-type),
    /// and re-instantiate the section's objects INSIDE the box (via SectionPlacer) — so an admin can move pieces,
    /// retag faces, and re-save with the footprint's own "Save Blueprint". This is the "spawn the bp near us within
    /// our layout box for us to edit and re-save" path.
    ///
    /// ADMIN-gated, NOT host-only: on a dedicated-server CLIENT IsServer() is false but the admin has
    /// ConfigSync.lockExempt, and a client-side Instantiate creates a client-owned ZDO that replicates (exactly how
    /// vanilla building works). So an admin editing from a connected client works — unlike the final "place" path,
    /// which stays host-gated on purpose.
    /// </summary>
    public static class SectionEditSpawner
    {
        private const float CellMetres = 8f;         // one footprint grid cell = 8m (matches the bake DefaultBox 8x_x8)
        private const float SpawnForwardDistance = 6f;

        /// <summary>Load a saved section by id (then name) and spawn it as an editable box in front of the player.</summary>
        public static string SpawnForEditById(string sectionId)
        {
            if (string.IsNullOrEmpty(sectionId)) return "no section id.";
            SectionBlueprint bp;
            try
            {
                SectionLibrary lib = FdmPaths.OpenLibrary();
                bp = lib.LoadSectionById(sectionId) ?? lib.LoadSectionByName(sectionId);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"edit-load failed ({sectionId}): {ex}");
                return $"load failed: {ex.Message}";
            }
            if (bp == null) return $"section '{sectionId}' not found.";
            return SpawnForEdit(bp);
        }

        /// <summary>Spawn an editable footprint box for a section and fill it with the section's objects.</summary>
        public static string SpawnForEdit(SectionBlueprint bp)
        {
            if (bp == null) return "no section.";
            if (ZNet.instance == null || ZNetScene.instance == null) return "not in a world.";
            if (!IsAdmin()) return "admin only (this world's admin list).";

            var player = Player.m_localPlayer;
            if (player == null) return "no local player.";

            GameObject prefab = ZNetScene.instance.GetPrefab(FootprintRegistration.FootprintPrefabName);
            if (prefab == null) return "footprint prefab not registered.";

            // In front of the player, ground-snapped — the box floor (Y=0) sits on the ground.
            Vector3 dir = player.transform.forward; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward; else dir.Normalize();
            Vector3 pos = player.transform.position + dir * SpawnForwardDistance;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float gh)) pos.y = gh;

            GameObject box;
            try { box = UnityEngine.Object.Instantiate(prefab, pos, rot); }
            catch (Exception ex) { FiresLogger.LogError($"edit box spawn failed: {ex}"); return $"box spawn failed: {ex.Message}"; }

            var fp = box.GetComponent<SectionFootprintController>();
            if (fp != null)
            {
                try { ConfigureFromSection(fp, bp); }
                catch (Exception ex) { FiresLogger.LogWarning($"edit box configure failed: {ex.Message}"); }
            }

            // Objects were captured relative to the section origin (the box floor), so placing at the box position
            // reconstructs them inside the box.
            SectionPlacer.PlaceResult r = SectionPlacer.Place(bp, pos, rot);

            string name = string.IsNullOrEmpty(bp.Name) ? bp.SectionId : bp.Name;
            FiresLogger.LogInfo($"edit spawn '{name}': box@{pos} {r.Spawned} objs, {r.Missing} missing.");
            return $"editing '{name}': box + {r.Spawned} objects spawned" + (r.Missing > 0 ? $" ({r.Missing} missing)" : "") +
                   ". Move/retag, then Save Blueprint on the box to re-save.";
        }

        /// <summary>
        /// Edit a whole SET: respawn ONE footprint box sized to the set (NxM cells), configured with the set's OUTER
        /// perimeter tags, then re-instantiate every cell's contents at its grid offset inside the box — so the admin
        /// sees the full assembled room and can retag/move and Save (which re-dices into the per-cell blueprints).
        /// </summary>
        public static string SpawnSetForEdit(List<SectionBlueprint> cells)
        {
            if (cells == null || cells.Count == 0) return "empty set.";
            if (ZNet.instance == null || ZNetScene.instance == null) return "not in a world.";
            if (!IsAdmin()) return "admin only (this world's admin list).";
            var player = Player.m_localPlayer;
            if (player == null) return "no local player.";
            GameObject prefab = ZNetScene.instance.GetPrefab(FootprintRegistration.FootprintPrefabName);
            if (prefab == null) return "footprint prefab not registered.";

            var rep = cells[0];
            int X = Mathf.Max(1, rep.SetSize.x), Z = Mathf.Max(1, rep.SetSize.y);

            Vector3 dir = player.transform.forward; dir.y = 0f;
            if (dir.sqrMagnitude < 0.01f) dir = Vector3.forward; else dir.Normalize();
            Vector3 pos = player.transform.position + dir * SpawnForwardDistance;
            Quaternion rot = Quaternion.LookRotation(dir, Vector3.up);
            if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(pos, out float gh)) pos.y = gh;

            GameObject box;
            try { box = UnityEngine.Object.Instantiate(prefab, pos, rot); }
            catch (Exception ex) { FiresLogger.LogError($"edit-set box spawn failed: {ex}"); return $"box spawn failed: {ex.Message}"; }

            var fp = box.GetComponent<SectionFootprintController>();
            if (fp != null)
            {
                try { ConfigureSetBox(fp, cells, X, Z); }
                catch (Exception ex) { FiresLogger.LogWarning($"edit-set configure failed: {ex.Message}"); }
            }

            int spawned = 0, missing = 0;
            foreach (var cell in cells)
            {
                if (cell == null) continue;
                Vector3 offset = SectionPlacer.SetCellOffset(cell.SetPos.x, cell.SetPos.y, X, Z);
                var r = SectionPlacer.Place(cell, pos + rot * offset, rot);
                spawned += r.Spawned; missing += r.Missing;
            }

            FiresLogger.LogInfo($"edit-set {X}x{Z} @{pos}: {spawned} objs, {missing} missing.");
            return $"editing {X}x{Z} set: box + {spawned} objects" + (missing > 0 ? $" ({missing} missing)" : "") +
                   ". Move/retag, then Save Blueprint to re-dice + re-save the set.";
        }

        // Configure a spawned box to match a set: NxM cells, outer perimeter tags (a perimeter cell's outer face
        // carries the box's original tag), role/type from the representative cell.
        private static void ConfigureSetBox(SectionFootprintController fp, List<SectionBlueprint> cells, int X, int Z)
        {
            int stories = Mathf.Max(1, cells[0].Stories);
            fp.FootprintCellsX = X; fp.FootprintCellsZ = Z; fp.Stories = stories;
            fp.BoxSize = new Vector3(X * CellMetres, SectionFootprintController.BoxHeightForStories(stories), Z * CellMetres);
            fp.Role = (int)cells[0].Role;
            fp.DungeonType = cells[0].DungeonType ?? "";
            fp.EdgeTagN = OuterTag(cells, c => c.SetPos.y == Z - 1, "north");
            fp.EdgeTagE = OuterTag(cells, c => c.SetPos.x == X - 1, "east");
            fp.EdgeTagS = OuterTag(cells, c => c.SetPos.y == 0, "south");
            fp.EdgeTagW = OuterTag(cells, c => c.SetPos.x == 0, "west");
            fp.EntranceFace = -1;
            foreach (var c in cells) if (c.EntranceFace >= 0) { fp.EntranceFace = c.EntranceFace; break; }
            fp.ApplyConfiguredState();
        }

        // First perimeter cell on the given edge reports the box's outer tag for that face (Wall if none found).
        private static EdgeTag OuterTag(List<SectionBlueprint> cells, Func<SectionBlueprint, bool> onEdge, string face)
        {
            foreach (var c in cells) if (onEdge(c)) return TagFor(c.EdgeTags, face);
            return EdgeTag.Wall;
        }

        // ── Edit-onto-hammer: arm a pending edit, applied to the NEXT footprint the admin hammer-places ───────────
        private static SectionBlueprint _pendingEdit;
        private static List<SectionBlueprint> _pendingSet;
        private static SectionBlueprint _pendingRaw;   // raw blueprint to DROP directly at the aim (no editable box)

        /// <summary>True while an Edit-in-Box OR a raw Place is armed (the grab preview should show).</summary>
        public static bool HasPending => _pendingEdit != null || _pendingSet != null || _pendingRaw != null;

        /// <summary>The raw blueprint armed for direct placement (null unless Place armed it).</summary>
        public static SectionBlueprint RawPending => _pendingRaw;

        /// <summary>Box size (metres, bottom-anchored) of the armed edit/place, for the grab preview. Default 1x1 if none.</summary>
        public static Vector3 PendingBoxSize()
        {
            if (_pendingSet != null)
            {
                var r = _pendingSet[0];
                return new Vector3(Mathf.Max(1, r.SetSize.x) * CellMetres, SectionFootprintController.BoxHeightForStories(Mathf.Max(1, r.Stories)), Mathf.Max(1, r.SetSize.y) * CellMetres);
            }
            var b = _pendingEdit ?? _pendingRaw;
            if (b != null)
                return new Vector3(Mathf.Max(1, b.Footprint.x) * CellMetres, SectionFootprintController.BoxHeightForStories(Mathf.Max(1, b.Stories)), Mathf.Max(1, b.Footprint.y) * CellMetres);
            return new Vector3(CellMetres, SectionFootprintController.BoxHeightForStories(1), CellMetres);
        }

        /// <summary>Disarm any pending edit/place (grab cancelled).</summary>
        public static void CancelPending() { _pendingEdit = null; _pendingSet = null; _pendingRaw = null; }

        /// <summary>
        /// Arm a raw PlanBuild blueprint to drop DIRECTLY at the aim (no editable box) — the grab preview rides the
        /// hammer, aim + left-click places the blueprint there. Wear-n-tear is suppressed so it can't collapse.
        /// </summary>
        public static string ArmForRawPlace(SectionBlueprint bp)
        {
            if (!ArmGate(out string why)) return why;
            if (bp == null || bp.Objects == null || bp.Objects.Count == 0) return "blueprint is empty.";
            SizeAndCentre(bp);
            _pendingRaw = bp; _pendingEdit = null; _pendingSet = null;
            FiresDungeonMaster.Editor.SectionPlaceGrabController.Begin();
            return $"place armed — aim + left-click to drop '{(string.IsNullOrEmpty(bp.Name) ? bp.SectionId : bp.Name)}'.";
        }

        /// <summary>Drop the raw-place blueprint at pos/rot (called by the grab on left-click). One-shot. Registers the
        /// spawned objects with the shared World Edit / Infinity Hammer undo stack so the placement can be reverted.</summary>
        public static bool PlaceRawAt(Vector3 pos, Quaternion rot)
        {
            var bp = _pendingRaw; _pendingRaw = null;
            if (bp == null) return false;

            var spawned = new List<ZDO>();
            FiresDungeonMaster.Editor.FiresUndoBridge.Begin();
            SectionPlacer.CollectSpawned = spawned;
            SectionPlacer.SuppressWearNTear = true;
            try { SectionPlacer.Place(bp, pos, rot); }
            finally { SectionPlacer.SuppressWearNTear = false; SectionPlacer.CollectSpawned = null; }
            foreach (var z in spawned) FiresDungeonMaster.Editor.FiresUndoBridge.AddSpawn(z);
            FiresDungeonMaster.Editor.FiresUndoBridge.End();
            return true;
        }

        // Centre a blueprint on its own bounds (XZ + floor-to-Y-min) and size a footprint to it (+1 cell, cap 40) — the
        // shared prep for both Edit-in-Box and raw Place so the preview box + placement line up on the aim.
        private static void SizeAndCentre(SectionBlueprint bp)
        {
            Vector3 min = new Vector3(1e9f, 1e9f, 1e9f), max = new Vector3(-1e9f, -1e9f, -1e9f);
            foreach (var p in bp.Objects) { if (p == null) continue; min = Vector3.Min(min, p.Pos); max = Vector3.Max(max, p.Pos); }
            Vector3 size = max - min;
            Vector3 centre = new Vector3((min.x + max.x) * 0.5f, min.y, (min.z + max.z) * 0.5f);
            foreach (var p in bp.Objects) { if (p != null) p.Pos -= centre; }
            bp.Footprint = new Vector2Int(Mathf.Clamp(Mathf.CeilToInt(size.x / CellMetres) + 1, 1, 40), Mathf.Clamp(Mathf.CeilToInt(size.z / CellMetres) + 1, 1, 40));
            bp.Stories = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(1f, size.y) / SectionFootprintController.StoryHeight), 1, 20);
        }

        /// <summary>
        /// Arm a saved section so the NEXT footprint the admin hammer-places becomes that section's editable box
        /// (configured + filled). The bench Edit button uses this so the box goes ONTO the hammer for the player to
        /// place where they want, instead of auto-dropping in front of them. Returns a message to show.
        /// </summary>
        public static string ArmForPlacementById(string sectionId)
        {
            if (!ArmGate(out string why)) return why;
            SectionBlueprint bp;
            try { var lib = FdmPaths.OpenLibrary(); bp = lib.LoadSectionById(sectionId) ?? lib.LoadSectionByName(sectionId); }
            catch (Exception ex) { return $"load failed: {ex.Message}"; }
            if (bp == null) return $"section '{sectionId}' not found.";
            _pendingEdit = bp; _pendingSet = null;
            FiresDungeonMaster.Editor.SectionPlaceGrabController.Begin();
            return $"edit armed — place the footprint (on your hammer) to drop '{(string.IsNullOrEmpty(bp.Name) ? bp.SectionId : bp.Name)}'.";
        }

        /// <summary>
        /// Arm a PLAIN PlanBuild blueprint (not a DM section) for edit-in-box: size a footprint to the blueprint's own
        /// object bounds (+1 cell of breathing room) and drop it on the hammer, so the next footprint the admin places
        /// fills with the blueprint's objects (TryApplyPendingEdit → SectionPlacer.Place). Lets the bench "Edit" a raw
        /// Infinity-Hammer/PlanBuild blueprint inside a fitted box. Returns a message to show.
        /// </summary>
        public static string ArmBlueprintForPlacement(SectionBlueprint bp)
        {
            if (!ArmGate(out string why)) return why;
            if (bp == null || bp.Objects == null || bp.Objects.Count == 0) return "blueprint is empty.";

            SizeAndCentre(bp);   // centre on bounds + size the footprint (shared with raw Place)

            _pendingEdit = bp; _pendingSet = null; _pendingRaw = null;
            FiresDungeonMaster.Editor.SectionPlaceGrabController.Begin();
            return $"edit armed — place the footprint to drop '{(string.IsNullOrEmpty(bp.Name) ? bp.SectionId : bp.Name)}' in a {bp.Footprint.x}x{bp.Footprint.y} box.";
        }

        /// <summary>Arm a whole diced set (its cells) to drop as one box on the next footprint placement.</summary>
        public static string ArmSetForPlacement(List<SectionBlueprint> cells)
        {
            if (cells == null || cells.Count == 0) return "empty set.";
            if (!ArmGate(out string why)) return why;
            _pendingSet = cells; _pendingEdit = null;
            FiresDungeonMaster.Editor.SectionPlaceGrabController.Begin();
            var rep = cells[0];
            return $"edit armed — place the footprint (on your hammer) to drop the {rep.SetSize.x}x{rep.SetSize.y} set.";
        }

        private static bool ArmGate(out string why)
        {
            why = null;
            if (ZNet.instance == null || ZNetScene.instance == null) { why = "not in a world."; return false; }
            if (!IsAdmin()) { why = "admin only (this world's admin list)."; return false; }
            if (Player.m_localPlayer == null) { why = "no local player."; return false; }
            return true;
        }

        /// <summary>
        /// Called from a freshly-placed (non-ghost) footprint's Awake. If an edit was armed, configure THIS box from
        /// the armed section + fill it with the section's objects, then disarm. One-shot — no effect otherwise.
        /// </summary>
        public static bool TryApplyPendingEdit(SectionFootprintController fp)
        {
            FiresLogger.LogInfo($"[EDIT-DBG] TryApplyPendingEdit fp={(fp != null)} set={(_pendingSet != null ? _pendingSet.Count.ToString() : "null")} single={(_pendingEdit != null)}");
            if (fp == null) return false;

            if (_pendingSet != null)
            {
                var cells = _pendingSet; _pendingSet = null;
                var rep = cells[0]; int X = Mathf.Max(1, rep.SetSize.x), Z = Mathf.Max(1, rep.SetSize.y);
                try { ConfigureSetBox(fp, cells, X, Z); } catch (Exception ex) { FiresLogger.LogWarning($"edit-set configure failed: {ex.Message}"); }
                Vector3 pos = fp.transform.position; Quaternion rot = fp.transform.rotation;
                int spawned = 0;
                SectionPlacer.SuppressWearNTear = true;
                try
                {
                    foreach (var c in cells)
                    {
                        if (c == null) continue;
                        var off = SectionPlacer.SetCellOffset(c.SetPos.x, c.SetPos.y, X, Z);
                        spawned += SectionPlacer.Place(c, pos + rot * off, rot).Spawned;
                    }
                }
                finally { SectionPlacer.SuppressWearNTear = false; }
                FiresLogger.LogInfo($"[EDIT-DBG] applied set {X}x{Z} at {pos}: spawned={spawned}");
                Msg($"editing {X}x{Z} set: {spawned} objects. Move/retag, then Save on the box.");
                return true;
            }

            if (_pendingEdit != null)
            {
                var bp = _pendingEdit; _pendingEdit = null;
                try { ConfigureFromSection(fp, bp); } catch (Exception ex) { FiresLogger.LogWarning($"edit configure failed: {ex.Message}"); }
                SectionPlacer.SuppressWearNTear = true;
                int spawned;
                try { spawned = SectionPlacer.Place(bp, fp.transform.position, fp.transform.rotation).Spawned; }
                finally { SectionPlacer.SuppressWearNTear = false; }
                FiresLogger.LogInfo($"[EDIT-DBG] applied single '{bp.SectionId}': spawned={spawned}");
                Msg($"editing '{(string.IsNullOrEmpty(bp.Name) ? bp.SectionId : bp.Name)}': {spawned} objects. Move/retag, then Save on the box.");
                return true;
            }

            return false;
        }

        private static void Msg(string m) => Player.m_localPlayer?.Message(MessageHud.MessageType.Center, "[FiresDungeonMaster] " + m);

        private static void ConfigureFromSection(SectionFootprintController fp, SectionBlueprint bp)
        {
            int cellsX = Mathf.Max(1, bp.Footprint.x);
            int cellsZ = Mathf.Max(1, bp.Footprint.y);
            int stories = Mathf.Max(1, bp.Stories);

            fp.FootprintCellsX = cellsX;
            fp.FootprintCellsZ = cellsZ;
            fp.Stories = stories;
            fp.BoxSize = new Vector3(cellsX * CellMetres, SectionFootprintController.BoxHeightForStories(stories), cellsZ * CellMetres);
            fp.Role = (int)bp.Role;
            fp.DungeonType = bp.DungeonType ?? "";

            fp.EdgeTagN = TagFor(bp.EdgeTags, "north");
            fp.EdgeTagE = TagFor(bp.EdgeTags, "east");
            fp.EdgeTagS = TagFor(bp.EdgeTags, "south");
            fp.EdgeTagW = TagFor(bp.EdgeTags, "west");
            fp.EntranceFace = bp.EntranceFace;

            fp.ApplyConfiguredState();   // persist to ZDO (owner) + rebuild markers / guide walls / snap points
        }

        // The saved edge vocab is lowercase (wall/doorway/open/window/back/left/right); an EMPTY list == Wall.
        private static EdgeTag TagFor(Dictionary<string, List<string>> tags, string face)
        {
            if (tags != null && tags.TryGetValue(face, out var list) && list != null && list.Count > 0)
            {
                switch (list[0].ToLowerInvariant())
                {
                    case "doorway": return EdgeTag.Doorway;
                    case "open":    return EdgeTag.Open;
                    case "window":  return EdgeTag.Window;
                    case "back":    return EdgeTag.Back;
                    case "left":    return EdgeTag.Left;
                    case "right":   return EdgeTag.Right;
                }
            }
            return EdgeTag.Wall;
        }

        private static bool IsAdmin() => FiresDungeonMaster.Editor.SectionFootprintController.IsAdmin();
    }
}
