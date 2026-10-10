using System.Collections.Generic;
using UnityEngine;

namespace FiresDungeonMaster.Editor
{
    /// <summary>
    /// SectionSnapping — the PURE, DETERMINISTIC edge-to-edge snap solver for P5.
    ///
    /// Vanilla's placement-ghost snap (Player.FindClosestSnapPoints) only TRANSLATES a ghost so a
    /// snap-point coincides with a world snap-point — it never re-orients. The dungeon grid needs the
    /// stronger join the prompt specifies: land a moving section's DOORWAY edge midpoint on a target
    /// section's DOORWAY edge midpoint with the two outward normals ANTI-PARALLEL (so the rooms face
    /// each other across the shared doorway). That is exactly vanilla DungeonGenerator.CalculateRoomPosRot:
    ///   exitRot = targetConn.rotation * Euler(0,180,0);  newRot = exitRot * Inverse(myConn.localRotation);
    /// We reproduce it on the XZ plane (yaw only) so authored sections align on a flat dungeon grid.
    ///
    /// No Unity-scene work, no Random: same inputs -> same pose, every machine. Used by both the
    /// hammer-place ghost override (FootprintSnapController) and programmatic placement (P4 LOAD / P6).
    /// </summary>
    public static class SectionSnapping
    {
        /// <summary>How close a moving doorway midpoint must be to a target doorway midpoint to snap.</summary>
        public const float SnapRadius = 4f;

        /// <summary>A candidate snap target: one doorway edge on a placed footprint, in world space.</summary>
        public struct DoorwayTarget
        {
            public Vector3 WorldMidpoint;   // world position of the target doorway edge midpoint
            public Vector3 WorldNormal;     // world outward normal of that doorway edge (XZ)
            public SectionFootprintController.EdgeTag Tag;             // Doorway or Open
            public int Face;                // target face index (N/E/S/W) — for stable ordering
        }

        /// <summary>A candidate doorway on the MOVING piece, expressed in its local space.</summary>
        public struct MovingDoor
        {
            public Vector3 LocalMidpoint;   // moving piece local-space midpoint
            public Vector3 LocalNormal;     // moving piece local-space outward normal (XZ)
            public int Face;                // moving face index — for stable ordering
        }

        /// <summary>Result of solving a snap: the pose the moving piece must take.</summary>
        public struct SnapResult
        {
            public bool Snapped;
            public Vector3 Position;
            public Quaternion Rotation;
            public int MovingFace;          // which moving doorway joined
            public int TargetFace;          // which target doorway it joined to
        }

        // ---- world-space doorway collection ----------------------------------------------------

        /// <summary>Collect every doorway edge (world midpoint+normal) from a placed footprint.</summary>
        public static void CollectDoorways(SectionFootprintController fp, List<DoorwayTarget> outList)
        {
            if (fp == null) return;
            for (int face = 0; face < SectionFootprintController.FaceCount; face++)
            {
                SectionFootprintController.EdgeTag tag = fp.GetEdgeTag(face);
                if (!SectionFootprintController.IsDoorway(tag)) continue;
                outList.Add(new DoorwayTarget
                {
                    WorldMidpoint = fp.EdgeWorldMidpoint(face),
                    WorldNormal = Flatten(fp.EdgeWorldNormal(face)),
                    Tag = tag,
                    Face = face,
                });
            }
        }

        /// <summary>Local doorway descriptors (midpoint+normal) for the moving footprint.</summary>
        public static List<MovingDoor> MovingDoorways(SectionFootprintController fp)
        {
            var list = new List<MovingDoor>();
            if (fp == null) return list;
            for (int face = 0; face < SectionFootprintController.FaceCount; face++)
            {
                if (!fp.IsDoorwayFace(face)) continue;
                list.Add(new MovingDoor
                {
                    LocalMidpoint = fp.EdgeLocalMidpoint(face),
                    LocalNormal = Flatten(SectionFootprintController.EdgeLocalNormal(face)),
                    Face = face,
                });
            }
            return list;
        }

