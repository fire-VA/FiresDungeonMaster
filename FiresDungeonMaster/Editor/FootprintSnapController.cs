using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// FootprintSnapController — P5 hammer-place snap.
    ///
    /// Vanilla's ghost snap (Player.FindClosestSnapPoints) only TRANSLATES the ghost onto a matching
    /// 'snappoint' child. SectionFootprintController.RebuildSnapPoints emits those children (DIRECT children
    /// of the footprint, the only ones vanilla Piece.GetSnapPoints reads) for doorway edges so that base
    /// translational assist still works, then this Player.UpdatePlacementGhost POSTFIX upgrades the join to
    /// a full DETERMINISTIC doorway alignment (position + rotation, opposing normals) whenever the build
    /// ghost is a FiresDungeonFootprint near another placed footprint's compatible doorway. Solver is
    /// SectionSnapping (pure, no Random).
    ///
    /// CLIENT-ONLY: this whole patch class is added to the plugin's DedicatedServerSkipPatchTypes (Player and
    /// the placement ghost do not exist headless). The IsClientOnly() guard is belt-and-suspenders.
    /// NOTE: NO `using FiresDungeonMaster.Sections;` here on purpose — it would drag in
    /// FiresDungeonMaster.Sections.Piece and make every bare `Piece` (Valheim) ambiguous (CS0104). The one
    /// SectionBlueprint reference below is fully qualified instead.
    /// </summary>
    [HarmonyPatch(typeof(Player), "UpdatePlacementGhost")]
    public static class FootprintSnapController
    {
        // Reused scratch lists (single-threaded Unity main-thread use). Bare Piece = Valheim's Piece.
        private static readonly List<Piece> s_nearPieces = new List<Piece>();
        private static readonly List<SectionSnapping.DoorwayTarget> s_targets = new List<SectionSnapping.DoorwayTarget>();

        // Search radius for nearby placed footprints whose doorways we can snap to.
        private const float NeighborSearchRadius = 24f;

        private static bool IsClientOnly()
            => SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;

        // 0.1.178: runs AFTER Core's ring-ghost postfix (RingPlacement, default priority), which tilts the ghost by the ring's up every frame;
        // the snap's rotation is already upright to the ring (SectionSnapping 0.1.177), so it must be the last word, not tilted twice
        [HarmonyPostfix, HarmonyPriority(Priority.Low)]
        private static void Postfix(Player __instance)
        {
            if (IsClientOnly()) return;
            try { ApplyDoorwaySnap(__instance); }
            catch (Exception ex) { Debug.LogWarning($"[FiresDungeonMaster] footprint snap skipped: {ex.Message}"); }
        }

        private static void ApplyDoorwaySnap(Player player)
        {
            if (player == null) return;
            GameObject ghost = player.m_placementGhost;
            if (ghost == null || !ghost.activeSelf) return;

            // Only act on OUR footprint ghost.
            var movingFp = ghost.GetComponent<SectionFootprintController>();
            if (movingFp == null) return;

            Vector3 desiredPos = ghost.transform.position;
            Quaternion desiredRot = ghost.transform.rotation;

            var movingDoors = SectionSnapping.MovingDoorways(movingFp);
            if (movingDoors.Count == 0) return; // no doorways authored on the moving footprint

            // Gather doorway targets from nearby PLACED footprints. The ghost is on the ghost layer and is
            // skipped by Piece.GetAllPiecesInRadius, so it never appears among placed Pieces.
            s_targets.Clear();
            s_nearPieces.Clear();
            Piece.GetAllPiecesInRadius(desiredPos, NeighborSearchRadius, s_nearPieces);
            for (int i = 0; i < s_nearPieces.Count; i++)
            {
                var p = s_nearPieces[i];
                if (p == null) continue;
                var fp = p.GetComponent<SectionFootprintController>();
                if (fp == null || fp == movingFp) continue;
                SectionSnapping.CollectDoorways(fp, s_targets);
            }
            if (s_targets.Count == 0) return;

            var solved = SectionSnapping.Solve(desiredPos, desiredRot, movingDoors, s_targets);
            if (!solved.Snapped) return;

            ghost.transform.position = solved.Position;
            ghost.transform.rotation = solved.Rotation;
        }

        // -------------------------------------------------------------------------------------------
        // Programmatic snap for P4 LOAD / P6 (non-hammer): snap a section blueprint's chosen doorway to a
        // target footprint's doorway and return the world pose to paste at. Deterministic, no scene churn
        // beyond reading the target footprints. Used when 'snapToNeighbors' is requested.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Compute the world pose to paste a SectionBlueprint so its nearest compatible doorway edge joins a
        /// doorway among <paramref name="placedFootprints"/>. Returns (cursorPos, cursorRot) unchanged if
        /// nothing snaps. The blueprint's footprint cell size drives the doorway midpoints
        /// (Footprint.x/Footprint.y cells * cellSize), so it must match the editor footprint scale.
        /// </summary>
        public static void SnapBlueprintToWorld(
            FiresDungeonMaster.Sections.SectionBlueprint bp,
            float cellSize,
            float storyHeight,
            Vector3 cursorPos,
            Quaternion cursorRot,
            IEnumerable<SectionFootprintController> placedFootprints,
            out Vector3 pos,
            out Quaternion rot)
        {
            pos = cursorPos;
            rot = cursorRot;
            if (bp == null || placedFootprints == null) return;

            // Build moving doorways from the blueprint's EdgeTags (faces north/east/south/west).
            var box = new Vector3(
                Mathf.Max(1, bp.Footprint.x) * cellSize,
                Mathf.Max(1, bp.Stories) * storyHeight,
                Mathf.Max(1, bp.Footprint.y) * cellSize);
            Vector3 half = box / 2f;

            var movingDoors = new List<SectionSnapping.MovingDoor>();
            for (int face = 0; face < SectionFootprintController.FaceCount; face++)
            {
                string key = SectionFootprintController.FaceNames[face];
                if (!bp.EdgeTags.TryGetValue(key, out var tags) || tags == null) continue;
                bool doorway = tags.Contains("doorway") || tags.Contains("open");
                if (!doorway) continue;

                Vector3 mid, nrm;
                LocalEdge(face, half, out mid, out nrm);
                movingDoors.Add(new SectionSnapping.MovingDoor { LocalMidpoint = mid, LocalNormal = nrm, Face = face });
            }
            if (movingDoors.Count == 0) return;

            var targets = new List<SectionSnapping.DoorwayTarget>();
            foreach (var fp in placedFootprints)
                SectionSnapping.CollectDoorways(fp, targets);
            if (targets.Count == 0) return;

            var solved = SectionSnapping.Solve(cursorPos, cursorRot, movingDoors, targets);
            if (!solved.Snapped) return;
            pos = solved.Position;
            rot = solved.Rotation;
        }

        // Local edge midpoint + outward normal for a face, given the box half-extents. Mirrors
        // SectionFootprintController.EdgeLocalMidpoint/EdgeLocalNormal but parameterized by 'half' so the
        // blueprint path needs no live controller.
        private static void LocalEdge(int face, Vector3 half, out Vector3 mid, out Vector3 nrm)
        {
            switch (face)
            {
                case 0: mid = new Vector3(0f, 0f,  half.z); nrm = Vector3.forward; break; // north
                case 1: mid = new Vector3( half.x, 0f, 0f);  nrm = Vector3.right;   break; // east
                case 2: mid = new Vector3(0f, 0f, -half.z); nrm = Vector3.back;    break; // south
                default: mid = new Vector3(-half.x, 0f, 0f);  nrm = Vector3.left;   break; // west
            }
        }
    }
}
