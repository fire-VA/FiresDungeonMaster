using UnityEngine;
using FiresCore.UI.ContextMenu;

namespace FiresDungeonMaster.Editor
{
    using MarkerHit = SectionFootprintController.MarkerHit;

    /// <summary>
    /// Payload carried on a ContextTarget when the cursor ray lands on one of a footprint's five spheres. Mirrors
    /// PatrolNodeMarker: it names the footprint and which sphere so the claim can open the right editor.
    /// </summary>
    public sealed class FootprintMarker
    {
        public SectionFootprintController Footprint;
        public MarkerHit Marker;
    }

    /// <summary>
    /// Aim a footprint sphere FROM A DISTANCE and Alt+Shift right-click to open the dedicated Section editor
    /// window (<see cref="FiresDungeonMaster.UI.SectionEditorPanel"/>). Uses the proven patrol-node input path:
    /// the Core FiresContextMenuDriver frees the cursor + raycasts on Alt+Shift right-click, resolving a target
    /// two ways (kept nearest):
    ///   (A) Physics.RaycastAll on RayMask (which INCLUDES 'piece_nonsolid') — the footprint body is a SOLID box
    ///       on that layer, so the driver's ray hits it directly and resolves to this controller.
    ///   (B) our ray-vs-sphere <see cref="HitTest"/> marker source — a precise sphere pick, collider-less like patrol.
    ///
    /// Rather than contribute rows to the SHARED parchment list (which concatenates every provider's rows — the
    /// companion "Move here" clutter), a footprint sphere CLAIMS the target (FiresContextMenu.RegisterClaim): the
    /// claim opens the proper editor window and the shared list is suppressed. Two independent resolve paths mean
    /// the window opens whether the marker source or the box-body physics hit wins the driver's tie-break.
    ///
    /// Registered lazily the first time a real (non-ghost) footprint spawns (SectionFootprintController.Awake).
    /// Client-only (the driver no-ops headless; SectionFootprintController.Active is only populated on the client).
    /// </summary>
    public static class SectionFootprintContextProvider
    {
        private static bool _registered;
        private static FiresContextMenu.MarkerHitTester _tester;
        private static FiresContextMenu.TargetClaim _claim;

        /// <summary>Register the ray-vs-sphere marker source + the target claim once (idempotent). Client-only.</summary>
        public static void EnsureRegistered()
        {
            if (_registered) return;
            if (SystemInfo.graphicsDeviceType == UnityEngine.Rendering.GraphicsDeviceType.Null) return; // headless
            _registered = true;
            _tester = HitTest;
            FiresContextMenu.RegisterMarkerSource(_tester);
            _claim = HandleClaim;
            FiresContextMenu.RegisterClaim(_claim);

            // Block the Core companion whistle (Shift+RightClick) while the player is in the dungeon staging area, so a
            // Shift/Alt+Shift right-click near a box edits it instead of summoning a companion into the build.
            FiresCore.Npc.Commands.CompanionCommandCoordinator.WhistleSuppressor = SectionFootprintController.IsPlayerInStagingArea;

            FiresLogger.LogDebug("[FOOTPRINT-CTX] marker source + editor claim + whistle suppressor registered (Alt+Shift right-click a sphere).");
        }

        // Marker source: ray-test the five spheres of every live footprint (PatrolRouteVisualizer.HitTest scaled to
        // N footprints). Nearest sphere across all footprints wins; RaycastMarker already biases the hit slightly
        // closer so a sphere beats the co-located solid footprint box wall in the driver's strict-'<' compare.
        private static bool HitTest(Ray ray, out ContextTarget target)
        {
            target = null;
            float best = float.MaxValue;
            SectionFootprintController bestFp = null;
            MarkerHit bestMarker = MarkerHit.None;

            var active = SectionFootprintController.Active;
            for (int i = 0; i < active.Count; i++)
            {
                var fp = active[i];
                if (fp == null) continue;
                MarkerHit hit = fp.RaycastMarker(ray, out float d);
                if (hit != MarkerHit.None && d < best) { best = d; bestFp = fp; bestMarker = hit; }
            }

            if (bestFp == null)
            {
                Dbg($"marker source: no orb hit (activeFootprints={active.Count})");
                return false;
            }
            Dbg($"marker source: orb HIT fp='{bestFp.name}' marker={bestMarker} dist={best:F1}m");

            Vector3 world = bestMarker == MarkerHit.Center ? bestFp.CenterMarkerWorld()
                          : bestFp.EdgeMarkerWorld(FaceIndex(bestMarker));
            target = new ContextTarget
            {
                Point = world,
                Distance = best,
                Marker = new FootprintMarker { Footprint = bestFp, Marker = bestMarker },
                MarkerKind = "Footprint",
            };
            return true;
        }

