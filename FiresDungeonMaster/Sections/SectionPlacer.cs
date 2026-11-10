using System;
using FiresCore.Logging;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// SectionPlacer (P4b) - re-instantiates a saved section's objects at a world transform, server-spawned and
    /// persistent, RESTORING each object's full ZDO data (so a pasted chest keeps its inventory, a smelter its
    /// ore, a sign its text). The FiresDungeonMaster equivalent of PlanBuild paste / Infinity Hammer place: for
    /// each captured object resolve the registered ZNetScene prefab, transform its relative pos/rot into world
    /// space at the placement transform, Object.Instantiate (ZNetView.Awake creates the persistent replicated
    /// ZDO inside Instantiate), apply scale, then restore the captured ZDO blob onto the new ZDO.
    ///
    /// SERVER-AUTHORITATIVE: every spawn must create a persistent ZDO, so this runs on the host/server. A remote
    /// admin client routes the place request through the console command's host gate (see DungeonMasterCommands)
    /// - never spawns directly. Dedi-safe: pure ZNetView/ZDO logic, no UI/mesh.
    /// </summary>
    public static class SectionPlacer
    {
        /// <summary>Outcome of a place: spawned count, missing-prefab count, and a message.</summary>
        public struct PlaceResult
        {
            public int Spawned;
            public int Missing;
            public string Message;
        }

        // Set true around an edit-in-box placement so freshly-spawned pieces don't run wear-n-tear / collapse.
        public static bool SuppressWearNTear;

        // When non-null, Place appends each spawned object's ZDO here — so the caller can register the batch with the
        // WorldEdit/InfinityHammer undo stack (see FiresUndoBridge). Null = don't track (normal dungeon generation).
        public static System.Collections.Generic.List<ZDO> CollectSpawned;

        /// <summary>Load a saved section by ID (then by name) and place it at the given world transform.</summary>
        public static PlaceResult PlaceById(string sectionId, Vector3 worldPos, Quaternion worldRot)
        {
            if (string.IsNullOrEmpty(sectionId))
                return new PlaceResult { Message = "no section id." };

            SectionBlueprint bp;
            try
            {
                SectionLibrary lib = FdmPaths.OpenLibrary();
                bp = lib.LoadSectionById(sectionId) ?? lib.LoadSectionByName(sectionId);
            }
            catch (Exception ex)
            {
                FiresLogger.LogError($"load for place failed ({sectionId}): {ex}");
                return new PlaceResult { Message = $"load failed: {ex.Message}" };
            }

            if (bp == null)
                return new PlaceResult { Message = $"section '{sectionId}' not found." };

            return Place(bp, worldPos, worldRot);
        }

        /// <summary>
        /// Spawn every object of bp at the given world transform. Each object's stored pos/rot is relative to the
        /// section origin (set by capture's re-origin): spawnPos = worldPos + worldRot*obj.Pos;
        /// spawnRot = worldRot*obj.Rot. The captured ZDO blob is restored after Instantiate so per-object state
        /// (inventories, fuel, text, tags) reconstructs.
        /// </summary>
        public static PlaceResult Place(SectionBlueprint bp, Vector3 worldPos, Quaternion worldRot)
        {
            if (bp == null || bp.Objects == null || bp.Objects.Count == 0)
                return new PlaceResult { Message = "section is empty." };

            ZNetScene scene = ZNetScene.instance;
            if (scene == null)
                return new PlaceResult { Message = "ZNetScene not ready." };

            int spawned = 0;
            int missing = 0;

            foreach (var obj in bp.Objects)
            {
                if (obj == null) continue;
                if (SectionCenterpoint.Is(obj)) continue;   // centerpoint fallback marker — the mod reconstructs the box, never spawns the prop
                if (obj.Chance < 1f && UnityEngine.Random.value > obj.Chance) continue;

                GameObject prefab = scene.GetPrefab(obj.Prefab);
                if (prefab == null) { missing++; continue; }

                Vector3 spawnPos = worldPos + (worldRot * obj.Pos);
                Quaternion spawnRot = worldRot * obj.Rot;

                try
                {
                    // ZNetView.Awake fires inside Instantiate and creates the persistent replicated ZDO at the
                    // correct pos with a name-resolvable prefab hash - the validated server-auth spawn.
                    GameObject go = UnityEngine.Object.Instantiate(prefab, spawnPos, spawnRot);

                    if (obj.Scale != Vector3.one && obj.Scale != Vector3.zero)
                    {
                        // ZNetView.SetLocalScale writes the ZDO scale (Awake already ran with the prefab's scale, so a
                        // bare transform.localScale would be visual-only and lost on reload / invisible to peers).
                        // AllPiecesScalable (AllScalablePatch) makes every prefab accept it.
                        var scaleNv = go.GetComponent<ZNetView>();
                        if (scaleNv != null && scaleNv.IsValid()) scaleNv.SetLocalScale(obj.Scale);
                        else go.transform.localScale = obj.Scale;
                    }

                    // While EDITING a blueprint in a box, kill wear-n-tear so the structure can't collapse on the admin
                    // (no ground/support at the edit spot). CancelInvoke stops WearNTear's repeating support check.
                    if (SuppressWearNTear)
                    {
                        var wnt = go.GetComponent<WearNTear>();
                        if (wnt != null) { wnt.CancelInvoke(); wnt.enabled = false; }
                    }

                    // Restore the captured ZDO blob into the freshly-created ZDO. We own it right after a
                    // server-side Instantiate, so the high-level ZDO.Set writes bump the data revision and
                    // replicate to peers. This is what makes containers keep their contents on paste.
                    RestoreData(go, obj.Data);
                    Generation.IhPlacement.Apply(go);   // 0.1.180: Infinity Hammer's settings (Core API, soft), after the restore

                    if (CollectSpawned != null)
                    {
                        var nv = go.GetComponent<ZNetView>();
                        if (nv != null && nv.IsValid()) CollectSpawned.Add(nv.GetZDO());
                    }

                    spawned++;
                }
                catch (Exception ex)
                {
                    FiresLogger.LogWarning($"place object '{obj.Prefab}' failed: {ex.Message}");
                }
            }

            FiresLogger.LogInfo($"placed section '{bp.Name}' id={bp.SectionId}: " +
                                $"{spawned} spawned, {missing} missing-prefab.");

            return new PlaceResult
            {
                Spawned = spawned,
                Missing = missing,
                Message = $"placed '{bp.Name}' - {spawned} objects" + (missing > 0 ? $" ({missing} missing)" : "") + "."
            };
        }

        /// <summary>One footprint cell = 8m (matches SectionFootprintController.CellMeters / the bake grid).</summary>
        private const float CellMeters = 8f;

        /// <summary>
        /// Place a whole diced SET as a rigid block: every cell blueprint at its grid offset from the set origin
        /// (worldPos = the set's box floor centre). Cell (i,j) offset = ((i-(X-1)/2)*8, 0, (j-(Z-1)/2)*8) rotated by
        /// worldRot; reuses <see cref="Place"/> per cell (centerpoint skipped, ZDO restored). Server-authoritative.
        /// </summary>
        public static PlaceResult PlaceSet(System.Collections.Generic.List<SectionBlueprint> cells, Vector3 worldPos, Quaternion worldRot)
        {
            if (cells == null || cells.Count == 0) return new PlaceResult { Message = "empty set." };
            var rep = cells[0];
            int X = Mathf.Max(1, rep.SetSize.x), Z = Mathf.Max(1, rep.SetSize.y);

            int spawned = 0, missing = 0;
            foreach (var cell in cells)
            {
                if (cell == null) continue;
                Vector3 offset = SetCellOffset(cell.SetPos.x, cell.SetPos.y, X, Z);
                var r = Place(cell, worldPos + worldRot * offset, worldRot);
                spawned += r.Spawned; missing += r.Missing;
            }

            string msg = $"placed {cells.Count}-room set ({X}x{Z}) — {spawned} objects" + (missing > 0 ? $" ({missing} missing)" : "") + ".";
            FiresLogger.LogInfo($"{msg}");
            return new PlaceResult { Spawned = spawned, Missing = missing, Message = msg };
        }

        /// <summary>Floor-centre offset of set cell (i,j) from the set origin (the box floor centre), in LOCAL space.</summary>
        public static Vector3 SetCellOffset(int i, int j, int cellsX, int cellsZ)
            => new Vector3((i - (cellsX - 1) * 0.5f) * CellMeters, 0f, (j - (cellsZ - 1) * 0.5f) * CellMeters);

        // Restore the captured ZDO blob onto the spawned object's freshly-created ZDO.
        private static void RestoreData(GameObject go, string data)
        {
            if (string.IsNullOrEmpty(data) || go == null) return;
            try
            {
                ZNetView nv = go.GetComponent<ZNetView>();
                if (nv == null || !nv.IsValid()) return;
                ZDO zdo = nv.GetZDO();
                if (zdo == null) return;
                SectionZdoData.Restore(zdo, data);
            }
            catch (Exception ex)
            {
                FiresLogger.LogWarning($"restore ZDO data failed for '{go.name}': {ex.Message}");
            }
        }
    }
}