        // ---- the solver ------------------------------------------------------------------------

        /// <summary>
        /// Given the moving piece's current desired pose (the cursor pose), its doorways, and the set of
        /// world doorway targets on nearby placed footprints, find the single best deterministic snap.
        ///
        /// "Best" is decided WITHOUT randomness: among all (movingDoor, target) pairs whose snapped
        /// position would be within SnapRadius of the cursor, pick the smallest distance; ties broken by
        /// (targetFace, movingFace) ascending. The returned pose makes movingDoor's midpoint coincide with
        /// target's midpoint and movingDoor's outward normal exactly oppose target's outward normal.
        /// </summary>
        public static SnapResult Solve(
            Vector3 desiredPos,
            Quaternion desiredRot,
            List<MovingDoor> movingDoors,
            List<DoorwayTarget> targets)
        {
            var result = new SnapResult { Snapped = false, Position = desiredPos, Rotation = desiredRot };
            if (movingDoors == null || movingDoors.Count == 0 || targets == null || targets.Count == 0)
                return result;

            float bestDist = float.MaxValue;
            int bestTFace = int.MaxValue, bestMFace = int.MaxValue;

            foreach (var t in targets)
            {
                foreach (var m in movingDoors)
                {
                    // Yaw that makes the chosen moving normal anti-parallel to the target normal.
                    Quaternion rot = SolveRotation(t.WorldNormal, m.LocalNormal);
                    Vector3 pos = SolvePosition(rot, m.LocalMidpoint, t.WorldMidpoint);

                    // Rank by how far the snapped position moves the piece from the cursor; the join is
                    // exact by construction, this only chooses the nearest candidate deterministically.
                    float d = Vector3.Distance(desiredPos, pos);
                    if (d > SnapRadius) continue;

                    bool better = d < bestDist - 1e-4f;
                    bool tie = Mathf.Abs(d - bestDist) <= 1e-4f &&
                               (t.Face < bestTFace || (t.Face == bestTFace && m.Face < bestMFace));
                    if (better || tie)
                    {
                        bestDist = d;
                        bestTFace = t.Face;
                        bestMFace = m.Face;
                        result.Snapped = true;
                        result.Position = pos;
                        result.Rotation = rot;
                        result.MovingFace = m.Face;
                        result.TargetFace = t.Face;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// Flat-grid yaw that makes the moving piece's local doorway normal point exactly OPPOSITE the
        /// target's world doorway normal. Computed as pure heading difference on the XZ plane so the
        /// result is ALWAYS a clean yaw (no off-grid tilt), including the 180°-opposed back-to-back case
        /// where FromToRotation would otherwise pick an arbitrary perpendicular axis. Matches vanilla
        /// DungeonGenerator.CalculateRoomPosRot's flat exitRot*Euler(0,180,0) connection alignment.
        /// </summary>
        public static Quaternion SolveRotation(Vector3 targetWorldNormal, Vector3 movingLocalNormal)
        {
            Vector3 want = Flatten(-targetWorldNormal);   // the direction the moving normal must end up facing
            Vector3 have = Flatten(movingLocalNormal);
            if (want.sqrMagnitude < 1e-6f || have.sqrMagnitude < 1e-6f)
                return Quaternion.identity;
            float targetYaw = Mathf.Atan2(want.x, want.z) * Mathf.Rad2Deg;
            float movingYaw = Mathf.Atan2(have.x, have.z) * Mathf.Rad2Deg;
            return Quaternion.Euler(0f, targetYaw - movingYaw, 0f);
        }

        /// <summary>World position so that rot*localMidpoint lands on targetWorldMidpoint (origin solve).</summary>
        public static Vector3 SolvePosition(Quaternion rot, Vector3 localMidpoint, Vector3 targetWorldMidpoint)
        {
            return targetWorldMidpoint - (rot * localMidpoint);
        }

        /// <summary>Drop the Y component (snapping is on the XZ dungeon plane); keep magnitude on XZ.</summary>
        private static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);
    }
}
