using System;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// The blueprint CENTERPOINT — a real, vanilla fallback prefab written at a saved section's local origin so the
    /// .blueprint stays universally placeable. A FiresDungeonMaster section is captured with every interior piece
    /// stored RELATIVE to the footprint box (the box IS the origin), so the box already acts as the centerpoint —
    /// but nothing sits at that origin, and the box's own prefab (FiresDungeonFootprint) is mod-only. Without the
    /// mod, Expand World / Infinity Hammer / PlanBuild would have no valid object to build the section around.
    ///
    /// So on save we drop one marker piece at local (0,0,0): a harmless vanilla prop (a yellow/glowing mushroom by
    /// default, configurable — see <see cref="DungeonMasterConfig"/>), tagged <see cref="Marker"/> in its extraInfo
    /// column and declared via the standard <c>#center:</c> header. Behaviour splits cleanly:
    ///   - WITH FiresDungeonMaster: the marker is skipped as content everywhere the mod spawns pieces (place /
    ///     generate / edit-in-box) — the footprint box reconstructs it, so the prop never materialises.
    ///   - WITHOUT the mod: the marker is just another piece, so a real mushroom sits at the centerpoint and every
    ///     other object is relative to it — the section stays a valid, placeable blueprint.
    ///
    /// The extraInfo column round-trips losslessly through the IH/PlanBuild format (col 9), so the marker survives
    /// a save/edit/re-save through any world-edit tool.
    /// </summary>
    public static class SectionCenterpoint
    {
        /// <summary>Written to a centerpoint piece's extraInfo (piece-line col 9). Identifies the fallback marker.</summary>
        public const string Marker = "fdm_center";

        /// <summary>Build the centerpoint piece: the fallback prefab at local origin, identity rotation, marked.</summary>
        public static Piece Make(string prefab) => new Piece
        {
            Prefab = string.IsNullOrWhiteSpace(prefab) ? "Pickable_Mushroom_yellow" : prefab.Trim(),
            Pos = Vector3.zero,
            Rot = Quaternion.identity,
            Scale = Vector3.one,
            ExtraInfo = Marker,
            Data = "",
            Chance = 1f,
        };

        /// <summary>True when this piece is the centerpoint fallback marker (skip it wherever the mod spawns content).</summary>
        public static bool Is(Piece p) => p != null && string.Equals(p.ExtraInfo, Marker, StringComparison.Ordinal);
    }
}