        // The claim: if this target is a footprint sphere (via marker source OR a physics hit on the box body
        // mapped to the nearest sphere), open the editor window and return true so the shared row list is skipped.
        private static bool HandleClaim(ContextTarget target, Vector2 screenPos)
        {
            var (fp, marker) = Resolve(target);
            if (fp == null)
            {
                Dbg($"claim DECLINED -> the shared (companion) menu will show. targetKind={target?.MarkerKind ?? "null"} " +
                    $"go={(target?.GameObject != null ? target.GameObject.name : "null")} point={target?.Point} activeFootprints={SectionFootprintController.Active.Count}");
                return false;
            }
            Dbg($"claim ACCEPTED fp='{fp.name}' marker={marker} -> opening notebook editor");
            FiresDungeonMaster.UI.NotebookEditorPanel.Open(fp, marker);
            return true;
        }

        private static void Dbg(string m) { if (SectionGuideWalls.Verbose) FiresLogger.LogDebug("[FOOTPRINT-CTX-DBG] " + m); }

        // How far OUTSIDE a footprint box a right-click still counts as "the dungeon's context" (metres). Keeps a
        // click on the ground right at the box edge on the editor, not on the companion "move here" provider. Small
        // so it never suppresses companion commands well away from a dungeon.
        private const float ClaimMargin = 1.5f;

        // Resolve which footprint + sphere this target is. A context menu should reflect WHAT you're pointing at, so
        // a footprint wins in three cases, most-specific first:
        //   1. the ray-vs-sphere marker source (MarkerKind "Footprint", carries a FootprintMarker) — a precise sphere;
        //   2. a physics hit on the footprint's own solid box collider ('piece_nonsolid') — mapped to the nearest sphere;
        //   3. the hit POINT lands inside (or just around) a footprint box — even if the nearest collider was a
        //      companion milling inside the box or the terrain floor. This is what stops the companion "move here"
        //      menu from popping while you build inside/around a section (the claim returns true -> shared list skipped).
        // (null, None) only when the click is nowhere near any footprint.
        private static (SectionFootprintController fp, MarkerHit marker) Resolve(ContextTarget target)
        {
            if (target == null) return (null, MarkerHit.None);

            if (target.MarkerKind == "Footprint" && target.Marker is FootprintMarker fm && fm.Footprint != null)
            {
                Dbg($"resolve: via MARKER SOURCE -> fp='{fm.Footprint.name}' marker={fm.Marker}");
                return (fm.Footprint, fm.Marker);
            }

            var fp = target.GameObject != null ? target.GameObject.GetComponentInParent<SectionFootprintController>() : null;
            if (fp == null && target.Collider != null)
                fp = target.Collider.GetComponentInParent<SectionFootprintController>();
            if (fp != null)
            {
                Dbg($"resolve: via PHYSICS hit on '{(target.GameObject != null ? target.GameObject.name : "?")}' -> fp='{fp.name}' nearest={fp.NearestMarkerTo(target.Point)}");
                return (fp, fp.NearestMarkerTo(target.Point));
            }

            var active = SectionFootprintController.Active;
            for (int i = 0; i < active.Count; i++)
            {
                var box = active[i];
                if (box == null) continue;
                bool inside = box.ContainsPoint(target.Point, ClaimMargin);
                Dbg($"resolve: CONTAINSPOINT fp='{box.name}' boxWorldPos={box.transform.position} box={box.BoxSize} point={target.Point} inside={inside}");
                if (inside) return (box, box.NearestMarkerTo(target.Point));
            }
            return (null, MarkerHit.None);
        }

        // MarkerHit N/E/S/W -> the 0..3 face index EdgeMarkerWorld uses.
        private static int FaceIndex(MarkerHit hit)
        {
            switch (hit)
            {
                case MarkerHit.North: return 0;
                case MarkerHit.East:  return 1;
                case MarkerHit.South: return 2;
                default:              return 3; // West
            }
        }
    }
}
